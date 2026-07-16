namespace ScreenTranslator.App.Core;

public sealed record RecognizedText(
    string Text,
    string? DetectedLanguage = null,
    double? Confidence = null);
