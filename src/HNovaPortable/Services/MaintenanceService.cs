using System.IO;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;

namespace HNovaPortable.Services;

public readonly record struct MaintenanceProgress(string Stage, double Percent, string Detail);

public sealed class RescanSummary
{
    public int Checked { get; set; }

    public int Healthy { get; set; }

    public int Missing { get; set; }

    public int VersionChanged { get; set; }

    public int Updated { get; set; }

    public List<string> Messages { get; } = [];

    public string ToReport()
    {
        var lines = new List<string>
        {
            $"检查 {Checked} 个软件：正常 {Healthy}，文件缺失 {Missing}，版本变化 {VersionChanged}",
        };

        if (Messages.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(Messages.Take(20));
            if (Messages.Count > 20)
            {
                lines.Add($"… 另有 {Messages.Count - 20} 条");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 软件库维护：重扫描、失效检测、重新定位、备份与恢复。
/// 所有操作都不主动删除用户文件。
/// </summary>
public sealed class MaintenanceService(AppPaths paths, AppRepository appRepository, IconService iconService)
{
    private readonly AppPaths _paths = paths;
    private readonly AppRepository _apps = appRepository;
    private readonly IconService _icons = iconService;

    /// <summary>重扫描全部条目：检查文件存在性、版本与体积变化，并回写健康状态。</summary>
    public async Task<RescanSummary> RescanAsync(IProgress<MaintenanceProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var summary = new RescanSummary();
        var apps = await _apps.ListAsync(new AppQuery(), cancellationToken).ConfigureAwait(false);
        summary.Checked = apps.Count;

        var index = 0;
        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new MaintenanceProgress("检查软件", apps.Count == 0 ? 100 : index * 100.0 / apps.Count, app.Name));

            var executable = app.ResolveExecutablePath(_paths.Root);
            if (executable is null)
            {
                summary.Missing++;
                summary.Messages.Add($"{app.Name}：找不到程序文件（{app.Mode switch { AppLocationMode.Managed => app.RelativePath, _ => app.LinkedPath }}）");
                await _apps.UpdateHealthAsync(app.Id, AppHealth.Missing, null, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var metadata = ExecutableMetadata.Read(executable);
            long size;
            try
            {
                size = new FileInfo(executable).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                size = 0;
            }

            var versionChanged = !string.IsNullOrWhiteSpace(app.ExeVersion)
                                 && !string.IsNullOrWhiteSpace(metadata.FileVersion)
                                 && !string.Equals(app.ExeVersion, metadata.FileVersion, StringComparison.OrdinalIgnoreCase);

            if (versionChanged)
            {
                summary.VersionChanged++;
                summary.Messages.Add($"{app.Name}：版本 {app.ExeVersion} → {metadata.FileVersion}");
                await _apps.UpdateHealthAsync(app.Id, AppHealth.VersionChanged, metadata.FileVersion, size > 0 ? size : null, cancellationToken).ConfigureAwait(false);
                summary.Updated++;
            }
            else
            {
                summary.Healthy++;
                await _apps.UpdateHealthAsync(app.Id, AppHealth.Ok, metadata.FileVersion, size > 0 ? size : null, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new MaintenanceProgress("完成", 100, "检查结束"));
        return summary;
    }

    /// <summary>把某个条目重新指向新的程序目录（库内 → 托管相对路径，库外 → 引用模式）。</summary>
    public async Task<(bool Success, string Message)> RelocateAsync(AppEntry app, string newExecutablePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(newExecutablePath))
        {
            return (false, "选择的程序不存在");
        }

        var full = Path.GetFullPath(newExecutablePath);
        var portable = PathUtils.ToPortableIfInside(_paths.Root, full);
        var flags = app.PortabilityFlags;

        if (portable is not null)
        {
            flags &= ~PortabilityFlags.LinkedOutsideLibrary;
        }
        else
        {
            flags |= PortabilityFlags.LinkedOutsideLibrary;
        }

        var iconKey = await _icons.CaptureAsync(full, app.Id, cancellationToken).ConfigureAwait(false) ?? app.IconKey;
        var metadata = ExecutableMetadata.Read(full);

        await _apps.UpsertAsync(new AppEntry
        {
            Id = app.Id,
            Name = app.Name,
            Mode = portable is not null ? AppLocationMode.Managed : AppLocationMode.Linked,
            RelativePath = portable,
            LinkedPath = portable is null ? full : null,
            WorkDirectory = portable is not null ? PathUtils.GetParentPortable(portable) : Path.GetDirectoryName(full),
            Arguments = app.Arguments,
            CategoryId = app.CategoryId,
            IsFavorite = app.IsFavorite,
            Notes = app.Notes,
            GithubUrl = app.GithubUrl,
            WebsiteUrl = app.WebsiteUrl,
            IconKey = iconKey,
            SizeBytes = app.SizeBytes,
            Health = AppHealth.Ok,
            PortabilityFlags = flags,
            LaunchCount = app.LaunchCount,
            LastLaunchUtc = app.LastLaunchUtc,
            ExeVersion = metadata.FileVersion ?? app.ExeVersion,
            ProductName = metadata.ProductName ?? app.ProductName,
            Company = metadata.Company ?? app.Company,
            Description = metadata.FileDescription ?? app.Description,
            CreatedUtc = app.CreatedUtc,
            LastScanUtc = DateTimeOffset.UtcNow,
        }, app.Tags, cancellationToken).ConfigureAwait(false);

        return (true, portable is not null ? "已更新为库内路径：" + portable : "已更新为引用路径：" + full);
    }

    /// <summary>备份 Data（可选包含 Apps）。备份目录结构可直接作为库使用。</summary>
    public async Task<string> BackupAsync(bool includeApps, IProgress<MaintenanceProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(_paths.BackupsDirectory, "backup-" + stamp);
        Directory.CreateDirectory(target);

        progress?.Report(new MaintenanceProgress("备份数据库", 5, target));

        var dataTarget = Path.Combine(target, "Data");
        Directory.CreateDirectory(dataTarget);

        var databaseTarget = Path.Combine(dataTarget, "library.db");
        File.Copy(_paths.DatabaseFile, databaseTarget, overwrite: true);

        if (File.Exists(_paths.SettingsFile))
        {
            File.Copy(_paths.SettingsFile, Path.Combine(dataTarget, "settings.json"), overwrite: true);
        }

        if (Directory.Exists(_paths.IconsDirectory))
        {
            await FileTransfer.CopyDirectoryAsync(_paths.IconsDirectory, Path.Combine(dataTarget, "icons"), null, cancellationToken).ConfigureAwait(false);
        }

        if (includeApps)
        {
            progress?.Report(new MaintenanceProgress("备份软件目录", 15, "复制 Apps（可能较慢）"));
            var adapter = progress is null ? null : new Progress<TransferProgress>(p =>
                progress.Report(new MaintenanceProgress("备份软件目录", 15 + p.Percent * 0.85, p.CurrentFile)));

            await FileTransfer.CopyDirectoryAsync(_paths.AppsDirectory, Path.Combine(target, "Apps"), adapter, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new MaintenanceProgress("完成", 100, "备份完成"));
        return target;
    }

    /// <summary>从备份目录恢复数据库与设置（恢复前自动备份当前库）。</summary>
    public async Task<(bool Success, string Message)> RestoreAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var sourceDatabase = Path.Combine(backupDirectory, "Data", "library.db");
        if (!File.Exists(sourceDatabase))
        {
            sourceDatabase = Path.Combine(backupDirectory, "library.db");
        }

        if (!File.Exists(sourceDatabase))
        {
            return (false, "所选目录不是有效的备份（缺少 library.db）");
        }

        await Task.Run(() =>
        {
            var safety = Path.Combine(_paths.BackupsDirectory, "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db");
            if (File.Exists(_paths.DatabaseFile))
            {
                File.Copy(_paths.DatabaseFile, safety, overwrite: true);
            }
        }, cancellationToken).ConfigureAwait(false);

        File.Copy(sourceDatabase, _paths.DatabaseFile, overwrite: true);

        var sourceSettings = Path.Combine(backupDirectory, "Data", "settings.json");
        if (File.Exists(sourceSettings))
        {
            File.Copy(sourceSettings, _paths.SettingsFile, overwrite: true);
        }

        return (true, "恢复完成，请重启 HNova Portable 以重新载入软件库。");
    }

    public IReadOnlyList<string> ListBackups()
    {
        try
        {
            return Directory.EnumerateDirectories(_paths.BackupsDirectory)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
