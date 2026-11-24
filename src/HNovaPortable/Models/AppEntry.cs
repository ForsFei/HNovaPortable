using System.IO;

namespace HNovaPortable.Models;

/// <summary>一条软件记录。路径语义见 <see cref="AppLocationMode"/>。</summary>
public sealed class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>排序用名称（去符号、统一大小写）。</summary>
    public string SortName { get; set; } = string.Empty;

    public AppLocationMode Mode { get; set; } = AppLocationMode.Managed;

    /// <summary>托管模式下的库内相对路径，例如 Apps/Foo/Foo.exe。</summary>
    public string? RelativePath { get; set; }

    /// <summary>引用模式下的绝对路径。</summary>
    public string? LinkedPath { get; set; }

    public string? WorkDirectory { get; set; }

    public string? Arguments { get; set; }

    public string? CategoryId { get; set; }

    public bool IsFavorite { get; set; }

    public string? GithubUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? Notes { get; set; }

    /// <summary>图标缓存文件名（Data/icons 下），为空时运行时从 EXE 提取。</summary>
    public string? IconKey { get; set; }

    public string? ExeVersion { get; set; }

    public string? ProductName { get; set; }

    public string? Company { get; set; }

    public string? Description { get; set; }

    public long SizeBytes { get; set; }

    public AppHealth Health { get; set; } = AppHealth.Ok;

    public PortabilityFlags PortabilityFlags { get; set; } = PortabilityFlags.None;

    public int LaunchCount { get; set; }

    public DateTimeOffset? LastLaunchUtc { get; set; }

    public DateTimeOffset? LastScanUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<string> Tags { get; set; } = [];

    /// <summary>解析出可执行的绝对路径；无法解析（引用失效）时返回 null。</summary>
    public string? ResolveExecutablePath(string libraryRoot)
    {
        if (Mode == AppLocationMode.Managed)
        {
            if (string.IsNullOrWhiteSpace(RelativePath))
            {
                return null;
            }

            var full = Core.PathUtils.Resolve(libraryRoot, RelativePath);
            return File.Exists(full) ? full : null;
        }

        return !string.IsNullOrWhiteSpace(LinkedPath) && File.Exists(LinkedPath) ? LinkedPath : null;
    }
}
