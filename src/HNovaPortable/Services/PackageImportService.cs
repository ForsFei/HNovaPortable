using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;
using Microsoft.Data.Sqlite;

namespace HNovaPortable.Services;

public readonly record struct PackageImportProgress(string Stage, double Percent, string Detail);

public sealed class PackageImportResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public int Imported { get; init; }

    public int Skipped { get; init; }

    public List<string> Warnings { get; init; } = [];

    public TimeSpan Elapsed { get; init; }

    public string Report()
    {
        if (!Success)
        {
            return "导入失败：" + Error;
        }

        var lines = new List<string>
        {
            $"已导入 {Imported} 个软件" + (Skipped > 0 ? $"，跳过 {Skipped} 个" : string.Empty),
            $"耗时 {Elapsed.TotalSeconds:0.0} 秒",
        };

        if (Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("注意事项：");
            lines.AddRange(Warnings.Take(20).Select(w => "· " + w));
            if (Warnings.Count > 20)
            {
                lines.Add($"… 另有 {Warnings.Count - 20} 条");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 导入库包（文件夹 / ZIP / 7z）：把包内 Apps 复制进当前库，
/// 并以“复制后的实际位置”重新计算相对路径，而不是相信包里的字符串。
/// </summary>
public sealed class PackageImportService(
    AppPaths paths,
    AppRepository appRepository,
    CategoryRepository categoryRepository,
    IconService iconService)
{
    private readonly AppPaths _paths = paths;
    private readonly AppRepository _apps = appRepository;
    private readonly CategoryRepository _categories = categoryRepository;
    private readonly IconService _icons = iconService;

    public async Task<PackageImportResult> ImportAsync(string packagePath, IProgress<PackageImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? temporaryRoot = null;
        var warnings = new List<string>();

        try
        {
            progress?.Report(new PackageImportProgress("准备", 0, "正在打开库包"));

            var full = Path.GetFullPath(packagePath);
            string root;

            if (Directory.Exists(full))
            {
                root = LocatePackageRoot(full);
            }
            else if (File.Exists(full))
            {
                var extension = Path.GetExtension(full).ToLowerInvariant();
                temporaryRoot = Path.Combine(_paths.CacheDirectory, "import-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(temporaryRoot);

                if (extension == ".zip")
                {
                    await Task.Run(() => ZipFile.ExtractToDirectory(full, temporaryRoot, overwriteFiles: true), cancellationToken).ConfigureAwait(false);
                }
                else if (extension == ".7z")
                {
                    var sevenZip = Find7Zip();
                    if (sevenZip is null)
                    {
                        return new PackageImportResult { Error = "导入 .7z 需要 7z.exe，请改用 ZIP 或把 7z.exe 放到程序目录" };
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = sevenZip,
                        Arguments = $"x \"{full}\" -o\"{temporaryRoot}\" -y",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var process = Process.Start(startInfo) ?? throw new IOException("无法启动 7z.exe");
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    if (process.ExitCode != 0)
                    {
                        return new PackageImportResult { Error = $"解压失败（7-Zip 退出码 {process.ExitCode}）" };
                    }
                }
                else
                {
                    return new PackageImportResult { Error = "仅支持文件夹、.zip 或 .7z 格式的库包" };
                }

                root = LocatePackageRoot(temporaryRoot);
            }
            else
            {
                return new PackageImportResult { Error = "库包路径不存在" };
            }

            var packageDatabase = Path.Combine(root, "Data", "library.db");
            if (!File.Exists(packageDatabase))
            {
                return new PackageImportResult { Error = "所选位置不是有效的库包（缺少 Data/library.db）" };
            }

            var manifest = ExportManifest.TryLoad(root);
            if (manifest is not null && manifest.SchemaVersion > Database.CurrentSchemaVersion)
            {
                warnings.Add($"库包结构版本较新（v{manifest.SchemaVersion}），导入可能不完整");
            }

            progress?.Report(new PackageImportProgress("读取条目", 5, "正在解析库包内容"));
            var packageApps = await ReadPackageAppsAsync(packageDatabase, cancellationToken).ConfigureAwait(false);
            if (packageApps.Count == 0)
            {
                return new PackageImportResult { Error = "库包里没有软件记录", Warnings = warnings };
            }

            var existingIds = await _apps.ListIdsAsync(cancellationToken).ConfigureAwait(false);
            var localCategories = await _categories.ListAsync(cancellationToken).ConfigureAwait(false);
            var categoryByName = localCategories
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            var copiedSegments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var imported = 0;
            var skipped = 0;

            for (var index = 0; index < packageApps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageApp = packageApps[index];
                var percent = 8 + index * 88.0 / packageApps.Count;
                progress?.Report(new PackageImportProgress("导入软件", percent, packageApp.Name));

                var mode = (AppLocationMode)packageApp.Mode;
                string? relativePath = null;
                string? linkedPath = null;
                var health = AppHealth.Ok;

                if (mode == AppLocationMode.Managed)
                {
                    var segments = (packageApp.RelativePath ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length < 3)
                    {
                        skipped++;
                        warnings.Add($"{packageApp.Name}：缺少有效的托管路径，已跳过");
                        continue;
                    }

                    var segment = segments[1];
                    var sourceFolder = Path.Combine(root, "Apps", segment);
                    if (!Directory.Exists(sourceFolder))
                    {
                        skipped++;
                        warnings.Add($"{packageApp.Name}：库包内缺少目录 Apps/{segment}，已跳过");
                        continue;
                    }

                    if (!copiedSegments.TryGetValue(segment, out var targetName))
                    {
                        var targetDirectory = PathUtils.GetUniqueDirectoryPath(_paths.AppsDirectory, segment);
                        var adapter = progress is null ? null : new Progress<TransferProgress>(p =>
                            progress.Report(new PackageImportProgress("复制软件", percent, $"{segment} · {p.Percent:0}%")));

                        await FileTransfer.CopyDirectoryAsync(sourceFolder, targetDirectory, adapter, cancellationToken).ConfigureAwait(false);
                        targetName = Path.GetFileName(targetDirectory);
                        copiedSegments[segment] = targetName;
                    }

                    var rest = string.Join('/', segments.Skip(2));
                    relativePath = $"Apps/{targetName}/{rest}";

                    // 以复制后的实际位置做存在性校验
                    if (!File.Exists(Path.Combine(_paths.Root, PathUtils.ToNative(relativePath))))
                    {
                        health = AppHealth.Missing;
                        warnings.Add($"{packageApp.Name}：复制后未找到主程序（{relativePath}）");
                    }
                }
                else
                {
                    var originalPath = packageApp.LinkedPath ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(originalPath) && PathUtils.IsInside(root, originalPath) && File.Exists(originalPath))
                    {
                        // 包内自带的“引用”条目：转成托管
                        var sourceDirectory = Path.GetDirectoryName(originalPath)!;
                        var targetDirectory = PathUtils.GetUniqueDirectoryPath(_paths.AppsDirectory, PathUtils.SanitizeFolderName(packageApp.Name));
                        await FileTransfer.CopyDirectoryAsync(sourceDirectory, targetDirectory, null, cancellationToken).ConfigureAwait(false);
                        relativePath = $"Apps/{Path.GetFileName(targetDirectory)}/{Path.GetFileName(originalPath)}";
                        mode = AppLocationMode.Managed;
                    }
                    else
                    {
                        linkedPath = originalPath;
                        health = AppHealth.NeedsRelocate;
                        warnings.Add($"{packageApp.Name}：引用路径不在库包内（{originalPath}），迁移后需手动重新定位");
                    }
                }

                var newId = existingIds.Add(packageApp.Id) ? packageApp.Id : Guid.NewGuid().ToString("N");

                string? categoryId = null;
                if (!string.IsNullOrWhiteSpace(packageApp.CategoryName))
                {
                    if (!categoryByName.TryGetValue(packageApp.CategoryName, out var existingCategory))
                    {
                        existingCategory = await _categories.GetOrCreateByNameAsync(packageApp.CategoryName, cancellationToken).ConfigureAwait(false);
                        categoryByName[packageApp.CategoryName] = existingCategory;
                    }

                    categoryId = existingCategory;
                }

                string? iconKey = null;
                if (!string.IsNullOrWhiteSpace(packageApp.IconKey))
                {
                    var sourceIcon = Path.Combine(root, "Data", "icons", Path.GetFileName(packageApp.IconKey));
                    if (File.Exists(sourceIcon))
                    {
                        var targetIcon = Path.Combine(_paths.IconsDirectory, newId + ".png");
                        try
                        {
                            Directory.CreateDirectory(_paths.IconsDirectory);
                            File.Copy(sourceIcon, targetIcon, overwrite: true);
                            iconKey = newId + ".png";
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            warnings.Add($"{packageApp.Name}：图标复制失败（{ex.Message}）");
                        }
                    }
                }

                if (iconKey is null)
                {
                    var executable = relativePath is not null
                        ? Path.Combine(_paths.Root, PathUtils.ToNative(relativePath))
                        : linkedPath;

                    if (executable is not null && File.Exists(executable))
                    {
                        iconKey = await _icons.CaptureAsync(executable, newId, cancellationToken).ConfigureAwait(false);
                    }
                }

                var entry = new AppEntry
                {
                    Id = newId,
                    Name = packageApp.Name,
                    Mode = mode,
                    RelativePath = relativePath,
                    LinkedPath = linkedPath,
                    WorkDirectory = mode == AppLocationMode.Managed && relativePath is not null
                        ? PathUtils.GetParentPortable(relativePath)
                        : packageApp.WorkDirectory,
                    Arguments = packageApp.Arguments,
                    CategoryId = categoryId,
                    IsFavorite = packageApp.IsFavorite,
                    Notes = packageApp.Notes,
                    IconKey = iconKey,
                    ExeVersion = packageApp.ExeVersion,
                    ProductName = packageApp.ProductName,
                    Company = packageApp.Company,
                    Description = packageApp.Description,
                    SizeBytes = packageApp.SizeBytes,
                    Health = health,
                    PortabilityFlags = (PortabilityFlags)packageApp.PortabilityFlags,
                    LastScanUtc = DateTimeOffset.UtcNow,
                };

                await _apps.UpsertAsync(entry, packageApp.Tags, cancellationToken).ConfigureAwait(false);
                imported++;
            }

            progress?.Report(new PackageImportProgress("完成", 100, "导入完成"));

            return new PackageImportResult
            {
                Success = true,
                Imported = imported,
                Skipped = skipped,
                Warnings = warnings,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            return new PackageImportResult { Error = "操作已取消", Warnings = warnings, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            return new PackageImportResult { Error = ex.Message, Warnings = warnings, Elapsed = stopwatch.Elapsed };
        }
        finally
        {
            if (temporaryRoot is not null)
            {
                FileTransfer.TryDeleteDirectory(temporaryRoot);
            }
        }
    }

    private static string LocatePackageRoot(string directory)
    {
        if (File.Exists(Path.Combine(directory, "Data", "library.db")))
        {
            return directory;
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                if (File.Exists(Path.Combine(sub, "Data", "library.db")))
                {
                    return sub;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return directory;
    }

    private static async Task<List<PackageApp>> ReadPackageAppsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = Database.OpenAt(databasePath, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.name, a.mode, a.rel_path, a.linked_path, a.work_dir, a.arguments,
                   IFNULL(c.name, ''), a.is_favorite, IFNULL(a.notes, ''), IFNULL(a.icon_key, ''),
                   IFNULL(a.exe_version, ''), IFNULL(a.product_name, ''), IFNULL(a.company, ''),
                   IFNULL(a.description, ''), a.size_bytes, a.portability_flags,
                   (SELECT GROUP_CONCAT(t.name, char(31)) FROM app_tags at JOIN tags t ON t.id = at.tag_id WHERE at.app_id = a.id)
            FROM apps a
            LEFT JOIN categories c ON c.id = a.category_id
            ORDER BY a.sort_name, a.name;
            """;

        var result = new List<PackageApp>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tags = reader.IsDBNull(17) ? null : reader.GetString(17);
            result.Add(new PackageApp
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Mode = reader.GetInt32(2),
                RelativePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                LinkedPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                WorkDirectory = reader.IsDBNull(5) ? null : reader.GetString(5),
                Arguments = reader.IsDBNull(6) ? null : reader.GetString(6),
                CategoryName = reader.GetString(7),
                IsFavorite = reader.GetInt32(8) != 0,
                Notes = reader.IsDBNull(9) ? null : reader.GetString(9),
                IconKey = reader.IsDBNull(10) ? null : reader.GetString(10),
                ExeVersion = reader.IsDBNull(11) ? null : reader.GetString(11),
                ProductName = reader.IsDBNull(12) ? null : reader.GetString(12),
                Company = reader.IsDBNull(13) ? null : reader.GetString(13),
                Description = reader.IsDBNull(14) ? null : reader.GetString(14),
                SizeBytes = reader.GetInt64(15),
                PortabilityFlags = reader.GetInt32(16),
                Tags = string.IsNullOrEmpty(tags) ? [] : tags.Split('\u001f', StringSplitOptions.RemoveEmptyEntries),
            });
        }

        return result;
    }

    private static string? Find7Zip()
    {
        var local = Path.Combine(AppPaths.ProgramDirectory, "7z.exe");
        if (File.Exists(local))
        {
            return local;
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

    private sealed class PackageApp
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public int Mode { get; init; }

        public string? RelativePath { get; init; }

        public string? LinkedPath { get; init; }

        public string? WorkDirectory { get; init; }

        public string? Arguments { get; init; }

        public string CategoryName { get; init; } = string.Empty;

        public bool IsFavorite { get; init; }

        public string? Notes { get; init; }

        public string? IconKey { get; init; }

        public string? ExeVersion { get; init; }

        public string? ProductName { get; init; }

        public string? Company { get; init; }

        public string? Description { get; init; }

        public long SizeBytes { get; init; }

        public int PortabilityFlags { get; init; }

        public IReadOnlyList<string> Tags { get; init; } = [];
    }
}
