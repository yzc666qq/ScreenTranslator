namespace ScreenTranslator.App.Core;

public sealed record TranslationResult(
    string SourceText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage);
