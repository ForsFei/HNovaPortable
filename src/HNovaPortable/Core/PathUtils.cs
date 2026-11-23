using System.IO;

namespace HNovaPortable.Core;

/// <summary>
/// 库内路径统一使用 '/' 分隔的相对路径（例如 Apps/Foo/Foo.exe），
/// 与盘符、计算机名、库根位置完全解耦。
/// </summary>
public static class PathUtils
{
    public static string ToPortable(string relativeOrAbsolute)
        => relativeOrAbsolute.Replace('\\', '/').TrimStart('/');

    public static string ToNative(string portableRelative)
        => portableRelative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>把库内的绝对路径转换成可存储的相对路径；不在库内时返回 null。</summary>
    public static string? ToPortableIfInside(string root, string fullPath)
    {
        var normalizedRoot = AppPaths.NormalizeDirectory(root);
        var normalizedPath = Path.GetFullPath(fullPath);

        if (!IsInside(normalizedRoot, normalizedPath))
        {
            return null;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        return ToPortable(relative);
    }

    public static string Resolve(string root, string portableRelative)
        => Path.GetFullPath(Path.Combine(root, ToNative(portableRelative)));

    public static bool IsInside(string root, string fullPath)
    {
        var normalizedRoot = AppPaths.NormalizeDirectory(root);
        var normalizedPath = Path.GetFullPath(fullPath);

        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetParentPortable(string portableRelative)
    {
        var portable = ToPortable(portableRelative);
        var index = portable.LastIndexOf('/');
        return index <= 0 ? string.Empty : portable[..index];
    }

    public static string GetLeafName(string portableRelative)
    {
        var portable = ToPortable(portableRelative);
        var index = portable.LastIndexOf('/');
        return index < 0 ? portable : portable[(index + 1)..];
    }

    /// <summary>把任意软件名转换成安全的文件夹名。</summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "App" : result;
    }

    /// <summary>在目标目录下生成不冲突的子目录名（Foo、Foo (2)、Foo (3)…）。</summary>
    public static string GetUniqueDirectoryPath(string parentDirectory, string folderName)
    {
        var safeName = SanitizeFolderName(folderName);
        var candidate = Path.Combine(parentDirectory, safeName);
        var index = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(parentDirectory, $"{safeName} ({index++})");
        }

        return candidate;
    }

    public static long GetDirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // 跳过无法读取的文件
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return total;
        }

        return total;
    }
}
