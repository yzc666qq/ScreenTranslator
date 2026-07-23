namespace ScreenTranslator.App.Core;

public enum TranslationServiceReadinessState
{
    Ready,
    ServiceUnavailable,
    TargetLanguageUnavailable
}

public sealed record TranslationServiceReadiness(
    TranslationServiceReadinessState State,
    string Message,
    IReadOnlyList<string> SupportedLanguages)
{
    public bool IsReady => State == TranslationServiceReadinessState.Ready;
}
