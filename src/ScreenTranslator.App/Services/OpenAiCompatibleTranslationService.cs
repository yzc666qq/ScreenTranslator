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
        CancellationToken cancellationToken = default,
        string? sourceLanguageHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var options = _options
            ?? throw new InvalidOperationException("尚未配置翻译服务。 ");

        _ = sourceLanguageHint;
        var sourceInstruction = sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "Detect the source language or languages from the text itself."
            : $"The source language is {sourceLanguage}.";

        var firstTranslation = await SendTranslationRequestAsync(
            options,
            text,
            BuildSystemPrompt(sourceInstruction, targetLanguage, isCorrectiveRetry: false),
            cancellationToken);

        if (!TranslationQualityGuard.ShouldRetry(text, firstTranslation, targetLanguage))
        {
            return new TranslationResult(text, firstTranslation, sourceLanguage, targetLanguage);
        }

        var retryTranslation = await SendTranslationRequestAsync(
            options,
            text,
            BuildSystemPrompt(sourceInstruction, targetLanguage, isCorrectiveRetry: true),
            cancellationToken);
        var preferredTranslation = TranslationQualityGuard.SelectPreferredTranslation(
            text,
            firstTranslation,
            retryTranslation,
            targetLanguage);

        return new TranslationResult(text, preferredTranslation, sourceLanguage, targetLanguage);
    }

    private async Task<string> SendTranslationRequestAsync(
        TranslationProviderOptions options,
        string text,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = text }
        };

        object requestBody = options.Endpoint.Host.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? new
            {
                model = options.Model,
                messages,
                stream = false,
                temperature = 0.1,
                thinking = new { type = "disabled" }
            }
            : new
            {
                model = options.Model,
                messages,
                stream = false,
                temperature = 0.1
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

        return translatedText;
    }

    private static string BuildSystemPrompt(
        string sourceInstruction,
        string targetLanguage,
        bool isCorrectiveRetry)
    {
        var retryInstruction = isCorrectiveRetry
            ? "The previous attempt echoed the source. Perform the translation again and do not paraphrase in the source language. "
            : string.Empty;

        return $"You are a real-time screen translation engine. {sourceInstruction} " +
               $"Translate every natural-language passage into {targetLanguage}. {retryInstruction}" +
               "Keep names, code, commands, paths, URLs, and numbers unchanged when appropriate. " +
               "Preserve paragraph breaks, blank lines, indentation, list numbering, and punctuation where practical, " +
               "but prioritize accurate, natural translation over matching the exact number of lines. " +
               "If text is already in the target language, keep it unchanged. Return only the translation, without explanations or Markdown fences.";
    }
}
