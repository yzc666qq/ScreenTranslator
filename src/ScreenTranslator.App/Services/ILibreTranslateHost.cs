using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public interface ILibreTranslateHost : IDisposable
{
    bool IsRunning { get; }

    void Stop();

    Task<Uri> StartAsync(
        string targetLanguage,
        IProgress<LocalEngineProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
