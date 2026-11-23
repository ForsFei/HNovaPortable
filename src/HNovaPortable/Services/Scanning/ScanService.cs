using System.IO;
using System.Text.RegularExpressions;
using HNovaPortable.Core;
using HNovaPortable.Models;

namespace HNovaPortable.Services.Scanning;

/// <summary>
/// 目录扫描与主程序识别。
/// 核心原则：一个“软件目录”只产出一个软件条目（可切换主程序），
/// 而不是把目录树里所有 EXE 都当成独立软件。
/// </summary>
public sealed partial class ScanService
{
    private const int MaxDepth = 4;
    private const int MaxCandidates = 8;

    /// <summary>这些子目录里的 EXE 大概率是辅助程序。</summary>
    private static readonly HashSet<string> PenalizedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "lib", "libs", "tools", "tool", "resources", "res", "plugins", "plugin",
        "extensions", "sdk", "samples", "docs", "doc", "help", "locale", "locales", "lang",
        "languages", "runtime", "redist", "vcredist", "uninstall", "drivers",
    };

    /// <summary>安装器 / 更新器 / 崩溃上报器等，不可能是主程序。</summary>
    private static readonly string[] ExcludedNameTokens =
    [
        "unins", "uninstall", "setup", "install", "update", "updater", "upgrade",
        "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "crashpad", "crashreport",
        "crashhandler", "crashsender", "7za", "7zg", "ffmpeg", "ffplay", "elevate",
        "notification_helper", "notifications_helper", "createdump", "remove",
    ];

    private static readonly string[] LocalDataDirectoryNames =
    [
        "data", "config", "conf", "settings", "profile", "profiles", "userdata", "user",
        "portable", "cache", "appdata", "documents",
    ];

    public async Task<FolderAnalysis> AnalyzeFolderAsync(string folderPath, CancellationToken cancellationToken = default)
        => await Task.Run(() => AnalyzeFolder(folderPath, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async Task<FolderAnalysis> AnalyzeExeAsync(string exePath, CancellationToken cancellationToken = default)
        => await Task.Run(() => AnalyzeSingleExe(exePath), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 扫描一个“装着很多软件”的根目录：一级子目录各算一个软件，
    /// 根目录下的散装 EXE 每个也算一个独立软件。
    /// </summary>
    public async Task<IReadOnlyList<FolderAnalysis>> ScanRootAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var root = AppPaths.NormalizeDirectory(rootPath);
            var analyses = new List<FolderAnalysis>();

            var subDirectories = new List<string>();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    // 避免把库自身目录当成软件
                    if (string.Equals(info.Name, "Apps", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(info.Name, "Data", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    subDirectories.Add(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return analyses;
            }

            var looseExes = new List<string>();
            try
            {
                looseExes.AddRange(Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            // 根目录本身就是一个软件目录（如 C:\Program Files\7-Zip）：
            // 顶层有 EXE 且子目录里没有 EXE，就按“单个软件”处理，而不是把每个 EXE 拆成一条。
            var looksLikeSingleApp = looseExes.Count is > 0 and <= 6
                                     && !subDirectories.Any(d => ContainsExecutable(d));

            if (looksLikeSingleApp)
            {
                progress?.Report(new ScanProgress(1, 1, "分析 " + Path.GetFileName(root)));
                var single = AnalyzeFolder(root, cancellationToken);
                return single.IsEmpty ? [] : [single];
            }

            var total = subDirectories.Count + looseExes.Count;
            var current = 0;

            foreach (var directory in subDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                var name = Path.GetFileName(directory);
                progress?.Report(new ScanProgress(current, total, $"分析 {name}"));

                var analysis = AnalyzeFolder(directory, cancellationToken);
                if (!analysis.IsEmpty)
                {
                    analyses.Add(analysis);
                }
            }

            foreach (var exe in looseExes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                progress?.Report(new ScanProgress(current, total, $"分析 {Path.GetFileName(exe)}"));

                var analysis = AnalyzeSingleExe(exe);
                if (!analysis.IsEmpty)
                {
                    analyses.Add(analysis);
                }
            }

            progress?.Report(new ScanProgress(total, total, "扫描完成"));

            return (IReadOnlyList<FolderAnalysis>)analyses
                .OrderBy(a => a.SuggestedName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool ContainsExecutable(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FolderAnalysis AnalyzeFolder(string folderPath, CancellationToken cancellationToken)
    {
        var folder = AppPaths.NormalizeDirectory(folderPath);
        var folderName = Path.GetFileName(folder);
        var executableFiles = EnumerateExecutables(folder, cancellationToken);

        var candidates = new List<ExeCandidate>();
        foreach (var (fullPath, relativePath) in executableFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = BuildCandidate(fullPath, relativePath, folderName, executableFiles.Count);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > MaxCandidates)
        {
            candidates = candidates.GetRange(0, MaxCandidates);
        }

        var flags = DetectFolderFlags(folder, executableFiles);
        if (candidates.Count > 0 && candidates[0].IsConsole)
        {
            flags |= PortabilityFlags.ConsoleSubsystem;
        }

        return new FolderAnalysis
        {
            FolderPath = folder,
            SuggestedName = CleanDisplayName(folderName),
            Candidates = candidates,
            TotalSizeBytes = PathUtils.GetDirectorySize(folder),
            ExeCount = executableFiles.Count,
            Flags = flags,
        };
    }

    private static FolderAnalysis AnalyzeSingleExe(string exePath)
    {
        var fullPath = Path.GetFullPath(exePath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var candidate = BuildCandidate(fullPath, Path.GetFileName(fullPath), Path.GetFileName(directory), 1);

        var candidates = new List<ExeCandidate>();
        if (candidate is not null)
        {
            candidates.Add(candidate);
        }

        var flags = PortabilityFlags.None;
        if (candidate?.IsConsole == true)
        {
            flags |= PortabilityFlags.ConsoleSubsystem;
        }

        var name = candidate is not null && !string.IsNullOrWhiteSpace(candidate.ProductName)
            ? candidate.ProductName!
            : Path.GetFileNameWithoutExtension(fullPath);

        long size = 0;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new FolderAnalysis
        {
            FolderPath = directory,
            SuggestedName = CleanDisplayName(name),
            Candidates = candidates,
            TotalSizeBytes = size,
            ExeCount = candidates.Count,
            Flags = flags,
            IsSingleExe = true,
            SingleExePath = fullPath,
        };
    }

    private static List<(string FullPath, string RelativePath)> EnumerateExecutables(string root, CancellationToken cancellationToken)
    {
        var results = new List<(string, string)>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var relative = PathUtils.ToPortable(Path.GetRelativePath(root, file));
                    results.Add((file, relative));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                    {
                        continue;
                    }

                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return results;
    }

    private static ExeCandidate? BuildCandidate(string fullPath, string relativePath, string folderName, int totalExeCount)
    {
        var bareName = Path.GetFileNameWithoutExtension(fullPath);

        if (IsExcludedName(bareName))
        {
            return null;
        }

        long size;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (size <= 0)
        {
            return null;
        }

        var pe = PeInfo.Read(fullPath);
        if (!pe.IsValid)
        {
            return null;
        }

        var (description, product, company, version, hasVersionInfo) = ReadVersionInfo(fullPath);

        var score = 0;
        var reasons = new List<string>();
        var depth = relativePath.Count(c => c == '/');

        if (string.Equals(bareName, folderName, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
            reasons.Add("与目录同名");
        }

        if (depth == 0)
        {
            score += 20;
            reasons.Add("位于根目录");
        }
        else if (depth == 1)
        {
            score += 8;
        }
        else
        {
            score -= 5;
        }

        // 体积参与打分：越大的程序越可能是主体（对数压缩，避免巨型安装包一票独大）
        score += (int)Math.Min(15, Math.Log10(size + 1) * 2.2);

        if (hasVersionInfo)
        {
            score += 15;
            reasons.Add("含版本信息");
        }

        if (pe.IsConsole)
        {
            score -= 15;
            reasons.Add("控制台程序");
        }
        else
        {
            score += 10;
            reasons.Add("图形界面");
        }

        if (IsPenalizedPath(relativePath))
        {
            score -= 15;
            reasons.Add("位于子目录");
        }

        if (totalExeCount == 1)
        {
            score += 10;
            reasons.Add("唯一可执行文件");
        }

        return new ExeCandidate
        {
            FullPath = fullPath,
            RelativePath = relativePath,
            Score = score,
            SizeBytes = size,
            FileDescription = description,
            ProductName = product,
            Company = company,
            FileVersion = version,
            IsConsole = pe.IsConsole,
            Reasons = reasons,
        };
    }

    private static (string? Description, string? Product, string? Company, string? Version, bool HasVersionInfo) ReadVersionInfo(string path)
    {
        var metadata = ExecutableMetadata.Read(path);
        return (metadata.FileDescription, metadata.ProductName, metadata.Company, metadata.FileVersion, metadata.HasVersionInfo);
    }

    private static bool IsExcludedName(string bareName)
    {
        foreach (var token in ExcludedNameTokens)
        {
            if (bareName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPenalizedPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (PenalizedDirectories.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static PortabilityFlags DetectFolderFlags(string folder, IReadOnlyList<(string FullPath, string RelativePath)> executables)
    {
        var flags = PortabilityFlags.None;

        var hasUninstaller = executables.Any(e =>
        {
            var name = Path.GetFileNameWithoutExtension(e.FullPath);
            return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
        });

        if (hasUninstaller)
        {
            flags |= PortabilityFlags.HasUninstaller;
        }

        var hasLocalData = false;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                if (LocalDataDirectoryNames.Contains(Path.GetFileName(directory)))
                {
                    hasLocalData = true;
                    break;
                }
            }

            if (!hasLocalData)
            {
                foreach (var pattern in new[] { "*.ini", "*.cfg", "*.conf", "*.toml", "*.yaml" })
                {
                    if (Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).Any())
                    {
                        hasLocalData = true;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hasLocalData = true; // 无法判断时不误报
        }

        if (!hasLocalData)
        {
            flags |= PortabilityFlags.NoLocalDataDirectory;
        }

        return flags;
    }

    /// <summary>把 “7-Zip 24.08 (x64)” 之类的目录名整理成展示名。</summary>
    public static string CleanDisplayName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "未命名软件";
        }

        var name = raw.Trim();
        name = BitnessSuffixRegex().Replace(name, string.Empty);
        name = VersionSuffixRegex().Replace(name, string.Empty);
        name = name.Trim(' ', '-', '_', '.', '·');
        return string.IsNullOrWhiteSpace(name) ? raw.Trim() : name;
    }

    [GeneratedRegex(@"[\s\-_]*\((?:x86|x64|32|64)[\s-]?(?:bit)?\)\s*$|[\s\-_]*\[\s*(?:x86|x64|32|64)[\s-]?(?:bit)?\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BitnessSuffixRegex();

    [GeneratedRegex(@"[\s\-_]*(?:v|ver\.?)?\s*\d+(?:\.\d+){1,3}(?:[\s\-_].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffixRegex();
}
