using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;
using Microsoft.Data.Sqlite;

namespace HNovaPortable.Services;

public enum ExportFormat
{
    /// <summary>直接生成可复制的文件夹。</summary>
    Folder = 0,

    /// <summary>ZIP 压缩包（.NET 内置实现）。</summary>
    Zip = 1,

    /// <summary>7z 压缩包（需要系统或同目录下有 7z.exe）。</summary>
    SevenZip = 2,
}

public sealed class ExportOptions
{
    /// <summary>文件夹模式为目录路径，压缩模式为压缩包文件路径。</summary>
    public required string TargetPath { get; init; }

    public ExportFormat Format { get; init; } = ExportFormat.Folder;

    /// <summary>是否带上 HNova Portable 程序本体，使导出结果可直接双击运行。</summary>
    public bool IncludeProgramFiles { get; init; } = true;

    /// <summary>把引用模式的软件复制进包并转换为托管模式。</summary>
    public bool ConvertLinkedToManaged { get; init; }

    /// <summary>未转换的引用条目是否保留记录（换电脑后会显示为需要重新定位）。</summary>
    public bool IncludeLinkedRecords { get; init; }

    /// <summary>空表示导出全部。</summary>
    public IReadOnlyList<string> AppIds { get; init; } = [];
}

public readonly record struct ExportProgress(string Stage, double Percent, string Detail);

public sealed class ExportResult
{
    public bool Success { get; init; }

    public string? TargetPath { get; init; }

    public string? Error { get; init; }

    public int AppCount { get; init; }

    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public TimeSpan Elapsed { get; init; }

    public List<string> Warnings { get; init; } = [];

