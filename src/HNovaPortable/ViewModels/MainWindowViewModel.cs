using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HNovaPortable.Models;
using HNovaPortable.Services;

namespace HNovaPortable.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const string AllNavKey = "all";

    private readonly LibraryService _library;
    private readonly ThemeService _theme;
    private readonly SettingsService _settings;

    public MainWindowViewModel(
        LibraryService library,
        ThemeService theme,
        SettingsService settings,
        LibraryViewModel libraryViewModel)
    {
        _library = library;
        _theme = theme;
        _settings = settings;
        Library = libraryViewModel;

        Library.LibraryChanged += async (_, _) => await RefreshCountsAsync().ConfigureAwait(true);
    }

    public LibraryViewModel Library { get; }

    public ObservableCollection<NavSection> NavSections { get; } = [];

    [ObservableProperty]
    private NavItem? selectedNav;

    [ObservableProperty]
    private string pageTitle = "全部软件";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "正在初始化软件库…";

    [ObservableProperty]
    private string libraryRoot = string.Empty;

    [ObservableProperty]
    private string libraryMeta = string.Empty;

    [ObservableProperty]
    private string themeLabel = "跟随系统";

    [ObservableProperty]
    private string themeGlyph = "\uE793";

    [ObservableProperty]
    private string? locationWarning;

    public string VersionText { get; } = "v0.1.0";

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            await _library.InitializeAsync().ConfigureAwait(true);

            LibraryRoot = _library.Paths.Root;
            LocationWarning = _library.Paths.IsFallbackLocation ? _library.Paths.LocationNote : null;
            LibraryMeta = $"schema v{_library.SchemaVersion} · 库标识 {ShortId(_library.LibraryId)}";

            await Library.InitializeAsync().ConfigureAwait(true);
            await BuildNavigationAsync().ConfigureAwait(true);

            ThemeLabel = _theme.ModeLabel;
            ThemeGlyph = _theme.Mode switch
            {
                AppTheme.Dark => "\uE708",
                AppTheme.Light => "\uE706",
                _ => "\uE793",
            };

            StatusText = _library.Stats.AppCount == 0
                ? "库结构已就绪，点击「导入软件」开始"
                : $"已载入 {_library.Stats.AppCount} 个软件";

            _settings.Current.LastLibraryId = _library.LibraryId;
            _settings.Save();
        }
        catch (Exception ex)
        {
            StatusText = "初始化失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BuildNavigationAsync()
    {
        NavSections.Clear();

        var librarySection = new NavSection("软件库");
        librarySection.Items.Add(new NavItem(AllNavKey, "全部软件", "\uE8F1") { Count = _library.Stats.AppCount });
        librarySection.Items.Add(new NavItem("favorites", "收藏", "\uE734") { Count = _library.Stats.FavoriteCount });
        librarySection.Items.Add(new NavItem("unhealthy", "需要修复", "\uE7BA") { Count = _library.Stats.UnhealthyCount });
        NavSections.Add(librarySection);

        var categories = await _library.GetCategoriesAsync().ConfigureAwait(true);
        var categorySection = new NavSection("分类");
        foreach (var category in categories)
        {
            categorySection.Items.Add(new NavItem("cat:" + category.Id, category.Name, "\uE8B7")
            {
                Count = category.AppCount,
                AccentColor = category.Color,
            });
        }

        NavSections.Add(categorySection);

        var target = SelectedNav is null
            ? NavSections[0].Items[0]
            : FindNavItem(SelectedNav.Key) ?? NavSections[0].Items[0];

        SelectNav(target);
    }

    private async Task RefreshCountsAsync()
    {
        await _library.RefreshStatsAsync().ConfigureAwait(true);
        var categories = await _library.GetCategoriesAsync().ConfigureAwait(true);

        foreach (var section in NavSections)
        {
            foreach (var item in section.Items)
            {
                if (item.Key == AllNavKey)
                {
                    item.Count = _library.Stats.AppCount;
                }
                else if (item.Key == "favorites")
                {
                    item.Count = _library.Stats.FavoriteCount;
                }
                else if (item.Key == "unhealthy")
                {
                    item.Count = _library.Stats.UnhealthyCount;
                }
                else if (item.Key.StartsWith("cat:", StringComparison.OrdinalIgnoreCase))
                {
                    var category = categories.FirstOrDefault(c => "cat:" + c.Id == item.Key);
                    item.Count = category?.AppCount ?? 0;
                }
            }
        }
    }

    private NavItem? FindNavItem(string key)
        => NavSections.SelectMany(s => s.Items).FirstOrDefault(i => i.Key == key);

    [RelayCommand]
    private void SelectNav(NavItem? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var section in NavSections)
        {
            foreach (var navItem in section.Items)
            {
                navItem.IsSelected = ReferenceEquals(navItem, item);
            }
        }

        SelectedNav = item;
        PageTitle = item.Title;
        Library.ApplyNav(item.Key, item.Title);
    }

    [RelayCommand]
    private void CycleTheme()
    {
        var next = _theme.Mode switch
        {
            AppTheme.System => AppTheme.Dark,
            AppTheme.Dark => AppTheme.Light,
            _ => AppTheme.System,
        };

        _theme.Apply(next);
        _settings.Current.Theme = next;
        _settings.Save();

        ThemeLabel = _theme.ModeLabel;
        ThemeGlyph = _theme.Mode switch
        {
            AppTheme.Dark => "\uE708",
            AppTheme.Light => "\uE706",
            _ => "\uE793",
        };
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshCountsAsync().ConfigureAwait(true);
        await Library.ReloadAsync().ConfigureAwait(true);
        StatusText = $"已刷新 · {Library.VisibleCount} 个软件";
    }

    [RelayCommand]
    private void OpenLibraryRoot() => OpenFolder(_library.Paths.Root);

    [RelayCommand]
    private void OpenAppsFolder() => OpenFolder(_library.Paths.AppsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_library.Paths.DataDirectory);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = "无法打开目录：" + ex.Message;
        }
    }

    private static string ShortId(string id)
        => string.IsNullOrEmpty(id) ? "—" : id[..Math.Min(8, id.Length)];
}
