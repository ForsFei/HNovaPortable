using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HNovaPortable.ViewModels;

public partial class NavItem(string key, string title, string glyph) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons 字形。</summary>
    public string Glyph { get; } = glyph;

    [ObservableProperty]
    private int count;

    [ObservableProperty]
    private bool isSelected;

    /// <summary>分类色点（可选）。</summary>
    [ObservableProperty]
    private string? accentColor;
}

public sealed class NavSection(string title)
{
    public string Title { get; } = title;

    public ObservableCollection<NavItem> Items { get; } = [];
}
