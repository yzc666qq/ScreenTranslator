namespace ScreenTranslator.App.Core;

internal readonly record struct TranslationRequestKey(
    string SourceFingerprint,
    string TargetLanguage);

internal sealed class LatestTranslationCoordinator : IDisposable
{
    private readonly object _gate = new();
    private TranslationRequestKey? _latestKey;
    private CancellationTokenSource? _activeOperation;

    public bool TryObserve(
        string sourceText,
        string targetLanguage,
        out TranslationRequestKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var fingerprint = RecognizedTextChangeTracker.CreateFingerprint(sourceText);
        if (fingerprint.Length == 0)
        {
            key = default;
            return false;
        }

        key = new TranslationRequestKey(
            fingerprint,
            targetLanguage.Trim().ToUpperInvariant());

        CancellationTokenSource? operationToCancel;

        lock (_gate)
        {
            if (_latestKey == key)
            {
                return false;
            }

            _latestKey = key;
            operationToCancel = _activeOperation;
        }

        CancelQuietly(operationToCancel);
        return true;
    }

    public CancellationTokenSource? TryBegin(
        TranslationRequestKey key,
        CancellationToken runCancellationToken)
    {
        lock (_gate)
        {
            if (_latestKey != key)
            {
                return null;
            }

            _activeOperation = CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken);
            return _activeOperation;
        }
    }

    public bool IsLatest(TranslationRequestKey key)
    {
        lock (_gate)
        {
            return _latestKey == key;
        }
    }

    public void AllowRetry(TranslationRequestKey key)
    {
        lock (_gate)
        {
            if (_latestKey == key)
            {
                _latestKey = null;
            }
        }
    }

    public void End(CancellationTokenSource operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_gate)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
            }
        }

        operation.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? activeOperation;

        lock (_gate)
        {
            activeOperation = _activeOperation;
            _activeOperation = null;
            _latestKey = null;
        }

        CancelQuietly(activeOperation);
        activeOperation?.Dispose();
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed between observation and cancellation.
        }
        catch (AggregateException)
        {
            // Cancellation callback failures must not prevent the newest work from being queued.
        }
    }
}
