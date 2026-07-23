namespace ScreenTranslator.App.Core;

public static class TranslationQualityGuard
{
    public static bool ShouldRetry(string sourceText, string translatedText, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var normalizedSource = NormalizeForComparison(sourceText);
        return normalizedSource.Length > 0 && normalizedSource
            .Equals(NormalizeForComparison(translatedText), StringComparison.OrdinalIgnoreCase);
    }

    public static string SelectPreferredTranslation(
        string sourceText,
        string firstTranslation,
        string retryTranslation,
        string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(firstTranslation);
        ArgumentNullException.ThrowIfNull(retryTranslation);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        if (!ShouldRetry(sourceText, firstTranslation, targetLanguage))
        {
            return firstTranslation;
        }

        return HasExpectedTargetWriting(retryTranslation, targetLanguage)
            ? retryTranslation
            : firstTranslation;
    }

    private static bool IsChineseTarget(string target) =>
        target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("中文", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("Chinese", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnglishTarget(string target) =>
        target.Equals("en", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("英文", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("英语", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("English", StringComparison.OrdinalIgnoreCase);

    private static bool IsKoreanTarget(string target) =>
        target.Equals("ko", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("ko-", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("韩语", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("한국", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("Korean", StringComparison.OrdinalIgnoreCase);

    private static bool IsJapaneseTarget(string target) =>
        target.Equals("ja", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("ja-", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("日语", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("日本", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("Japanese", StringComparison.OrdinalIgnoreCase);

    private static bool HasExpectedTargetWriting(string text, string targetLanguage)
    {
        var target = targetLanguage.Trim();

        if (IsChineseTarget(target))
        {
            return text.Any(IsHanCharacter);
        }

        if (IsEnglishTarget(target))
        {
            return text.Any(IsLatinLetter);
        }

        if (IsKoreanTarget(target))
        {
            return text.Any(IsHangulCharacter);
        }

        if (IsJapaneseTarget(target))
        {
            return text.Any(character => IsKanaCharacter(character) || IsHanCharacter(character));
        }

        return NormalizeForComparison(text).Length > 0;
    }

    private static string NormalizeForComparison(string sourceText) =>
        new(sourceText
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static bool IsLatinLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsHanCharacter(char character) =>
        character is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF';

    private static bool IsHangulCharacter(char character) =>
        character is >= '\uAC00' and <= '\uD7AF';

    private static bool IsKanaCharacter(char character) =>
        character is >= '\u3040' and <= '\u30FF';
}
