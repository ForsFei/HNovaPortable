using System.Diagnostics;
using System.IO;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;

namespace HNovaPortable.Services;

public enum LaunchOutcome
{
    Started,
    Missing,
    Failed,
}

/// <summary>软件启动与环境定位。托管软件的工作目录同样使用库内相对路径。</summary>
public sealed class LaunchService(AppPaths paths, AppRepository appRepository)
{
    private readonly AppPaths _paths = paths;
    private readonly AppRepository _apps = appRepository;

    public string? ResolveExecutable(AppEntry entry) => entry.ResolveExecutablePath(_paths.Root);

    public string? ResolveWorkingDirectory(AppEntry entry)
    {
        var executable = ResolveExecutable(entry);
        if (executable is null)
        {
            return null;
        }

        var defaultDirectory = Path.GetDirectoryName(executable);

        if (string.IsNullOrWhiteSpace(entry.WorkDirectory))
        {
            return defaultDirectory;
        }

        if (Path.IsPathRooted(entry.WorkDirectory))
        {
            return Directory.Exists(entry.WorkDirectory) ? entry.WorkDirectory : defaultDirectory;
        }

        var candidate = PathUtils.Resolve(_paths.Root, entry.WorkDirectory);
        return Directory.Exists(candidate) ? candidate : defaultDirectory;
    }

    public async Task<LaunchOutcome> LaunchAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable(entry);
        if (executable is null)
        {
            return LaunchOutcome.Missing;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = entry.Arguments ?? string.Empty,
                WorkingDirectory = ResolveWorkingDirectory(entry) ?? Path.GetDirectoryName(executable) ?? _paths.Root,
                UseShellExecute = true,
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return LaunchOutcome.Failed;
            }

            await _apps.RecordLaunchAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            return LaunchOutcome.Started;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return LaunchOutcome.Failed;
        }
    }

    /// <summary>在资源管理器中选中程序；程序不存在时打开其所在目录。</summary>
    public bool RevealInExplorer(AppEntry entry)
    {
        var executable = ResolveExecutable(entry);
        var target = executable ?? Path.GetDirectoryName(entry.LinkedPath ?? string.Empty);

        try
        {
            if (!string.IsNullOrWhiteSpace(executable))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{executable}\"",
                    UseShellExecute = true,
                });
                return true;
            }

            var directory = ResolveLinkedDirectory(entry);
            if (directory is not null && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = true,
                });
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(target);
    }

    private string? ResolveLinkedDirectory(AppEntry entry)
    {
        if (entry.Mode == AppLocationMode.Managed && !string.IsNullOrWhiteSpace(entry.RelativePath))
        {
            var full = PathUtils.Resolve(_paths.Root, entry.RelativePath);
            var directory = Path.GetDirectoryName(full);
            return directory is not null && Directory.Exists(directory) ? directory : null;
        }

        if (!string.IsNullOrWhiteSpace(entry.LinkedPath))
        {
            var directory = Path.GetDirectoryName(entry.LinkedPath);
            return directory is not null && Directory.Exists(directory) ? directory : null;
        }

        return null;
    }
}
