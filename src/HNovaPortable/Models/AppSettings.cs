namespace HNovaPortable.Models;

/// <summary>随库保存的用户设置（Data/settings.json）。</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public AppViewMode ViewMode { get; set; } = AppViewMode.Grid;

    public string SortBy { get; set; } = "Name";

    public bool SortDescending { get; set; }

    /// <summary>上次使用的库标识，用于识别“换了库根 / 换了库”的情况。</summary>
    public string? LastLibraryId { get; set; }
}

public sealed record LibraryStats(int AppCount, int FavoriteCount, int UnhealthyCount, int CategoryCount);
