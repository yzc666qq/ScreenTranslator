using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public interface IScreenCaptureService
{
    Task<ScreenCaptureFrame> CaptureAsync(
        ScreenRegion region,
        CancellationToken cancellationToken = default);
}
