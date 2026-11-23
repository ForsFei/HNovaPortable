using System.IO;

namespace HNovaPortable.Core;

/// <summary>
/// 解析并管理“软件库根目录”。这是整个应用可移动性的唯一真相来源，
/// 库内所有路径都以 <see cref="Root"/> 为基准表达。
/// </summary>
public sealed class AppPaths
{
    public const string RootSwitch = "--root";
    public const string LinkFileName = "link.root";

    private static readonly string[] RequiredDirectories = ["Apps", "Data", "Data/icons", "Data/cache", "Data/backups", "Data/exports"];

    private AppPaths(string root, bool isFallback, string? note)
    {
        Root = root;
        IsFallbackLocation = isFallback;
        LocationNote = note;
    }

    /// <summary>软件库根目录（绝对路径，无尾随分隔符）。</summary>
    public string Root { get; }

    /// <summary>库根不可写时是否降级到了用户目录。</summary>
    public bool IsFallbackLocation { get; }

    /// <summary>库根来源说明，用于状态栏展示。</summary>
    public string? LocationNote { get; }

    public string AppsDirectory => Path.Combine(Root, "Apps");

    public string DataDirectory => Path.Combine(Root, "Data");

    public string IconsDirectory => Path.Combine(DataDirectory, "icons");

    public string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");

    public string ExportsDirectory => Path.Combine(DataDirectory, "exports");

    public string DatabaseFile => Path.Combine(DataDirectory, "library.db");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>程序自身所在目录（发布/复制位置）。</summary>
    public static string ProgramDirectory { get; } = NormalizeDirectory(AppContext.BaseDirectory);

    public static AppPaths Create(IReadOnlyList<string>? args = null)
    {
        var (candidate, note) = ResolveCandidate(args ?? []);
        var failure = TryEnsureStructure(candidate);
        if (failure is null)
        {
            return new AppPaths(candidate, false, note);
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HNovaPortable");

        var fallbackFailure = TryEnsureStructure(fallback);
        if (fallbackFailure is null)
        {
            return new AppPaths(fallback, true, $"库根 {candidate} 不可写（{failure}），已降级到 {fallback}");
        }

        throw new IOException($"无法初始化软件库目录。{candidate}: {failure} / {fallback}: {fallbackFailure}");
    }

    private static (string Root, string? Note) ResolveCandidate(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], RootSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                // 显式指定即视为用户意图：目录不存在时创建，而不是悄悄退回程序目录。
                var explicitRoot = NormalizeDirectory(args[i + 1]);
                return (explicitRoot, $"库根来自命令行参数：{explicitRoot}");
            }
        }

        var linkFile = Path.Combine(ProgramDirectory, "Data", LinkFileName);
        if (File.Exists(linkFile))
        {
            try
            {
                var linked = File.ReadAllText(linkFile).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(linked))
                {
                    var root = NormalizeDirectory(linked);
                    if (Directory.Exists(root))
                    {
                        return (root, $"库根来自 {LinkFileName}：{root}");
                    }
                }
            }
            catch (IOException)
            {
                // 忽略，退回程序目录
            }
        }

        return (ProgramDirectory, null);
    }

    private static string? TryEnsureStructure(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            foreach (var relative in RequiredDirectories)
            {
                Directory.CreateDirectory(Path.Combine(root, relative));
            }

            // 探测真实可写性：只创建目录在部分只读场景下不会报错。
            var probe = Path.Combine(root, "Data", "cache", ".write-probe");
            File.WriteAllText(probe, DateTime.UtcNow.ToString("O"));
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ex.Message;
        }
    }

    public static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        return Path.TrimEndingDirectorySeparator(full);
    }
}
