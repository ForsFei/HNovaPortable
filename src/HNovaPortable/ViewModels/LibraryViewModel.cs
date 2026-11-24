using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;
using HNovaPortable.Services;
using HNovaPortable.Services.Scanning;
using HNovaPortable.Views;
using Microsoft.Win32;

namespace HNovaPortable.ViewModels;

/// <summary>软件库主视图：筛选、启动、编辑、导入与维护入口。</summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly AppRepository _apps;
    private readonly CategoryRepository _categories;
    private readonly IconService _icons;
    private readonly LaunchService _launcher;
    private readonly DialogService _dialogs;
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly MaintenanceService _maintenance;
    private readonly ImportService _importService;
    private readonly ScanService _scanner;
    private readonly ExportService _exportService;
    private readonly PackageImportService _packageImporter;

    private string? _categoryId;
    private bool _favoritesOnly;
    private bool _unhealthyOnly;
    private CancellationTokenSource? _debounceCts;
    private bool _suppressReload;

    public LibraryViewModel(
        AppPaths paths,
        AppRepository apps,
        CategoryRepository categories,
        IconService icons,
        LaunchService launcher,
        DialogService dialogs,
        SettingsService settings,
        LibraryService library,
        MaintenanceService maintenance,
        ImportService importService,
        ScanService scanner,
        ExportService exportService,
        PackageImportService packageImporter)
    {
        _paths = paths;
        _apps = apps;
        _categories = categories;
        _icons = icons;
        _launcher = launcher;
        _dialogs = dialogs;
        _settings = settings;
        _library = library;
        _maintenance = maintenance;
        _importService = importService;
        _scanner = scanner;
        _exportService = exportService;
        _packageImporter = packageImporter;
    }

    /// <summary>导入/删除等会改变软件库的操作完成后触发，供外壳刷新导航计数。</summary>
    public event EventHandler? LibraryChanged;

    public ObservableCollection<AppItemViewModel> Apps { get; } = [];

    public ObservableCollection<Category> Categories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsVisible))]
    private AppItemViewModel? selectedApp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsVisible))]
    [NotifyPropertyChangedFor(nameof(DetailsButtonText))]
    private bool showDetails = true;

    public bool IsDetailsVisible => ShowDetails && SelectedApp is not null;

    public string DetailsButtonText => ShowDetails ? "隐藏详情" : "显示详情";

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    [RelayCommand]
    private async Task ManageStartupAsync()
    {
        try
        {
            var entries = await _apps.ListAsync(new AppQuery());
            _dialogs.ShowStartupManager(entries, _paths);
        }
        catch (Exception ex)
        {
            _dialogs.Error("无法读取自启设置：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task CheckAllUpdatesAsync()
    {
        StatusMessage = "正在检查全库更新…";
        try
        {
            var entries = await _apps.ListAsync(new AppQuery());
            var results = new List<(string Name, string? GithubUrl, AppUpdateResult Result)>(entries.Count);
            foreach (var batch in entries.Chunk(4))
            {
                results.AddRange(await Task.WhenAll(batch.Select(async app =>
                    (app.Name, app.GithubUrl, await AppUpdateService.CheckAsync(app.GithubUrl, app.WebsiteUrl, app.ExeVersion)))));
            }

            StatusMessage = $"全库更新检查完成（{entries.Count} 个软件）";
            _dialogs.ShowUpdateResults(results);
        }
        catch (Exception ex)
        {
            StatusMessage = "全库更新检查失败：" + ex.Message;
        }
    }

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool isGridView = true;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private int visibleCount;

    [ObservableProperty]
    private bool hasApps;

    [ObservableProperty]
    private string emptyTitle = "软件库还是空的";

    [ObservableProperty]
    private string emptyDescription = "整理好的免安装软件会集中存放在库内 Apps 目录，分类、标签与启动配置保存在 Data 目录。换电脑时整目录复制即可继续使用。";

    [ObservableProperty]
    private string statusMessage = "就绪";

    [ObservableProperty]
    private string filterSummary = string.Empty;

    [ObservableProperty]
    private AppSort sort = AppSort.Name;

    /// <summary>当前页签标题，用于空状态文案。</summary>
    public string CurrentNavTitle { get; private set; } = "全部软件";

    public async Task InitializeAsync()
    {
        _suppressReload = true;
        try
        {
            Categories.Clear();
            foreach (var category in await _categories.ListAsync().ConfigureAwait(true))
            {
                Categories.Add(category);
            }

            IsGridView = _settings.Current.ViewMode == AppViewMode.Grid;
        }
        finally
        {
            _suppressReload = false;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    public void ApplyNav(string key, string title)
    {
        CurrentNavTitle = title;
        _categoryId = null;
        _favoritesOnly = false;
        _unhealthyOnly = false;

        if (key.StartsWith("cat:", StringComparison.OrdinalIgnoreCase))
        {
            _categoryId = key[4..];
        }
        else if (key.Equals("favorites", StringComparison.OrdinalIgnoreCase))
        {
            _favoritesOnly = true;
        }
        else if (key.Equals("unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            _unhealthyOnly = true;
        }

        _ = ReloadAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressReload)
        {
            return;
        }

        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ReloadAsync());
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async Task ReloadAsync()
    {
        if (_suppressReload)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var query = new AppQuery
            {
                CategoryId = _categoryId,
                FavoritesOnly = _favoritesOnly,
                UnhealthyOnly = _unhealthyOnly,
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                Sort = Sort,
            };

            var entries = await _apps.ListAsync(query).ConfigureAwait(true);
            var categoryLookup = Categories
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

            var selectedId = SelectedApp?.Entry.Id;
            Apps.Clear();

            foreach (var entry in entries)
            {
                var categoryName = entry.CategoryId is not null && categoryLookup.TryGetValue(entry.CategoryId, out var name)
                    ? name
                    : "未分类";

                var icon = _icons.Load(entry.IconKey, entry.ResolveExecutablePath(_paths.Root));
                Apps.Add(new AppItemViewModel(entry, icon, categoryName));
            }

            VisibleCount = Apps.Count;
            HasApps = Apps.Count > 0;
            SelectedApp = selectedId is null ? null : Apps.FirstOrDefault(a => a.Entry.Id == selectedId);

            UpdateEmptyState();
            FilterSummary = BuildFilterSummary();

            var hasFilter = _favoritesOnly || _unhealthyOnly || _categoryId is not null || !string.IsNullOrWhiteSpace(SearchText);
            StatusMessage = Apps.Count > 0
                ? $"显示 {Apps.Count} 个软件"
                : hasFilter ? "没有匹配的软件" : "软件库为空，点击「导入软件」开始";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取软件库失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateEmptyState()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            EmptyTitle = "没有找到匹配的软件";
            EmptyDescription = $"没有名称、标签或备注包含“{SearchText.Trim()}”的软件。可以换个关键词，或清空搜索框。";
            return;
        }

        if (_favoritesOnly)
        {
            EmptyTitle = "还没有收藏的软件";
            EmptyDescription = "在软件卡片上点击星标即可收藏，收藏的软件会集中显示在这里。";
            return;
        }

        if (_unhealthyOnly)
        {
            EmptyTitle = "没有需要修复的软件";
            EmptyDescription = "所有软件的主程序都能正常定位。执行“重新扫描”可以主动检查版本变化。";
            return;
        }

        if (_categoryId is not null)
        {
            EmptyTitle = "该分类下还没有软件";
            EmptyDescription = "导入软件时选择这个分类，或在软件详情里修改分类。";
            return;
        }

        EmptyTitle = "软件库还是空的";
        EmptyDescription = "整理好的免安装软件会集中存放在库内 Apps 目录，分类、标签与启动配置保存在 Data 目录。换电脑时整目录复制即可继续使用。";
    }

    private string BuildFilterSummary()
    {
        var parts = new List<string> { CurrentNavTitle };
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            parts.Add($"搜索“{SearchText.Trim()}”");
        }

        parts.Add($"{VisibleCount} 个");
        return string.Join(" · ", parts);
    }

    // ==================== 启动与文件操作 ====================

    [RelayCommand]
    private async Task LaunchAsync(AppItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var outcome = await _launcher.LaunchAsync(item.Entry).ConfigureAwait(true);
        switch (outcome)
        {
            case LaunchOutcome.Started:
                item.Entry.LaunchCount++;
                item.Entry.LastLaunchUtc = DateTimeOffset.UtcNow;
                item.SyncFromEntry();
                StatusMessage = $"已启动 {item.Name}";
                break;

            case LaunchOutcome.Missing:
                StatusMessage = $"{item.Name} 的程序文件已丢失";
                if (_dialogs.Confirm($"{item.Name} 的程序文件找不到了。\n\n是否现在重新定位它的主程序？", "程序缺失"))
                {
                    Relocate(item);
                }

                break;

            default:
                StatusMessage = $"启动 {item.Name} 失败";
                break;
        }
    }

    [RelayCommand]
    private void OpenFolder(AppItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_launcher.RevealInExplorer(item.Entry))
        {
            StatusMessage = "无法打开所在目录";
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(AppItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsFavorite = !item.IsFavorite;
        item.Entry.IsFavorite = item.IsFavorite;
        await _apps.SetFavoriteAsync(item.Entry.Id, item.IsFavorite).ConfigureAwait(true);
        LibraryChanged?.Invoke(this, EventArgs.Empty);

        if (_favoritesOnly && !item.IsFavorite)
        {
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    // ==================== 编辑 / 删除 / 重定位 ====================

    [RelayCommand]
    private void Edit(AppItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var viewModel = new AppEditorViewModel(item.Entry, Categories, _apps, _icons, _paths, _dialogs);
        var window = new EditAppWindow(viewModel);
        if (_dialogs.ShowDialog(window) == true)
        {
            _ = ReloadAfterChangeAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(AppItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var managedHint = item.IsManaged
            ? "\n\n只从库中移除记录，库内 Apps 目录下的文件会保留，可自行清理。"
            : "\n\n只删除库内记录，不删除外部文件。";

        if (!_dialogs.Confirm($"确定要从软件库移除 {item.Name} 吗？{managedHint}", "移除软件", danger: true))
        {
            return;
        }

        try
        {
            StartupService.SetEnabled(item.Entry, _paths, false);
            await _apps.DeleteAsync(item.Entry.Id).ConfigureAwait(true);

            var iconPath = Path.Combine(_paths.IconsDirectory, item.Entry.Id + ".png");
            if (File.Exists(iconPath))
            {
                File.Delete(iconPath);
            }

            StatusMessage = $"已移除 {item.Name}";
            await ReloadAfterChangeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "移除失败：" + ex.Message;
            _dialogs.Error("移除失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void Relocate(AppItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"选择 {item.Name} 的主程序",
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _ = RelocateCoreAsync(item, dialog.FileName);
    }

    private async Task RelocateCoreAsync(AppItemViewModel item, string executablePath)
    {
        var (success, message) = await _maintenance.RelocateAsync(item.Entry, executablePath).ConfigureAwait(true);
        StatusMessage = message;

        if (success)
        {
            await ReloadAfterChangeAsync().ConfigureAwait(true);
        }
        else
        {
            _dialogs.Warn(message);
        }
    }

    // ==================== 导入 / 导出 / 维护 ====================

    [RelayCommand]
    private void Import()
    {
        var viewModel = new ImportWizardViewModel(_scanner, _importService, _categories, _dialogs);
        var window = new ImportWindow(viewModel);
        _dialogs.ShowDialog(window);

        if (viewModel.DidImport)
        {
            _ = ReloadAfterChangeAsync();
        }
    }

    [RelayCommand]
    private void Export()
    {
        var viewModel = new ExportViewModel(_exportService, _apps, _dialogs, SelectedApp?.Entry.Id);
        var window = new ExportWindow(viewModel);
        _dialogs.ShowDialog(window);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsBusy = true;
        StatusMessage = "正在重新扫描软件库…";
        try
        {
            var summary = await _maintenance.RescanAsync().ConfigureAwait(true);
            await ReloadAfterChangeAsync().ConfigureAwait(true);
            StatusMessage = $"扫描完成：{summary.Checked} 个软件，缺失 {summary.Missing}，版本变化 {summary.VersionChanged}";
            _dialogs.Info(summary.ToReport(), "扫描结果");
        }
        catch (Exception ex)
        {
            StatusMessage = "扫描失败：" + ex.Message;
            _dialogs.Error("扫描失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        IsBusy = true;
        StatusMessage = "正在备份软件库…";
        try
        {
            var target = await _maintenance.BackupAsync(includeApps: false).ConfigureAwait(true);
            StatusMessage = "备份完成";
            _dialogs.Info(
                $"已备份到：\n{target}\n\n备份包含 library.db、设置与图标。Apps 目录中的软件文件保持原地未动，" +
                "如需完整备份（含软件文件），可直接复制整个 HNova Portable 文件夹。",
                "备份完成");
        }
        catch (Exception ex)
        {
            StatusMessage = "备份失败：" + ex.Message;
            _dialogs.Error("备份失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ImportPackage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择库包（文件夹内 library.db 或 zip/7z 压缩包）",
            Filter = "库包 (*.zip;*.7z)|*.zip;*.7z|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _ = RunPackageImportAsync(dialog.FileName);
    }

    private async Task RunPackageImportAsync(string packagePath)
    {
        IsBusy = true;
        StatusMessage = "正在导入库包…";
        try
        {
            var result = await _packageImporter.ImportAsync(packagePath).ConfigureAwait(true);
            if (result.Success)
            {
                await ReloadAfterChangeAsync().ConfigureAwait(true);
            }

            StatusMessage = result.Success ? $"已导入 {result.Imported} 个软件" : "库包导入失败";
            _dialogs.Info(result.Report(), result.Success ? "导入完成" : "导入失败");
        }
        catch (Exception ex)
        {
            StatusMessage = "导入失败：" + ex.Message;
            _dialogs.Error("导入失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择备份目录（形如 Data/backups/backup-20240101-120000）",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_dialogs.Confirm("恢复会用备份覆盖当前 library.db 与设置。\n当前的库会先自动保存一份快照。\n\n继续吗？", "恢复备份", danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (success, message) = await _maintenance.RestoreAsync(dialog.FolderName).ConfigureAwait(true);
            _dialogs.Info(message, success ? "恢复完成" : "恢复失败");
            StatusMessage = message;
            await ReloadAfterChangeAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAfterChangeAsync()
    {
        Categories.Clear();
        foreach (var category in await _categories.ListAsync().ConfigureAwait(true))
        {
            Categories.Add(category);
        }

        await ReloadAsync().ConfigureAwait(true);
        await _library.RefreshStatsAsync().ConfigureAwait(true);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ==================== 视图与排序 ====================

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        IsGridView = mode.Equals("grid", StringComparison.OrdinalIgnoreCase);
        _settings.Current.ViewMode = IsGridView ? AppViewMode.Grid : AppViewMode.List;
        _settings.Save();
    }

    [RelayCommand]
    private async Task ChangeSortAsync(string sortKey)
    {
        if (Enum.TryParse<AppSort>(sortKey, ignoreCase: true, out var sort))
        {
            Sort = sort;
            await ReloadAsync().ConfigureAwait(true);
        }
    }
}
