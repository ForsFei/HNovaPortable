namespace HNovaPortable.Models;

/// <summary>软件的存储方式。</summary>
public enum AppLocationMode
{
    /// <summary>托管：文件被复制进库内 Apps 目录，随库迁移。</summary>
    Managed = 0,

    /// <summary>引用：只记录外部路径，迁移到新电脑后需要重定位。</summary>
    Linked = 1,
}

/// <summary>软件当前健康状态。</summary>
public enum AppHealth
{
    Ok = 0,
    Missing = 1,
    VersionChanged = 2,
    NeedsRelocate = 3,
}

/// <summary>迁移兼容性提示（位标记）。</summary>
[Flags]
public enum PortabilityFlags
{
    None = 0,

    /// <summary>目录内存在卸载器，疑似安装版。</summary>
    HasUninstaller = 1 << 0,

    /// <summary>没有本地数据/配置目录，可能把配置写到 AppData。</summary>
    NoLocalDataDirectory = 1 << 1,

    /// <summary>引用模式且文件在库根之外。</summary>
    LinkedOutsideLibrary = 1 << 2,

    /// <summary>依赖控制台窗口的辅助程序，不适合直接作为主程序。</summary>
    ConsoleSubsystem = 1 << 3,
}

public enum AppViewMode
{
    Grid = 0,
    List = 1,
}

/// <summary>主题模式（避免与 System.Windows.ThemeMode 冲突，命名 AppTheme）。</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}
