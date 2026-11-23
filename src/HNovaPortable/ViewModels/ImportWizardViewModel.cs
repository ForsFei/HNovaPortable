using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HNovaPortable.Data;
using HNovaPortable.Models;
using HNovaPortable.Services;
using HNovaPortable.Services.Scanning;
using Microsoft.Win32;

namespace HNovaPortable.ViewModels;

/// <summary>
/// 导入向导：选择来源 → 识别主程序 → 确认名称/模式/分类 → 复制并入库。
/// 所有耗时操作异步执行，可取消，失败会清理半成品。
/// </summary>
public partial class ImportWizardViewModel : ObservableObject
{
    private readonly ScanService _scanner;
    private readonly ImportService _importService;
    private readonly CategoryRepository _categories;
    private readonly DialogService _dialogs;

    private CancellationTokenSource? _cancellationTokenSource;

    public ImportWizardViewModel(
        ScanService scanner,
        ImportService importService,
        CategoryRepository categories,
        DialogService dialogs)
    {
        _scanner = scanner;
        _importService = importService;
        _categories = categories;
        _dialogs = dialogs;

        Items.CollectionChanged += OnItemsChanged;
    }

    public ObservableCollection<ImportCandidateViewModel> Items { get; } = [];

    public ObservableCollection<Category> Categories { get; } = [];

    [ObservableProperty]
    private ImportCandidateViewModel? selectedItem;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private bool isCompleted;

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string statusText = "选择「添加 EXE」或「扫描文件夹」开始";

    [ObservableProperty]
    private string progressDetail = string.Empty;

    [ObservableProperty]
    private string resultText = string.Empty;

    [ObservableProperty]
    private int selectedCount;

    [ObservableProperty]
    private string scanSummary = string.Empty;

    public bool DidImport { get; private set; }

    public bool HasItems => Items.Count > 0;

    public bool CanImport => SelectedCount > 0 && !IsBusy && !IsScanning;

    public string SelectedSummary => SelectedCount == 0 ? "未选择" : $"已选 {SelectedCount} 个";

