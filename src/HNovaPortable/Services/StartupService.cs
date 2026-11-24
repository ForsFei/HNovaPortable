using System.IO;
using System.Runtime.InteropServices;
using HNovaPortable.Core;
using HNovaPortable.Models;

namespace HNovaPortable.Services;

internal static class StartupService
{
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

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            $"HNovaPortable-{id:N}.lnk");
    }
}
