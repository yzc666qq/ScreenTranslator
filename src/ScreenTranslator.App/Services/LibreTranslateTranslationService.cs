using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public sealed class LibreTranslateTranslationService(HttpClient httpClient) : ITranslationService
{
    private const int MaximumCachedLines = 512;
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ManagedRuntimeRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly Regex LineSeparatorPattern = new(
        "(\\r\\n|\\n|\\r)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<CacheKey, string> _translationCache = [];
    private Uri? _translateEndpoint;
    private Uri? _languagesEndpoint;
    private bool _isManagedRuntime;

    public void Configure(Uri endpoint, bool isManagedRuntime = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("本地翻译地址必须是完整的 HTTP 或 HTTPS 地址。", nameof(endpoint));
        }

        var translateEndpoint = BuildActionEndpoint(endpoint, "translate");
        var languagesEndpoint = BuildActionEndpoint(endpoint, "languages");

        if (_translateEndpoint != translateEndpoint)
        {
            _translationCache.Clear();
            _translateEndpoint = translateEndpoint;
        }

        _languagesEndpoint = languagesEndpoint;
        _isManagedRuntime = isManagedRuntime;
    }

    public async Task<TranslationServiceReadiness> CheckReadinessAsync(
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var endpoint = _languagesEndpoint
            ?? throw new InvalidOperationException("尚未配置本地离线翻译服务。");
        var normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage, allowAuto: false);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(ReadinessTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(request, timeoutCancellation.Token);
        }
        catch (HttpRequestException)
        {
            return Unavailable(
                $"无法连接 {endpoint.GetLeftPart(UriPartial.Authority)}。" +
                GetUnavailableAction());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(
                $"连接 {endpoint.GetLeftPart(UriPartial.Authority)} 超时。" +
                GetTimeoutAction());
        }

        using (response)
        {
            string responseBody;

            try
            {
                responseBody = await response.Content.ReadAsStringAsync(timeoutCancellation.Token);
            }
            catch (HttpRequestException)
            {
                return Unavailable("读取 LibreTranslate 语言列表失败，请确认服务仍在运行。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Unavailable(
                    $"读取 {endpoint.GetLeftPart(UriPartial.Authority)} 的语言列表超时。" +
                    "请确认语言模型已加载完成。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(
                    $"LibreTranslate 的 /languages 接口返回 {(int)response.StatusCode}: " +
                    GetErrorSummary(responseBody));
            }

            try
            {
                using var document = JsonDocument.Parse(responseBody);

                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return Unavailable("LibreTranslate 返回的语言列表格式无效，请检查服务版本或接口地址。");
                }

                var supportedLanguages = document.RootElement
                    .EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.Object &&
                                    item.TryGetProperty("code", out var code) &&
                                    code.ValueKind == JsonValueKind.String
                        ? code.GetString()
                        : null)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => NormalizeLanguageCode(code!, allowAuto: false))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var supportedTargets = document.RootElement
                    .EnumerateArray()
                    .SelectMany(GetSupportedTargets)
                    .Concat(supportedLanguages)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (supportedLanguages.Length == 0)
                {
                    return Unavailable(
                        "LibreTranslate 返回的语言列表不包含有效语言，请检查模型是否加载完成。");
                }

                if (!supportedTargets.Contains(normalizedTargetLanguage))
                {
                    return new TranslationServiceReadiness(
                        TranslationServiceReadinessState.TargetLanguageUnavailable,
                        $"当前服务未加载目标语言“{normalizedTargetLanguage}”。" +
                        $"已加载：{string.Join("、", supportedLanguages)}。" +
                        "请安装对应 Argos 模型并重启服务。",
                        supportedLanguages);
                }

                return new TranslationServiceReadiness(
                    TranslationServiceReadinessState.Ready,
                    $"LibreTranslate 已就绪，目标语言：{normalizedTargetLanguage}",
                    supportedLanguages);
            }
            catch (JsonException)
            {
                return Unavailable("LibreTranslate 返回的语言列表不是有效 JSON，请检查服务版本或接口地址。");
            }
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        string? sourceLanguageHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var endpoint = _translateEndpoint
            ?? throw new InvalidOperationException("尚未配置本地离线翻译服务。");
        _ = sourceLanguageHint;

        var normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage, allowAuto: true);
        var normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage, allowAuto: false);
        var segments = LineSeparatorPattern.Split(text);
        var contents = segments
            .Where(segment => !IsLineSeparator(segment))
            .Select(SplitLine)
            .Where(line => line.Content.Length > 0)
            .Select(line => line.Content)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingContents = new List<string>();

        foreach (var content in contents)
        {
            var key = new CacheKey(normalizedSourceLanguage, normalizedTargetLanguage, content);

            if (_translationCache.TryGetValue(key, out var cachedTranslation))
            {
                translations[content] = cachedTranslation;
            }
            else
            {
                missingContents.Add(content);
            }
        }

        if (missingContents.Count > 0)
        {
            var translatedContents = await TranslateBatchAsync(
                endpoint,
                missingContents,
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                cancellationToken);

            if (_translationCache.Count + translatedContents.Count > MaximumCachedLines)
            {
                _translationCache.Clear();
            }

            for (var index = 0; index < missingContents.Count; index++)
            {
                var content = missingContents[index];
                var translatedContent = translatedContents[index];
                translations[content] = translatedContent;

                if (_translationCache.Count < MaximumCachedLines)
                {
                    _translationCache[new CacheKey(
                        normalizedSourceLanguage,
                        normalizedTargetLanguage,
                        content)] = translatedContent;
                }
            }
        }

        var builder = new StringBuilder(text.Length);

        foreach (var segment in segments)
        {
            if (IsLineSeparator(segment))
            {
                builder.Append(segment);
                continue;
            }

            var line = SplitLine(segment);
            builder.Append(line.Prefix);

            if (line.Content.Length > 0)
            {
                builder.Append(translations[line.Content]);
            }

            builder.Append(line.Suffix);
        }

        return new TranslationResult(
            text,
            builder.ToString(),
            normalizedSourceLanguage,
            normalizedTargetLanguage);
    }

    private async Task<IReadOnlyList<string>> TranslateBatchAsync(
        Uri endpoint,
        IReadOnlyList<string> contents,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            q = contents,
            source = sourceLanguage,
            target = targetLanguage,
            format = "text"
        };

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json")
            };

            HttpResponseMessage response;

            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new InvalidOperationException(
                    $"无法连接本地翻译服务 {endpoint.GetLeftPart(UriPartial.Authority)}。" +
                    GetUnavailableAction(),
                    exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("本地翻译服务响应超时，请确认语言模型已经加载完成。", exception);
            }

            using (response)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if (_isManagedRuntime &&
                        attempt == 0 &&
                        (int)response.StatusCode is >= 500 and <= 599)
                    {
                        await Task.Delay(ManagedRuntimeRetryDelay, cancellationToken);
                        continue;
                    }

                    throw new HttpRequestException(
                        $"本地翻译服务返回 {(int)response.StatusCode}: {GetErrorSummary(responseBody)}");
                }

                using var document = JsonDocument.Parse(responseBody);

                if (!document.RootElement.TryGetProperty("translatedText", out var translatedText))
                {
                    throw new InvalidOperationException("本地翻译服务未返回 translatedText。");
                }

                if (translatedText.ValueKind == JsonValueKind.String && contents.Count == 1)
                {
                    var result = translatedText.GetString();

                    if (string.IsNullOrWhiteSpace(result))
                    {
                        throw new InvalidOperationException("本地翻译服务返回了空译文。");
                    }

                    return [result];
                }

                if (translatedText.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("本地翻译服务返回了无法识别的译文格式。");
                }

                var results = translatedText
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray();

                if (results.Length != contents.Count || results.Any(string.IsNullOrWhiteSpace))
                {
                    throw new InvalidOperationException("本地翻译服务返回的译文数量或内容无效。");
                }

                return results;
            }
        }
    }

    private static Uri BuildActionEndpoint(Uri endpoint, string action)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');

        if (path.EndsWith("/translate", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/translate".Length];
        }

        builder.Path = $"{path}/{action}";
        return builder.Uri;
    }

    private static IEnumerable<string> GetSupportedTargets(JsonElement language)
    {
        if (language.ValueKind != JsonValueKind.Object ||
            !language.TryGetProperty("targets", out var targets) ||
            targets.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var target in targets.EnumerateArray())
        {
            if (target.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(target.GetString()))
            {
                yield return NormalizeLanguageCode(target.GetString()!, allowAuto: false);
            }
        }
    }

    private static TranslationServiceReadiness Unavailable(string message) =>
        new(
            TranslationServiceReadinessState.ServiceUnavailable,
            message,
            Array.Empty<string>());

    private string GetUnavailableAction() =>
        _isManagedRuntime
            ? "内置 LibreTranslate 可能已意外停止，请停止翻译后重新开始。"
            : "请先启动 LibreTranslate，再重新点击“开始实时翻译”。";

    private string GetTimeoutAction() =>
        _isManagedRuntime
            ? "内置 LibreTranslate 正在准备语言模型，请稍后重试。"
            : "请确认 LibreTranslate 已启动且语言模型已加载完成。";

    private static string NormalizeLanguageCode(string language, bool allowAuto)
    {
        var normalized = language.Trim();

        if (allowAuto && normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        var separatorIndex = normalized.IndexOfAny(['-', '_']);
        return (separatorIndex > 0 ? normalized[..separatorIndex] : normalized).ToLowerInvariant();
    }

    private static bool IsLineSeparator(string value) =>
        value is "\r\n" or "\n" or "\r";

    private static LineParts SplitLine(string line)
    {
        var contentStart = 0;

        while (contentStart < line.Length && char.IsWhiteSpace(line[contentStart]))
        {
            contentStart++;
        }

        var contentEnd = line.Length;

        while (contentEnd > contentStart && char.IsWhiteSpace(line[contentEnd - 1]))
        {
            contentEnd--;
        }

        return new LineParts(
            line[..contentStart],
            line[contentStart..contentEnd],
            line[contentEnd..]);
    }

    private static string GetErrorSummary(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? "未知错误";
            }
        }
        catch (JsonException)
        {
            // Fall back to a bounded plain-text response below.
        }

        return responseBody.Length > 300 ? responseBody[..300] + "…" : responseBody;
    }

    private sealed record LineParts(string Prefix, string Content, string Suffix);

    private sealed record CacheKey(string SourceLanguage, string TargetLanguage, string Text);
}
