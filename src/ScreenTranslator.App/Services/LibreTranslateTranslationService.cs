using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public sealed class LibreTranslateTranslationService(HttpClient httpClient) : ITranslationService
{
    private const int MaximumCachedLines = 512;
    private static readonly Regex LineSeparatorPattern = new(
        "(\\r\\n|\\n|\\r)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<CacheKey, string> _translationCache = [];
    private Uri? _translateEndpoint;

    public void Configure(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("本地翻译地址必须是完整的 HTTP 或 HTTPS 地址。", nameof(endpoint));
        }

        var translateEndpoint = BuildTranslateEndpoint(endpoint);

        if (_translateEndpoint != translateEndpoint)
        {
            _translationCache.Clear();
            _translateEndpoint = translateEndpoint;
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
                $"无法连接本地翻译服务 {endpoint.GetLeftPart(UriPartial.Authority)}。请先启动 LibreTranslate。",
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

    private static Uri BuildTranslateEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');

        if (!path.EndsWith("/translate", StringComparison.OrdinalIgnoreCase))
        {
            path += "/translate";
        }

        builder.Path = path;
        return builder.Uri;
    }

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
