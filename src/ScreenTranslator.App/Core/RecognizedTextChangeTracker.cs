namespace ScreenTranslator.App.Core;

public sealed class RecognizedTextChangeTracker
{
    private string? _lastTranslatedText;

    public bool ShouldTranslate(string? recognizedText)
    {
        return !string.IsNullOrWhiteSpace(recognizedText) &&
               !string.Equals(recognizedText, _lastTranslatedText, StringComparison.Ordinal);
    }

    public void MarkTranslated(string recognizedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recognizedText);
        _lastTranslatedText = recognizedText;
    }

    public void Reset()
    {
        _lastTranslatedText = null;
    }
}
