using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using ScreenTranslator.App.Core;

namespace ScreenTranslator.App.Services;

public sealed class LocalModelStore
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly HttpClient _httpClient;
    private readonly LocalModelDescriptor _descriptor;
    private readonly string _modelDirectory;

    public LocalModelStore(
        HttpClient httpClient,
        LocalModelDescriptor? descriptor = null,
        string? modelDirectory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _descriptor = descriptor ?? LocalModelDescriptor.Qwen25HalfBillionQ4Km;
        _modelDirectory = modelDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTranslator",
            "Models");
    }

    public string ModelPath => Path.Combine(_modelDirectory, _descriptor.FileName);

    public async Task<string> EnsureModelAsync(
        IProgress<LocalModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelDirectory);

        if (await IsValidModelAsync(ModelPath, progress, cancellationToken))
        {
            progress?.Report(new LocalModelProgress(
                "内置离线模型已就绪",
                $"Qwen2.5 0.5B · {FormatBytes(_descriptor.FileSize)}"));
            return ModelPath;
        }

        if (File.Exists(ModelPath))
        {
            File.Delete(ModelPath);
        }

        var temporaryPath = ModelPath + ".download";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        progress?.Report(new LocalModelProgress(
            "首次使用需要下载离线模型",
            $"准备下载 Qwen2.5 0.5B · {FormatBytes(_descriptor.FileSize)}",
            TotalBytes: _descriptor.FileSize));

        using var response = await _httpClient.GetAsync(
            _descriptor.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != _descriptor.FileSize)
        {
            throw new InvalidDataException(
                $"离线模型大小不匹配：预期 {_descriptor.FileSize} 字节，实际 {contentLength} 字节。");
        }

        try
        {
            await DownloadAndVerifyAsync(response, temporaryPath, progress, cancellationToken);
            File.Move(temporaryPath, ModelPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }

        progress?.Report(new LocalModelProgress(
            "离线模型下载完成",
            $"Qwen2.5 0.5B · {FormatBytes(_descriptor.FileSize)}",
            _descriptor.FileSize,
            _descriptor.FileSize));
        return ModelPath;
    }

    private async Task<bool> IsValidModelAsync(
        string path,
        IProgress<LocalModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != _descriptor.FileSize)
        {
            return false;
        }

        progress?.Report(new LocalModelProgress(
            "正在验证本地离线模型",
            "检查模型完整性…"));

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash).Equals(
            _descriptor.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAndVerifyAsync(
        HttpResponseMessage response,
        string temporaryPath,
        IProgress<LocalModelProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long totalRead = 0;

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(
                    buffer.AsMemory(0, CopyBufferSize),
                    cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
                incrementalHash.AppendData(buffer, 0, bytesRead);
                totalRead += bytesRead;

                progress?.Report(new LocalModelProgress(
                    "正在下载内置离线模型",
                    $"{totalRead * 100d / _descriptor.FileSize:0.0}% · " +
                    $"{FormatBytes(totalRead)} / {FormatBytes(_descriptor.FileSize)}",
                    totalRead,
                    _descriptor.FileSize));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await destination.FlushAsync(cancellationToken);

        if (totalRead != _descriptor.FileSize)
        {
            throw new InvalidDataException(
                $"离线模型下载不完整：预期 {_descriptor.FileSize} 字节，实际 {totalRead} 字节。");
        }

        var actualHash = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
        if (!actualHash.Equals(_descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("离线模型 SHA-256 校验失败，已丢弃本次下载。");
        }
    }

    private static string FormatBytes(long value) =>
        $"{value / 1024d / 1024d:0.0} MB";
}
