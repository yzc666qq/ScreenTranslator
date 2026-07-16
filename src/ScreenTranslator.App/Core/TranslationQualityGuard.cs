namespace ScreenTranslator.App.Core;

public static class TranslationQualityGuard
{
    public static bool ShouldRetry(string sourceText, string translatedText, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var target = targetLanguage.Trim();
        var sourceLatinLetters = sourceText.Count(IsLatinLetter);
        var translatedLatinLetters = translatedText.Count(IsLatinLetter);
        var sourceHanCharacters = sourceText.Count(IsHanCharacter);
        var translatedHanCharacters = translatedText.Count(IsHanCharacter);

        if (IsChineseTarget(target))
        {
            return sourceLatinLetters >= 4 && translatedHanCharacters == 0;
        }

        if (IsEnglishTarget(target))
        {
            return sourceHanCharacters >= 2 && translatedLatinLetters < 3;
        }

        if (IsKoreanTarget(target))
        {
            return (sourceLatinLetters >= 4 || sourceHanCharacters >= 2) &&
                   !translatedText.Any(IsHangulCharacter);
        }

        if (IsJapaneseTarget(target))
        {
            return sourceLatinLetters >= 4 &&
                   !translatedText.Any(character => IsKanaCharacter(character) || IsHanCharacter(character));
        }

        return false;
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

    private static bool IsLatinLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsHanCharacter(char character) =>
        character is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF';

    private static bool IsHangulCharacter(char character) =>
        character is >= '\uAC00' and <= '\uD7AF';

    private static bool IsKanaCharacter(char character) =>
        character is >= '\u3040' and <= '\u30FF';
}
