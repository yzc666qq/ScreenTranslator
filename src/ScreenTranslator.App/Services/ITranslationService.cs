using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        string? sourceLanguageHint = null);
}
