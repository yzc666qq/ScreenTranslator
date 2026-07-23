using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public interface ITextRecognizer
{
    Task<RecognizedText> RecognizeAsync(
        ScreenCaptureFrame frame,
        CancellationToken cancellationToken = default);
}
