using System.Globalization;
using System.Text;

namespace ScreenTranslator.App.Core;

public sealed class RecognizedTextChangeTracker
{
    private string? _lastTranslatedFingerprint;

    public bool ShouldTranslate(string? recognizedText)
    {
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            return false;
        }

        var fingerprint = CreateFingerprint(recognizedText);

        if (fingerprint.Length == 0)
        {
            return false;
        }

        return !fingerprint.Equals(_lastTranslatedFingerprint, StringComparison.Ordinal);
    }

    public void MarkTranslated(string recognizedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recognizedText);
        _lastTranslatedFingerprint = CreateFingerprint(recognizedText);
    }

    public void Reset()
    {
        _lastTranslatedFingerprint = null;
    }

    internal static string CreateFingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var sourceCharacter in normalized)
        {
            if (char.GetUnicodeCategory(sourceCharacter) == UnicodeCategory.Format)
            {
                continue;
            }

            if (char.IsWhiteSpace(sourceCharacter))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            var character = sourceCharacter switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u2013' or '\u2014' => '-',
                _ => sourceCharacter
            };
            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }
}
