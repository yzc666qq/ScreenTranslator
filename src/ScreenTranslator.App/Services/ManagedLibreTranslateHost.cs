using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public sealed class ManagedLibreTranslateHost : ILibreTranslateHost
{
    private const int MaximumLogLines = 30;
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly string _executablePath;
    private readonly string _dataRoot;
    private readonly TimeSpan _startupTimeout;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentQueue<string> _recentLogs = new();

    private Process? _process;
    private Uri? _endpoint;
    private bool _disposed;

    public ManagedLibreTranslateHost(
        HttpClient httpClient,
        string? executablePath = null,
        string? dataRoot = null,
        TimeSpan? startupTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _executablePath = executablePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "LibreTranslate",
            "python",
            "Scripts",
            "libretranslate.exe");
        _dataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTranslator",
            "LibreTranslate");
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
    }

    public bool IsRunning => _process is { HasExited: false } && _endpoint is not null;

    public string DiagnosticSummary => GetLogSummary();

    public void Stop()
    {
        var process = _process;
        _process = null;
        _endpoint = null;

        if (process is null)
        {
            return;
        }

        StopOwnedProcess(process);
        process.Dispose();
    }

    public async Task<Uri> StartAsync(
        string targetLanguage,
        IProgress<LocalEngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _startLock.WaitAsync(cancellationToken);

        try
        {
            if (IsRunning)
            {
                return _endpoint!;
            }

            while (_recentLogs.TryDequeue(out _))
            {
            }

            if (!File.Exists(_executablePath))
            {
                throw new FileNotFoundException(
                    "当前安装包不包含 LibreTranslate 私有运行时。" +
                    "请使用完整发布包，或先运行 tools/Build-LibreTranslateRuntime.ps1。",
                    _executablePath);
            }

            var port = ReserveLoopbackPort();
            var endpoint = new Uri($"http://127.0.0.1:{port}");
            EnsureDataDirectories(_dataRoot);
            var startInfo = CreateStartInfo(_executablePath, _dataRoot, port);
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += Process_OutputDataReceived;
            process.ErrorDataReceived += Process_ErrorDataReceived;

            progress?.Report(new LocalEngineProgress(
                "正在启动内置 LibreTranslate",
                "准备本地机器翻译运行时…"));

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("LibreTranslate 进程未能启动。");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
                _endpoint = endpoint;

                await WaitUntilReadyAsync(
                    process,
                    endpoint,
                    targetLanguage,
                    progress,
                    cancellationToken);
            }
            catch
            {
                StopOwnedProcess(process);
                process.Dispose();
                _process = null;
                _endpoint = null;
                throw;
            }

            progress?.Report(new LocalEngineProgress(
                "LibreTranslate 已就绪",
                "专用翻译模型已加载 · 全程在本机运行"));
            return endpoint;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string dataRoot,
        int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, IPEndPoint.MinPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[]
                 {
                     "--host", "127.0.0.1",
                     "--port", port.ToString(),
                     "--load-only", "en,zh,ja,ko",
                     "--disable-files-translation",
                     "--disable-web-ui",
                     "--threads", Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(),
                     "--translation-cache", "all"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(dataRoot, "Data");
        startInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(dataRoot, "Cache");
        startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(dataRoot, "Config");
        startInfo.Environment["ARGOS_PACKAGES_DIR"] = Path.Combine(dataRoot, "Models");
        startInfo.Environment["ARGOS_DEVICE_TYPE"] = "cpu";
        startInfo.Environment["ARGOS_CHUNK_TYPE"] = "ARGOSTRANSLATE";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        return startInfo;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _startLock.Dispose();
    }

    private async Task WaitUntilReadyAsync(
        Process process,
        Uri endpoint,
        string targetLanguage,
        IProgress<LocalEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var service = new LibreTranslateTranslationService(_httpClient);
        service.Configure(endpoint, isManagedRuntime: true);
        var startedAt = Stopwatch.StartNew();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(_startupTimeout);

        try
        {
            while (true)
            {
                timeoutCancellation.Token.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"LibreTranslate 启动后意外退出（代码 {process.ExitCode}）。{GetLogSummary()}");
                }

                var readiness = await service.CheckReadinessAsync(
                    targetLanguage,
                    timeoutCancellation.Token);

                if (readiness.IsReady)
                {
                    return;
                }

                if (readiness.State == TranslationServiceReadinessState.TargetLanguageUnavailable)
                {
                    throw new InvalidOperationException(readiness.Message);
                }

                progress?.Report(new LocalEngineProgress(
                    "正在准备 LibreTranslate 语言模型",
                    startedAt.Elapsed < TimeSpan.FromSeconds(5)
                        ? "启动本地服务…"
                        : $"首次使用会自动下载并加载语言包，已等待 {startedAt.Elapsed:mm\\:ss}"));
                await Task.Delay(TimeSpan.FromSeconds(1), timeoutCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"LibreTranslate 在 {_startupTimeout.TotalMinutes:0.#} 分钟内未能就绪。{GetLogSummary()}");
        }
    }

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e) =>
        AddLogLine(e.Data);

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e) =>
        AddLogLine(e.Data);

    private void AddLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _recentLogs.Enqueue(line.Trim());

        while (_recentLogs.Count > MaximumLogLines)
        {
            _recentLogs.TryDequeue(out _);
        }
    }

    private string GetLogSummary()
    {
        var summary = string.Join(" | ", _recentLogs.TakeLast(5));
        return string.IsNullOrWhiteSpace(summary) ? "没有可用的运行日志。" : summary;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void EnsureDataDirectories(string dataRoot)
    {
        Directory.CreateDirectory(Path.Combine(dataRoot, "Data"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Cache"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Config"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Models"));
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited or a concurrent cancellation path already released it.
        }
    }
}
