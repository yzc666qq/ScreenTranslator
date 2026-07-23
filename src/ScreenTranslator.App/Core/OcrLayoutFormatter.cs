using System.Text;

namespace ScreenTranslator.App.Core;

public static class OcrLayoutFormatter
{
    private const int MaximumHorizontalSpaces = 40;
    private const int MaximumBlankLines = 3;

    public static string Format(IEnumerable<IReadOnlyList<RecognizedWordLayout>> sourceLines)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);

        var lines = sourceLines
            .Select(line => line
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .OrderBy(word => word.X)
                .ToArray())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var words = lines.SelectMany(line => line).ToArray();
        var originX = words.Min(word => word.X);
        var characterWidth = Median(words
            .Where(word => word.Width > 0)
            .Select(word => word.Width / Math.Max(1, word.Text.Length))) ?? 8d;
        var lineHeight = Median(words
            .Where(word => word.Height > 0)
            .Select(word => word.Height)) ?? 16d;

        characterWidth = Math.Max(1d, characterWidth);
        lineHeight = Math.Max(1d, lineHeight);

        var builder = new StringBuilder();
        double? previousBottom = null;

        foreach (var line in lines)
        {
            var lineTop = line.Min(word => word.Y);
            var lineBottom = line.Max(word => word.Y + word.Height);

            if (previousBottom is not null)
            {
                builder.AppendLine();
                var verticalGap = Math.Max(0d, lineTop - previousBottom.Value);
                var blankLines = Math.Clamp(
                    (int)Math.Round(verticalGap / lineHeight, MidpointRounding.AwayFromZero) - 1,
                    0,
                    MaximumBlankLines);

                for (var index = 0; index < blankLines; index++)
                {
                    builder.AppendLine();
                }
            }

            var firstWord = line[0];
            var indentation = CalculateSpaces(firstWord.X - originX, characterWidth, minimum: 0);
            builder.Append(' ', indentation);
            builder.Append(firstWord.Text);

            var previousRight = firstWord.X + firstWord.Width;

            foreach (var word in line.Skip(1))
            {
                var gap = Math.Max(0d, word.X - previousRight);
                builder.Append(' ', CalculateSpaces(gap, characterWidth, minimum: 1));
                builder.Append(word.Text);
                previousRight = word.X + word.Width;
            }

            previousBottom = lineBottom;
        }

        return builder.ToString();
    }

    private static int CalculateSpaces(double distance, double characterWidth, int minimum)
    {
        var spaces = (int)Math.Round(distance / characterWidth, MidpointRounding.AwayFromZero);
        return Math.Clamp(spaces, minimum, MaximumHorizontalSpaces);
    }

    private static double? Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();

        if (ordered.Length == 0)
        {
            return null;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}