    public async Task InitializeAsync()
    {
        Categories.Clear();
        foreach (var category in await _categories.ListAsync().ConfigureAwait(true))
        {
            Categories.Add(category);
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanImport));

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanImport));

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(SelectedSummary));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ImportCandidateViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ImportCandidateViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(HasItems));
        RecalculateSelection();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportCandidateViewModel.IsSelected))
        {
            RecalculateSelection();
        }
    }

    private void RecalculateSelection()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        OnPropertyChanged(nameof(CanImport));
    }

    // ==================== 来源 ====================

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的程序（可多选）",
            Filter = "程序 (*.exe)|*.exe",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsScanning = true;
        StatusText = "正在分析所选程序…";

        try
        {
            foreach (var file in dialog.FileNames)
            {
                var analysis = await _scanner.AnalyzeExeAsync(file).ConfigureAwait(true);
                AddCandidate(analysis);
            }

            StatusText = $"已识别 {Items.Count} 个程序";
            SelectedItem ??= Items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "分析失败：" + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择软件库根目录（每个子文件夹视为一个软件）",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ScanFolderCoreAsync(dialog.FolderName).ConfigureAwait(true);
    }

    public async Task ScanFolderCoreAsync(string folder)
    {
        IsScanning = true;
        ScanSummary = "正在扫描…";
        StatusText = "正在扫描目录…";

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.Percent;
            ProgressDetail = p.Message;
            StatusText = $"扫描中 {p.Current}/{p.Total}：{p.Message}";
        });

        try
        {
            var analyses = await _scanner.ScanRootAsync(folder, progress).ConfigureAwait(true);
            foreach (var analysis in analyses)
            {
                AddCandidate(analysis);
            }

            ScanSummary = analyses.Count == 0
                ? "该目录下没有找到可执行程序"
                : $"扫描完成：{analyses.Count} 个软件候选";

            StatusText = ScanSummary;
            SelectedItem ??= Items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
        }
        finally
        {
            IsScanning = false;
            Progress = 0;
            ProgressDetail = string.Empty;
        }
    }

    private void AddCandidate(FolderAnalysis analysis)
    {
        if (analysis.IsEmpty)
        {
            return;
        }

        var defaultCategory = Categories.FirstOrDefault(c => c.Name == "其他");
        Items.Add(new ImportCandidateViewModel(analysis, defaultCategory));
    }

    [RelayCommand]
    private void RemoveItem(ImportCandidateViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        Items.Remove(item);
        if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = Items.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task ScanCurrentFolderAsync()
    {
        var item = SelectedItem;
        if (item is null || item.IsSingleExe)
        {
            return;
        }

        IsScanning = true;
        StatusText = "正在重新分析目录…";
        try
        {
            var analysis = await _scanner.AnalyzeFolderAsync(item.Analysis.FolderPath).ConfigureAwait(true);
            var index = Items.IndexOf(item);
            if (index >= 0)
            {
                var replacement = new ImportCandidateViewModel(analysis, item.Category) { Name = item.Name };
                Items[index] = replacement;
                SelectedItem = replacement;
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ==================== 导入 ====================

    [RelayCommand]
    private async Task ImportSelectedAsync()
    {
        var pending = Items.Where(i => i.IsSelected).ToList();
        if (pending.Count == 0)
        {
            StatusText = "请先勾选要导入的软件";
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        IsBusy = true;
        IsCompleted = false;
        HasResult = false;
        DidImport = false;
        Progress = 0;

        var succeeded = new List<string>();
        var failed = new List<string>();

        try
        {
            for (var index = 0; index < pending.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var item = pending[index];
                var slice = 100.0 / pending.Count;
                var basePercent = index * slice;

                StatusText = $"正在导入 {item.Name}（{index + 1}/{pending.Count}）";

                var adapter = new Progress<ImportProgress>(p =>
                {
                    Progress = basePercent + p.Percent * slice / 100.0;
                    ProgressDetail = p.Detail;
                    StatusText = $"{item.Name} · {p.Stage}";
                });

                var request = new ImportRequest
                {
                    SourcePath = item.SourcePath,
                    MainExecutablePath = item.MainExecutable?.FullPath,
                    Name = item.Name,
                    Mode = item.Mode,
                    CategoryId = item.Category?.Id,
                    Arguments = item.Arguments,
                    Flags = item.Analysis.Flags,
                    CopyWholeFolder = item.CopyWholeFolder,
                };

                var result = await _importService.ImportAsync(request, adapter, token).ConfigureAwait(true);
                if (result.Success)
                {
                    succeeded.Add(item.Name);
                }
                else
                {
                    failed.Add($"{item.Name}：{result.Error}");
                }
            }

            Progress = 100;
            DidImport = succeeded.Count > 0;
            IsCompleted = true;
            HasResult = true;
            ProgressDetail = string.Empty;

            var lines = new List<string>
            {
                succeeded.Count == 0 ? "没有软件被导入" : $"成功导入 {succeeded.Count} 个软件",
            };

            if (failed.Count > 0)
            {
                lines.Add($"失败 {failed.Count} 个：");
                lines.AddRange(failed.Select(f => "· " + f));
            }

            lines.Add(string.Empty);
            lines.Add("下一步可以在软件库里为它们设置分类、标签与启动参数。");

            ResultText = string.Join(Environment.NewLine, lines);
            StatusText = succeeded.Count == 0 ? "导入失败" : "导入完成";

            foreach (var item in pending.Where(i => succeeded.Any(s => s == i.Name)).ToList())
            {
                Items.Remove(item);
            }

            if (failed.Count > 0)
            {
                _dialogs.Warn(ResultText, "导入结果");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消导入";
            ResultText = "导入已取消，未完成的复制已被清理。";
            HasResult = true;
        }
        catch (Exception ex)
        {
            StatusText = "导入失败：" + ex.Message;
            ResultText = "导入失败：" + ex.Message;
            HasResult = true;
            _dialogs.Error(ResultText);
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    [RelayCommand]
    private void Reset()
    {
        HasResult = false;
        IsCompleted = false;
        ResultText = string.Empty;
        Progress = 0;
        StatusText = "选择「添加 EXE」或「扫描文件夹」开始";
    }
}
