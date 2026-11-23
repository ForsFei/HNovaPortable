using System.IO;

namespace HNovaPortable.Services;

public readonly record struct TransferProgress(long BytesDone, long BytesTotal, string CurrentFile)
{
    public double Percent => BytesTotal <= 0 ? 0 : Math.Min(100, BytesDone * 100.0 / BytesTotal);
}

/// <summary>
/// 目录/文件复制引擎：字节级进度、可取消、失败时清理半成品。
/// 导入（源 → 库）与导出（库 → 目标）共用同一套实现，保证行为一致。
/// </summary>
public static class FileTransfer
{
    private const int BufferSize = 1024 * 1024;

    public sealed record CopyResult(int FileCount, long TotalBytes);

    public static (int FileCount, long TotalBytes) PlanDirectoryCopy(string sourceDirectory)
    {
        var count = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (count, bytes);
    }

    /// <summary>复制整个目录。目标目录不存在时创建；调用方负责失败后的清理。</summary>
    public static async Task<CopyResult> CopyDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (fileCount, totalBytes) = PlanDirectoryCopy(sourceDirectory);
        if (fileCount == 0)
        {
            Directory.CreateDirectory(targetDirectory);
            return new CopyResult(0, 0);
        }

        // 先建目录骨架，减少逐文件创建的开销
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        Directory.CreateDirectory(targetDirectory);

        long copied = 0;
        var lastReport = 0L;

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relative);
            var directory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            copied += await CopyFileAsync(sourceFile, targetFile, copied, totalBytes, relative, progress, cancellationToken)
                .ConfigureAwait(false);

            if (copied - lastReport >= totalBytes / 100 || copied >= totalBytes)
            {
                lastReport = copied;
                progress?.Report(new TransferProgress(copied, totalBytes, relative));
            }
        }

        progress?.Report(new TransferProgress(totalBytes, totalBytes, string.Empty));
        return new CopyResult(fileCount, totalBytes);
    }

    public static async Task<long> CopyFileAsync(
        string sourceFile,
        string targetFile,
        long alreadyCopied,
        long totalBytes,
        string displayName,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[BufferSize];
        long written = 0;

        await using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                if (written % (BufferSize * 4) == 0)
                {
                    progress?.Report(new TransferProgress(alreadyCopied + written, totalBytes, displayName));
                }
            }
        }

        // 保留原始时间戳，便于用户核对
        try
        {
            File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(sourceFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return written;
    }

    public static long GetAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return long.MaxValue;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>回滚用：只删除本次操作新建的目录（path 为空时什么也不做）。</summary>
    public static void TryDeleteDirectoryIfCreated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TryDeleteDirectory(path);
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
