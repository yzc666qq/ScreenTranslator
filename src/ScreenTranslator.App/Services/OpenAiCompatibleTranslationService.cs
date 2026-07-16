using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public sealed class OpenAiCompatibleTranslationService(HttpClient httpClient) : ITranslationService
{
    private TranslationProviderOptions? _options;

    public void Configure(TranslationProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);

        if (!options.Endpoint.IsAbsoluteUri ||
            (options.Endpoint.Scheme != Uri.UriSchemeHttps && options.Endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("翻译接口必须是完整的 HTTP 或 HTTPS 地址。", nameof(options));
        }

        _options = options;
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var options = _options
            ?? throw new InvalidOperationException("尚未配置翻译服务。 ");

        var sourceDescription = sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "自动识别的源语言"
            : sourceLanguage;

        var messages = new object[]
        {
            new
            {
                role = "system",
                content = $"你是实时屏幕翻译器。把{sourceDescription}翻译为 {targetLanguage}。严格保持原文的换行、空行、相对缩进、编号和标点结构；不要合并或拆分行。只输出译文，不要解释，不要添加 Markdown 代码块。"
            },
            new { role = "user", content = text }
        };

        object requestBody = options.Endpoint.Host.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? new
            {
                model = options.Model,
                messages,
                stream = false,
                thinking = new { type = "disabled" }
            }
            : new
            {
                model = options.Model,
                messages,
                stream = false
            };

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var summary = responseBody.Length > 300 ? responseBody[..300] + "…" : responseBody;
            throw new HttpRequestException($"翻译接口返回 {(int)response.StatusCode}: {summary}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var translatedText = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(translatedText))
        {
            throw new InvalidOperationException("翻译接口未返回有效译文。 ");
        }

        return new TranslationResult(text, translatedText, sourceLanguage, targetLanguage);
    }
}
