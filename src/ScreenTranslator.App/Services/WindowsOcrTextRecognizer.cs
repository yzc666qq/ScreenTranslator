using ScreenTranslator.App.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenTranslator.App.Services;

public sealed class WindowsOcrTextRecognizer : ITextRecognizer
{
    private readonly OcrEngine _engine;

    public WindowsOcrTextRecognizer()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows 未安装可用的 OCR 语言包。请在系统语言设置中添加 OCR 支持。 ");
    }

    public async Task<RecognizedText> RecognizeAsync(
        ScreenCaptureFrame frame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (frame.Stride != frame.Width * 4)
        {
            throw new ArgumentException("OCR 只接受连续 BGRA8 像素。", nameof(frame));
        }

        var width = frame.Width;
        var height = frame.Height;
        var pixels = frame.Pixels.ToArray();
        var maxDimension = (int)OcrEngine.MaxImageDimension;

        if (maxDimension > 0 && Math.Max(width, height) > maxDimension)
        {
            (pixels, width, height) = DownscaleBgra8(pixels, width, height, maxDimension);
        }

        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        var buffer = writer.DetachBuffer();
        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);

        var result = await _engine.RecognizeAsync(softwareBitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var lines = result.Lines.Select(line =>
            (IReadOnlyList<RecognizedWordLayout>)line.Words
                .Select(word => new RecognizedWordLayout(
                    word.Text,
                    word.BoundingRect.X,
                    word.BoundingRect.Y,
                    word.BoundingRect.Width,
                    word.BoundingRect.Height))
                .ToArray());

        return new RecognizedText(
            OcrLayoutFormatter.Format(lines),
            _engine.RecognizerLanguage?.LanguageTag);
    }

    private static (byte[] Pixels, int Width, int Height) DownscaleBgra8(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int maxDimension)
    {
        var scale = maxDimension / (double)Math.Max(sourceWidth, sourceHeight);
        var targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var target = new byte[targetWidth * targetHeight * 4];

        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            var sourceY = Math.Min(sourceHeight - 1, (int)(targetY / scale));

            for (var targetX = 0; targetX < targetWidth; targetX++)
            {
                var sourceX = Math.Min(sourceWidth - 1, (int)(targetX / scale));
                var sourceOffset = (sourceY * sourceWidth + sourceX) * 4;
                var targetOffset = (targetY * targetWidth + targetX) * 4;
                System.Buffer.BlockCopy(source, sourceOffset, target, targetOffset, 4);
            }
        }

        return (target, targetWidth, targetHeight);
    }
}
