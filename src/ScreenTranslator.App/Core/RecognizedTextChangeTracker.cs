using System.Globalization;
using System.Text;

namespace ScreenTranslator.App.Core;

public sealed class RecognizedTextChangeTracker
{
    private const int RequiredStableObservations = 2;

    private string? _lastTranslatedFingerprint;
    private string? _pendingFingerprint;
    private int _pendingObservations;

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

        if (_lastTranslatedFingerprint is null)
        {
            return true;
        }

        if (fingerprint.Equals(_lastTranslatedFingerprint, StringComparison.Ordinal))
        {
            ClearPendingChange();
            return false;
        }

        if (!fingerprint.Equals(_pendingFingerprint, StringComparison.Ordinal))
        {
            _pendingFingerprint = fingerprint;
            _pendingObservations = 1;
            return false;
        }

        _pendingObservations++;
        return _pendingObservations >= RequiredStableObservations;
    }

    public void MarkTranslated(string recognizedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recognizedText);
        _lastTranslatedFingerprint = CreateFingerprint(recognizedText);
        ClearPendingChange();
    }

    public void Reset()
    {
        _lastTranslatedFingerprint = null;
        ClearPendingChange();
    }

    private static string CreateFingerprint(string text)
    {
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

    private void ClearPendingChange()
    {
        _pendingFingerprint = null;
        _pendingObservations = 0;
    }
}
