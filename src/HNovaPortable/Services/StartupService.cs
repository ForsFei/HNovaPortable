using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HNovaPortable.Core;
using HNovaPortable.Models;
using HNovaPortable.Data;
using Microsoft.Data.Sqlite;

namespace HNovaPortable.Services;

internal static class StartupService
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out var id) || !File.Exists(ShortcutPath(id)))
        {
            return 0;
        }

        try
        {
            var paths = AppPaths.ForStartup(args);
            var apps = new AppRepository(new Database(paths));
            var stoppedAt = Stopwatch.StartNew();
            AppEntry? entry = null;
            do
            {
                try
                {
                    if (File.Exists(paths.DatabaseFile))
                    {
                        entry = await apps.GetForStartupAsync(args[1]).ConfigureAwait(false);
                        if (entry is null || entry.ResolveExecutablePath(paths.Root) is not null)
                        {
                            break;
                        }
                    }
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 or 10 or 14)
                {
                    // 登录时磁盘可能还未就绪，或数据库尚被另一实例短暂占用。
                }

                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            while (stoppedAt.Elapsed < TimeSpan.FromSeconds(20));

            if (entry is null || !IsEnabled(entry))
            {
                return 0;
            }

            var result = await new LaunchService(paths, apps).LaunchAsync(entry).ConfigureAwait(false);
            return result == LaunchOutcome.Started ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
        {
            Trace.TraceWarning("登录自启失败：{0}", ex.Message);
            return 1;
        }
    }

    public static bool IsEnabled(AppEntry entry) => File.Exists(ShortcutPath(entry));

    public static void SetEnabled(AppEntry entry, AppPaths paths, bool enabled)
    {
        var shortcutPath = ShortcutPath(entry);
        if (!enabled)
        {
            File.Delete(shortcutPath);
            return;
        }

        var host = Environment.ProcessPath;
        if (host is null || !Path.GetFileName(host).Equals("HNovaPortable.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请从 HNovaPortable.exe 启动后再设置自启。");
        }

        if (entry.ResolveExecutablePath(paths.Root) is null)
        {
            throw new FileNotFoundException("软件文件不存在。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = host;
                shortcut.Arguments = $"--startup-app {entry.Id} --root \"{Path.Combine(paths.Root, ".")}\"";
                shortcut.WorkingDirectory = AppPaths.ProgramDirectory;
                shortcut.Description = entry.Name;
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string ShortcutPath(AppEntry entry)
    {
        if (!Guid.TryParse(entry.Id, out var id))
        {
            throw new InvalidOperationException("软件标识无效。");
        }

        return ShortcutPath(id);
    }

    private static string ShortcutPath(Guid id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), $"HNovaPortable-{id:N}.lnk");
}
