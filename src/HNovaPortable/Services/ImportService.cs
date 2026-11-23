using System.Diagnostics;
using System.IO;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;
using HNovaPortable.Services.Scanning;

namespace HNovaPortable.Services;

public sealed class ImportRequest
{
    /// <summary>来源：EXE 文件或软件目录。</summary>
    public required string SourcePath { get; init; }

    /// <summary>目录模式下的主程序绝对路径。</summary>
    public string? MainExecutablePath { get; init; }

    public required string Name { get; init; }

    public AppLocationMode Mode { get; init; } = AppLocationMode.Managed;

    public string? CategoryId { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Arguments { get; init; }

    public string? WorkDirectory { get; init; }

    public string? Notes { get; init; }

    /// <summary>扫描阶段识别到的兼容性标记。</summary>
    public PortabilityFlags Flags { get; init; }

    /// <summary>是否复制整个目录（单 EXE 模式下表示“连所在目录一起复制”）。</summary>
    public bool CopyWholeFolder { get; init; } = true;
}

public readonly record struct ImportProgress(string Stage, double Percent, string Detail);

public sealed class ImportResult
{
    public bool Success { get; init; }

    public string? AppId { get; init; }

    public string? Error { get; init; }

    public string? ExecutablePath { get; init; }

    public long BytesCopied { get; init; }

    public TimeSpan Elapsed { get; init; }

    public static ImportResult Failure(string error, TimeSpan elapsed) => new() { Error = error, Elapsed = elapsed };
}

/// <summary>
/// 导入管线：校验 → 空间检查 → 复制（Managed）或直接登记（Linked）→ 图标 → 入库。
/// 复制失败或取消时会清理半成品目录，绝不留下“半个软件”。
/// </summary>
public sealed class ImportService(AppPaths paths, AppRepository appRepository, IconService iconService)
{
    private readonly AppPaths _paths = paths;
    private readonly AppRepository _apps = appRepository;
    private readonly IconService _icons = iconService;

