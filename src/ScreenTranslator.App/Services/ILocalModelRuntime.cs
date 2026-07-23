using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public interface ILocalModelRuntime
{
    Task InitializeAsync(
        IProgress<LocalModelProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> GenerateAsync(
        string prompt,
        int maximumTokens,
        CancellationToken cancellationToken = default);
}