    public string Report()
    {
        if (!Success)
        {
            return "导出失败：" + Error;
        }

        var lines = new List<string>
        {
            $"已导出 {AppCount} 个软件，{FileCount} 个文件，共 {Bytes.Format(TotalBytes)}",
            $"耗时 {Elapsed.TotalSeconds:0.0} 秒",
            $"目标：{TargetPath}",
        };

        if (Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("注意事项：");
            lines.AddRange(Warnings.Select(w => "· " + w));
        }

        lines.Add(string.Empty);
        lines.Add("把整个文件夹（或解压后的目录）复制到新电脑，双击 HNovaPortable.exe 即可继续使用。");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 整库导出。核心不变量：托管软件在包内仍然是 Apps/... 相对路径，
/// 因此换电脑后无需任何路径重写。
/// </summary>
public sealed class ExportService(AppPaths paths, AppRepository appRepository)
{
    private const string ManifestFileName = "export-manifest.json";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppPaths _paths = paths;
    private readonly AppRepository _apps = appRepository;

    public async Task<ExportResult> ExportAsync(ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var isFolder = options.Format == ExportFormat.Folder;
        var staging = isFolder
            ? Path.GetFullPath(options.TargetPath)
            : Path.Combine(_paths.CacheDirectory, "export-" + Guid.NewGuid().ToString("N")[..8]);

        var createdTargetFolder = false;

        try
        {
            progress?.Report(new ExportProgress("准备", 0, "读取软件库"));

            var all = await _apps.ListAsync(new AppQuery(), cancellationToken).ConfigureAwait(false);
            var selected = options.AppIds.Count == 0
                ? all.ToList()
                : all.Where(a => options.AppIds.Contains(a.Id, StringComparer.Ordinal)).ToList();

            if (selected.Count == 0)
            {
                return new ExportResult { Error = "没有可导出的软件", Elapsed = stopwatch.Elapsed };
            }

            // ---------- 预检 ----------
            progress?.Report(new ExportProgress("预检", 2, "检查文件与磁盘空间"));

            var exportable = new List<AppEntry>();
            foreach (var app in selected)
            {
                var executable = app.ResolveExecutablePath(_paths.Root);
                if (executable is null)
                {
                    warnings.Add($"{app.Name}：程序文件不存在，已跳过");
                    continue;
                }

                if (app.Mode == AppLocationMode.Linked && !options.ConvertLinkedToManaged && !options.IncludeLinkedRecords)
                {
                    warnings.Add($"{app.Name}：引用模式且未选择打包，已跳过");
                    continue;
                }

                exportable.Add(app);
            }

            if (exportable.Count == 0)
            {
                return new ExportResult { Error = "没有可导出的有效软件", Warnings = warnings, Elapsed = stopwatch.Elapsed };
            }

            var estimatedBytes = exportable.Sum(a => Math.Max(a.SizeBytes, 0));
            createdTargetFolder = EnsureTargetIsUsable(options, isFolder);

            var spaceTarget = isFolder ? options.TargetPath : Path.GetDirectoryName(Path.GetFullPath(options.TargetPath)) ?? _paths.Root;
            var freeSpace = FileTransfer.GetAvailableFreeSpace(spaceTarget);
            var sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(spaceTarget)), Path.GetPathRoot(_paths.Root), StringComparison.OrdinalIgnoreCase);
            var required = (long)(estimatedBytes * (sameDrive ? 2.2 : 1.1));

            if (estimatedBytes > 0 && required > freeSpace)
            {
                return new ExportResult
                {
                    Error = $"目标磁盘空间不足：预计需要 {Bytes.Format(required)}，可用 {Bytes.Format(freeSpace)}",
                    Warnings = warnings,
                    Elapsed = stopwatch.Elapsed,
                };
            }

            Directory.CreateDirectory(staging);
            var packageApps = Path.Combine(staging, "Apps");
            var packageData = Path.Combine(staging, "Data");
            var packageIcons = Path.Combine(packageData, "icons");
            Directory.CreateDirectory(packageApps);
            Directory.CreateDirectory(packageData);
            Directory.CreateDirectory(packageIcons);

            // ---------- 复制托管软件目录（按 Apps 下一级目录去重） ----------
            var copiedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folderJobs = new List<(string Source, string Target)>();

            foreach (var app in exportable.Where(a => a.Mode == AppLocationMode.Managed))
            {
                // 相对路径形如 Apps/<文件夹>/<子路径>，取第 2 段作为库内软件目录名
                var segments = (app.RelativePath ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
                var firstSegment = segments.Length >= 2 ? segments[1] : null;
                if (string.IsNullOrWhiteSpace(firstSegment) || !copiedFolders.Add(firstSegment))
                {
                    continue;
                }

                var source = Path.Combine(_paths.AppsDirectory, firstSegment);
                if (Directory.Exists(source))
                {
                    folderJobs.Add((source, Path.Combine(packageApps, firstSegment)));
                }
                else
                {
                    warnings.Add($"{app.Name}：托管目录不存在（{firstSegment}）");
                }
            }

            // ---------- 转换引用软件 ----------
            var converted = new List<(AppEntry App, string PortablePath, string? IconSource)>();
            foreach (var app in exportable.Where(a => a.Mode == AppLocationMode.Linked && options.ConvertLinkedToManaged))
            {
                var executable = app.ResolveExecutablePath(_paths.Root);
                if (executable is null)
                {
                    warnings.Add($"{app.Name}：引用路径失效，未打包");
                    continue;
                }

                var sourceDirectory = Path.GetDirectoryName(executable)!;

                if (PathUtils.IsInside(_paths.Root, executable))
                {
                    // 已在库内，直接按相对路径登记
                    var portable = PathUtils.ToPortableIfInside(_paths.Root, executable);
                    if (portable is not null)
                    {
                        var segment = portable.Split('/').ElementAtOrDefault(1);
                        if (!string.IsNullOrWhiteSpace(segment) && copiedFolders.Add(segment))
                        {
                            var source = Path.Combine(_paths.AppsDirectory, segment);
                            if (Directory.Exists(source))
                            {
                                folderJobs.Add((source, Path.Combine(packageApps, segment)));
                            }
                        }

                        converted.Add((app, portable, executable));
                    }

                    continue;
                }

                var targetName = PathUtils.SanitizeFolderName(app.Name);
                var targetFolder = Path.Combine(packageApps, targetName);
                var index = 2;
                while (Directory.Exists(targetFolder) || copiedFolders.Contains(targetName))
                {
                    targetName = $"{PathUtils.SanitizeFolderName(app.Name)} ({index++})";
                    targetFolder = Path.Combine(packageApps, targetName);
                }

                var executableCount = Directory.EnumerateFiles(sourceDirectory, "*.exe", SearchOption.TopDirectoryOnly).Count();
                if (executableCount > 10)
                {
                    warnings.Add($"{app.Name}：所在目录包含 {executableCount} 个程序，已整目录打包，可能包含无关文件");
                }

                folderJobs.Add((sourceDirectory, targetFolder));
                copiedFolders.Add(targetName);
                converted.Add((app, $"Apps/{targetName}/{Path.GetFileName(executable)}", executable));
            }

            // ---------- 执行复制 ----------
            var totalJobBytes = folderJobs.Sum(j => PathUtils.GetDirectorySize(j.Source));
            var activeTotal = Math.Max(totalJobBytes, 1);
            long doneBytes = 0;
            var folderIndex = 0;

            foreach (var job in folderJobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                folderIndex++;
                var jobName = Path.GetFileName(job.Source);
                var jobBytes = PathUtils.GetDirectorySize(job.Source);

                progress?.Report(new ExportProgress("复制软件", 5 + doneBytes * 70.0 / activeTotal,
                    $"{jobName}（{folderIndex}/{folderJobs.Count}）"));

                var adapter = progress is null ? null : new Progress<TransferProgress>(p =>
                    progress.Report(new ExportProgress("复制软件", 5 + (doneBytes + p.BytesDone) * 70.0 / activeTotal, jobName)));

                await FileTransfer.CopyDirectoryAsync(job.Source, job.Target, adapter, cancellationToken).ConfigureAwait(false);
                doneBytes += jobBytes;
            }

            // ---------- 复制数据库并裁剪 ----------
            progress?.Report(new ExportProgress("写入管理数据", 78, "生成 library.db"));
            var packageDatabase = Path.Combine(packageData, "library.db");
            File.Copy(_paths.DatabaseFile, packageDatabase, overwrite: true);

            var retainedIds = exportable.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            await PruneDatabaseAsync(packageDatabase, retainedIds, converted, cancellationToken).ConfigureAwait(false);

            // ---------- 图标 ----------
            progress?.Report(new ExportProgress("复制图标", 84, "同步图标缓存"));
            foreach (var app in exportable.Where(a => !string.IsNullOrWhiteSpace(a.IconKey)))
            {
                var source = Path.Combine(_paths.IconsDirectory, Path.GetFileName(app.IconKey!));
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Path.Combine(packageIcons, Path.GetFileName(source));
                if (!File.Exists(target))
                {
                    File.Copy(source, target, overwrite: true);
                }
            }

            // ---------- 设置与程序文件 ----------
            if (File.Exists(_paths.SettingsFile))
            {
                File.Copy(_paths.SettingsFile, Path.Combine(packageData, "settings.json"), overwrite: true);
            }

            if (options.IncludeProgramFiles)
            {
                progress?.Report(new ExportProgress("复制程序文件", 88, "打包 HNova Portable 本体"));
                await CopyProgramFilesAsync(staging, cancellationToken).ConfigureAwait(false);
            }

            // ---------- 校验数据库 ----------
            var integrity = VerifyDatabase(packageDatabase);
            if (!integrity.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return new ExportResult { Error = "导出库文件校验失败：" + integrity, Warnings = warnings, Elapsed = stopwatch.Elapsed };
            }

            // ---------- 清单 ----------
            var (fileCount, totalBytes) = CountFiles(staging);
            var manifest = new ExportManifest
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                LibraryId = await ReadLibraryIdAsync(cancellationToken).ConfigureAwait(false),
                SchemaVersion = Database.CurrentSchemaVersion,
                AppCount = retainedIds.Count,
                FileCount = fileCount,
                TotalBytes = totalBytes,
                DatabaseSha256 = ComputeSha256(packageDatabase),
                IncludeProgramFiles = options.IncludeProgramFiles,
                Warnings = warnings,
            };

            await File.WriteAllTextAsync(
                Path.Combine(staging, ManifestFileName),
                JsonSerializer.Serialize(manifest, ManifestJson),
                cancellationToken).ConfigureAwait(false);

            // ---------- 打包 ----------
            string finalTarget = staging;
            if (!isFolder)
            {
                progress?.Report(new ExportProgress("压缩", 92, "正在生成压缩包"));
                finalTarget = Path.GetFullPath(options.TargetPath);

                if (options.Format == ExportFormat.Zip)
                {
                    await CreateZipAsync(staging, finalTarget, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var sevenZip = Find7Zip();
                    if (sevenZip is null)
                    {
                        return new ExportResult
                        {
                            Error = "未找到 7z.exe，请改用 ZIP 格式，或把 7z.exe 放到程序目录下",
                            Warnings = warnings,
                            Elapsed = stopwatch.Elapsed,
                        };
                    }

                    await Run7ZipAsync(sevenZip, staging, finalTarget, cancellationToken).ConfigureAwait(false);
                }
            }

            // ---------- 结果校验 ----------
            progress?.Report(new ExportProgress("校验", 97, "核对导出结果"));
            var verifyMessage = await VerifyPackageAsync(finalTarget, isFolder, options.Format, fileCount, cancellationToken)
                .ConfigureAwait(false);
            if (verifyMessage is not null)
            {
                warnings.Add(verifyMessage);
            }

            if (!isFolder)
            {
                FileTransfer.TryDeleteDirectory(staging);
            }

            progress?.Report(new ExportProgress("完成", 100, "导出完成"));

            return new ExportResult
            {
                Success = true,
                TargetPath = finalTarget,
                AppCount = retainedIds.Count,
                FileCount = fileCount,
                TotalBytes = totalBytes,
                Elapsed = stopwatch.Elapsed,
                Warnings = warnings,
            };
        }
        catch (OperationCanceledException)
        {
            CleanupAfterFailure(isFolder, createdTargetFolder, staging);
            return new ExportResult { Error = "操作已取消", Warnings = warnings, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            CleanupAfterFailure(isFolder, createdTargetFolder, staging);
            return new ExportResult { Error = ex.Message, Warnings = warnings, Elapsed = stopwatch.Elapsed };
        }

        static void CleanupAfterFailure(bool isFolder, bool createdTargetFolder, string staging)
        {
            if (!isFolder || createdTargetFolder)
            {
                FileTransfer.TryDeleteDirectory(staging);
            }
        }
    }

    private static bool EnsureTargetIsUsable(ExportOptions options, bool isFolder)
    {
        if (isFolder)
        {
            var target = Path.GetFullPath(options.TargetPath);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                throw new IOException("目标文件夹已存在且不为空，请选择新的目录");
            }

            var created = !Directory.Exists(target);
            Directory.CreateDirectory(target);
            return created;
        }

        var file = Path.GetFullPath(options.TargetPath);
        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(file))
        {
            File.Delete(file);
        }

        return false;
    }

