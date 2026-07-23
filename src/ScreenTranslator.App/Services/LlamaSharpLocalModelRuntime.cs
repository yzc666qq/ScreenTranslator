using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public sealed class LlamaSharpLocalModelRuntime(LocalModelStore modelStore)
    : ILocalModelRuntime, IDisposable
{
    private const string TranslationSystemMessage =
        "You are a precise translation engine. Detect the source language automatically. " +
        "Return only the translated text without explanations, labels, quotation marks, or markdown.";

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private bool _disposed;

    public async Task InitializeAsync(
        IProgress<LocalModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_executor is not null)
        {
            return;
        }

        var modelPath = await modelStore.EnsureModelAsync(progress, cancellationToken);
        await _initializationLock.WaitAsync(cancellationToken);

        try
        {
            if (_executor is not null)
            {
                return;
            }

            progress?.Report(new LocalModelProgress(
                "正在加载内置离线模型",
                "首次加载通常需要数秒…"));

            await Task.Run(() =>
            {
                var threadCount = Math.Max(1, Environment.ProcessorCount - 1);
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = 2048,
                    GpuLayerCount = 0,
                    Threads = threadCount,
                    BatchThreads = threadCount,
                    BatchSize = 512
                };
                var weights = LLamaWeights.LoadFromFile(parameters);

                try
                {
                    var executor = new StatelessExecutor(weights, parameters, logger: null)
                    {
                        ApplyTemplate = true,
                        SystemMessage = TranslationSystemMessage
                    };
                    _weights = weights;
                    _executor = executor;
                }
                catch
                {
                    weights.Dispose();
                    throw;
                }
            }, cancellationToken);

            progress?.Report(new LocalModelProgress(
                "内置离线翻译已就绪",
                "Qwen2.5 0.5B · 全程在本机运行"));
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<string> GenerateAsync(
        string prompt,
        int maximumTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTokens);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await InitializeAsync(cancellationToken: cancellationToken);
        await _inferenceLock.WaitAsync(cancellationToken);

        try
        {
            var executor = _executor
                ?? throw new InvalidOperationException("内置离线模型尚未完成加载。");
            using var sampling = new GreedySamplingPipeline();
            var parameters = new InferenceParams
            {
                MaxTokens = maximumTokens,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
                SamplingPipeline = sampling
            };
            var builder = new StringBuilder();

            await foreach (var fragment in executor
                               .InferAsync(prompt, parameters, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                builder.Append(fragment);
            }

            return builder.ToString();
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _executor?.Context.Dispose();
        _weights?.Dispose();
        _initializationLock.Dispose();
        _inferenceLock.Dispose();
    }
}