    public async Task<ImportResult> ImportAsync(ImportRequest request, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? createdDirectory = null;

        try
        {
            progress?.Report(new ImportProgress("准备", 0, "正在检查来源"));

            var source = Path.GetFullPath(request.SourcePath);
            var isDirectory = Directory.Exists(source);

            string sourceDirectory;
            string executableAbsolute;

            if (isDirectory)
            {
                sourceDirectory = source;
                if (string.IsNullOrWhiteSpace(request.MainExecutablePath))
                {
                    return ImportResult.Failure("未指定主程序", stopwatch.Elapsed);
                }

                executableAbsolute = Path.GetFullPath(request.MainExecutablePath);
                if (!File.Exists(executableAbsolute))
                {
                    return ImportResult.Failure("主程序不存在：" + executableAbsolute, stopwatch.Elapsed);
                }

                if (!PathUtils.IsInside(sourceDirectory, executableAbsolute))
                {
                    return ImportResult.Failure("主程序必须位于所选目录内", stopwatch.Elapsed);
                }
            }
            else
            {
                if (!File.Exists(source))
                {
                    return ImportResult.Failure("来源文件不存在：" + source, stopwatch.Elapsed);
                }

                sourceDirectory = Path.GetDirectoryName(source) ?? string.Empty;
                executableAbsolute = source;
            }

            var copyWholeFolder = request.CopyWholeFolder;
            var name = BuildName(request.Name, executableAbsolute);
            var metadata = ExecutableMetadata.Read(executableAbsolute);
            var sourceSize = copyWholeFolder ? PathUtils.GetDirectorySize(sourceDirectory) : new FileInfo(executableAbsolute).Length;
            var appId = Guid.NewGuid().ToString("N");

            AppEntry entry;
            string executableAfterImport;

            if (request.Mode == AppLocationMode.Managed)
            {
                var freeSpace = FileTransfer.GetAvailableFreeSpace(_paths.Root);
                if (sourceSize > 0 && sourceSize > freeSpace)
                {
                    return ImportResult.Failure(
                        $"目标磁盘剩余空间不足：需要 {FolderAnalysis.FormatSize(sourceSize)}，可用 {FolderAnalysis.FormatSize(freeSpace)}",
                        stopwatch.Elapsed);
                }

                var targetDirectory = PathUtils.GetUniqueDirectoryPath(_paths.AppsDirectory, name);
                Directory.CreateDirectory(targetDirectory);
                createdDirectory = targetDirectory;

                var copyProgress = progress is null ? null : new Progress<TransferProgress>(p =>
                    progress.Report(new ImportProgress("复制文件", p.Percent, ShortenFile(p.CurrentFile))));

                if (copyWholeFolder)
                {
                    var plan = FileTransfer.PlanDirectoryCopy(sourceDirectory);
                    progress?.Report(new ImportProgress("复制文件", 0, $"共 {plan.FileCount} 个文件，{FolderAnalysis.FormatSize(plan.TotalBytes)}"));

                    var copyResult = await FileTransfer.CopyDirectoryAsync(sourceDirectory, targetDirectory, copyProgress, cancellationToken)
                        .ConfigureAwait(false);

                    if (copyResult.TotalBytes != plan.TotalBytes)
                    {
                        return ImportResult.Failure(
                            $"复制校验失败：源 {FolderAnalysis.FormatSize(plan.TotalBytes)}，实际 {FolderAnalysis.FormatSize(copyResult.TotalBytes)}",
                            stopwatch.Elapsed);
                    }

                    var relativeInSource = Path.GetRelativePath(sourceDirectory, executableAbsolute);
                    executableAfterImport = Path.Combine(targetDirectory, relativeInSource);
                }
                else
                {
                    var fileName = Path.GetFileName(executableAbsolute);
                    executableAfterImport = Path.Combine(targetDirectory, fileName);
                    await FileTransfer.CopyFileAsync(executableAbsolute, executableAfterImport, 0, sourceSize, fileName, copyProgress, cancellationToken)
                        .ConfigureAwait(false);
                }

                var portablePath = PathUtils.ToPortableIfInside(_paths.Root, executableAfterImport);
                if (portablePath is null)
                {
                    return ImportResult.Failure("无法计算库内相对路径（目标不在库根内）", stopwatch.Elapsed);
                }

                entry = new AppEntry
                {
                    Id = appId,
                    Name = name,
                    Mode = AppLocationMode.Managed,
                    RelativePath = portablePath,
                    WorkDirectory = string.IsNullOrWhiteSpace(request.WorkDirectory)
                        ? PathUtils.GetParentPortable(portablePath)
                        : request.WorkDirectory,
                    Arguments = request.Arguments,
                    CategoryId = request.CategoryId,
                    Notes = request.Notes,
                    SizeBytes = sourceSize,
                    Health = AppHealth.Ok,
                    PortabilityFlags = request.Flags,
                    ExeVersion = metadata.FileVersion,
                    ProductName = metadata.ProductName,
                    Company = metadata.Company,
                    Description = metadata.FileDescription,
                    LastScanUtc = DateTimeOffset.UtcNow,
                };
            }
            else
            {
                var flags = request.Flags;
                if (!PathUtils.IsInside(_paths.Root, executableAbsolute))
                {
                    flags |= PortabilityFlags.LinkedOutsideLibrary;
                }

                executableAfterImport = executableAbsolute;
                entry = new AppEntry
                {
                    Id = appId,
                    Name = name,
                    Mode = AppLocationMode.Linked,
                    LinkedPath = executableAbsolute,
                    WorkDirectory = string.IsNullOrWhiteSpace(request.WorkDirectory)
                        ? Path.GetDirectoryName(executableAbsolute)
                        : request.WorkDirectory,
                    Arguments = request.Arguments,
                    CategoryId = request.CategoryId,
                    Notes = request.Notes,
                    SizeBytes = sourceSize,
                    Health = AppHealth.Ok,
                    PortabilityFlags = flags,
                    ExeVersion = metadata.FileVersion,
                    ProductName = metadata.ProductName,
                    Company = metadata.Company,
                    Description = metadata.FileDescription,
                    LastScanUtc = DateTimeOffset.UtcNow,
                };
            }

            progress?.Report(new ImportProgress("生成图标", 92, "正在提取程序图标"));
            entry.IconKey = await _icons.CaptureAsync(executableAfterImport, appId, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ImportProgress("写入数据库", 96, "正在保存软件信息"));
            await _apps.UpsertAsync(entry, request.Tags, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ImportProgress("完成", 100, "已加入软件库"));
            return new ImportResult
            {
                Success = true,
                AppId = appId,
                ExecutablePath = executableAfterImport,
                BytesCopied = request.Mode == AppLocationMode.Managed ? sourceSize : 0,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            FileTransfer.TryDeleteDirectoryIfCreated(createdDirectory);
            return ImportResult.Failure("操作已取消", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            FileTransfer.TryDeleteDirectoryIfCreated(createdDirectory);
            return ImportResult.Failure(ex.Message, stopwatch.Elapsed);
        }
    }

    private static string BuildName(string? requested, string executablePath)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return ScanService.CleanDisplayName(requested.Trim());
        }

        var metadata = ExecutableMetadata.Read(executablePath);
        var raw = metadata.ProductName ?? metadata.FileDescription ?? Path.GetFileNameWithoutExtension(executablePath);
        return ScanService.CleanDisplayName(raw);
    }

    private static string ShortenFile(string relativePath)
        => relativePath.Length <= 60 ? relativePath : "…" + relativePath[^57..];
}