    private static async Task PruneDatabaseAsync(
        string packageDatabase,
        HashSet<string> retainedIds,
        IReadOnlyList<(AppEntry App, string PortablePath, string? IconSource)> converted,
        CancellationToken cancellationToken)
    {
        await using var connection = Database.OpenAt(packageDatabase);

        await using (var delete = connection.CreateCommand())
        {
            var names = new List<string>();
            var index = 0;
            foreach (var id in retainedIds)
            {
                var name = "$id" + index++;
                names.Add(name);
                delete.Parameters.AddWithValue(name, id);
            }

            delete.CommandText = $"DELETE FROM apps WHERE id NOT IN ({string.Join(",", names)});";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            delete.CommandText = "DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM app_tags);";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (app, portablePath, _) in converted)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE apps SET mode = 0, rel_path = $relPath, linked_path = NULL, health = 0,
                       portability_flags = portability_flags & ~4
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$relPath", portablePath);
            update.Parameters.AddWithValue("$id", app.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var meta = connection.CreateCommand())
        {
            meta.CommandText = """
                INSERT INTO meta (key, value) VALUES ('last_export_utc', $now)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            meta.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CopyProgramFilesAsync(string staging, CancellationToken cancellationToken)
    {
        var programDirectory = AppPaths.ProgramDirectory;
        var target = staging;

        foreach (var file in Directory.EnumerateFiles(programDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(programDirectory, file);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, '/')[0];

            if (firstSegment.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                firstSegment.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
                firstSegment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                firstSegment.Equals("backup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Copy(file, destination, overwrite: true);
            }
        }

        await Task.CompletedTask;
    }

    private static async Task CreateZipAsync(string sourceDirectory, string zipPath, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).ToList();
            var total = files.Count;
            var index = 0;

            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                var relative = Path.GetRelativePath(sourceDirectory, file);
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);

                if (index % 20 == 0 || index == total)
                {
                    progress?.Report(new ExportProgress("压缩", 92 + index * 7.0 / Math.Max(total, 1), relative));
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task Run7ZipAsync(string sevenZip, string sourceDirectory, string targetFile, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZip,
            Arguments = $"a -t7z \"{targetFile}\" \".\\*\" -mx=5 -y",
            WorkingDirectory = sourceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(startInfo) ?? throw new IOException("无法启动 7z.exe");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new IOException($"7-Zip 压缩失败（退出码 {process.ExitCode}）");
        }
    }

    private static string? Find7Zip()
    {
        var local = Path.Combine(AppPaths.ProgramDirectory, "7z.exe");
        if (File.Exists(local))
        {
            return local;
        }

        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var candidate = Path.Combine(path.Trim(), "7z.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\7-Zip\7z.exe",
                     @"C:\Program Files (x86)\7-Zip\7z.exe",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<string?> VerifyPackageAsync(string target, bool isFolder, ExportFormat format, int expectedFileCount, CancellationToken cancellationToken)
    {
        if (isFolder)
        {
            var (count, _) = CountFiles(target);
            return count >= expectedFileCount
                ? null
                : $"文件数量校验异常：清单 {expectedFileCount}，实际 {count}";
        }

        if (format != ExportFormat.Zip)
        {
            return File.Exists(target) && new FileInfo(target).Length > 0 ? null : "压缩包为空";
        }

        try
        {
            await using var stream = File.OpenRead(target);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var count = archive.Entries.Count;
            return count >= expectedFileCount ? null : $"压缩包条目数少于预期（{count} < {expectedFileCount}）";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return "压缩包无法读取：" + ex.Message;
        }
    }

    private static (int FileCount, long TotalBytes) CountFiles(string directory)
    {
        var count = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (count, bytes);
    }

    private static string VerifyDatabase(string databasePath)
    {
        try
        {
            using var connection = Database.OpenAt(databasePath, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return command.ExecuteScalar()?.ToString() ?? "unknown";
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return ex.Message;
        }
    }

    private async Task<string> ReadLibraryIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Database.OpenAt(_paths.DatabaseFile, readOnly: true);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'library_id' LIMIT 1;";
            return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?.ToString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return string.Empty;
        }
    }

    private static string ComputeSha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>导出的库包清单，导入端据此做校验与提示。</summary>
public sealed class ExportManifest
{
    public int FormatVersion { get; set; } = 1;

    public string Kind { get; set; } = "HNovaPortable.LibraryPackage";

    public DateTimeOffset CreatedUtc { get; set; }

    public string LibraryId { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public int AppCount { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public string DatabaseSha256 { get; set; } = string.Empty;

    public bool IncludeProgramFiles { get; set; }

    public List<string> Warnings { get; set; } = [];

    private static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string FileName = "export-manifest.json";

    public static ExportManifest? TryLoad(string packageRoot)
    {
        var path = Path.Combine(packageRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(path), LoadOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
