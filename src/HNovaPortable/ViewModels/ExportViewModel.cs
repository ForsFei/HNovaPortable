using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HNovaPortable.Data;
using HNovaPortable.Services;
using Microsoft.Win32;

namespace HNovaPortable.ViewModels;

public partial class ExportAppOption(string id, string name) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool isSelected = true;
}

/// <summary>导出迁移向导：范围 → 选项 → 目标 → 进度 → 报告。</summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly ExportService _exportService;
    private readonly AppRepository _apps;
    private readonly DialogService _dialogs;

    private CancellationTokenSource? _cts;

    public ExportViewModel(
        ExportService exportService,
        AppRepository apps,
        DialogService dialogs,
        string? preselectedAppId = null)
    {
        _exportService = exportService;
        _apps = apps;
        _dialogs = dialogs;

        PreselectedAppId = preselectedAppId;
    }

    public ObservableCollection<ExportAppOption> Apps { get; } = [];

    public string? PreselectedAppId { get; }

    [ObservableProperty]
    private bool exportAll = true;

    [ObservableProperty]
    private int selectedCount;

    [ObservableProperty]
    private int totalCount;

    [ObservableProperty]
    private int formatIndex;

    [ObservableProperty]
    private bool includeProgramFiles = true;

    [ObservableProperty]
    private bool convertLinkedToManaged = true;

    [ObservableProperty]
    private bool includeLinkedRecords;

    [ObservableProperty]
    private string targetPath = string.Empty;

    [ObservableProperty]
    private string targetHint = "可直接复制到 U 盘的文件夹，推荐用于整库迁移";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private bool isExporting;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string statusText = "选择导出范围与目标位置";

    [ObservableProperty]
    private string progressDetail = string.Empty;

    [ObservableProperty]
    private string resultText = string.Empty;

    [ObservableProperty]
    private bool canOpenTarget;

    public bool HasSevenZip { get; } = File.Exists(SevenZipPath) || FindInPath("7z.exe") is not null;

    private static string SevenZipPath => Path.Combine(Core.AppPaths.ProgramDirectory, "7z.exe");

    public async Task InitializeAsync()
    {
        var all = await _apps.ListAsync(new Models.AppQuery()).ConfigureAwait(true);
        TotalCount = all.Count;

        Apps.Clear();
        foreach (var app in all)
        {
            Apps.Add(new ExportAppOption(app.Id, app.Name));
        }

        foreach (var option in Apps)
        {
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportAppOption.IsSelected))
                {
                    RecalculateSelection();
                }
            };
        }

        RecalculateSelection();
        SuggestTargetPath();
    }

    private void RecalculateSelection()
    {
        SelectedCount = Apps.Count(a => a.IsSelected);
    }

    partial void OnExportAllChanged(bool value)
    {
        if (value)
        {
            foreach (var app in Apps)
            {
                app.IsSelected = true;
            }
        }
    }

    partial void OnFormatIndexChanged(int value)
    {
        TargetHint = value switch
        {
            0 => "可直接复制到 U 盘的文件夹，推荐用于整库迁移",
            1 => "ZIP 压缩包，解压后即可使用",
            _ => HasSevenZip ? "7z 压缩包（体积更小）" : "未检测到 7z.exe，无法导出 7z",
        };

        SuggestTargetPath();
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (FormatIndex == 0)
        {
            var dialog = new OpenFolderDialog { Title = "选择导出位置（将新建一个库文件夹）" };
            if (dialog.ShowDialog() == true)
            {
                TargetPath = Path.Combine(dialog.FolderName, DefaultFolderName());
            }

            return;
        }

        var save = new SaveFileDialog
        {
            Title = "选择导出文件",
            FileName = DefaultFileName(),
            Filter = FormatIndex == 1 ? "ZIP 压缩包 (*.zip)|*.zip" : "7z 压缩包 (*.7z)|*.7z",
            OverwritePrompt = true,
        };

        if (save.ShowDialog() == true)
        {
            TargetPath = save.FileName;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            StatusText = "请先选择导出位置";
            return;
        }

        var ids = ExportAll ? [] : Apps.Where(a => a.IsSelected).Select(a => a.Id).ToList();
        if (!ExportAll && ids.Count == 0)
        {
            StatusText = "请至少勾选一个软件";
            return;
        }

        if (FormatIndex == 2 && !HasSevenZip)
        {
            StatusText = "未找到 7z.exe，请改用 ZIP 或选择文件夹格式";
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsExporting = true;
        HasResult = false;
        CanOpenTarget = false;
        Progress = 0;
        StatusText = "正在导出…";

        try
        {
            var options = new ExportOptions
            {
                TargetPath = TargetPath,
                Format = (ExportFormat)FormatIndex,
                IncludeProgramFiles = IncludeProgramFiles,
                ConvertLinkedToManaged = ConvertLinkedToManaged,
                IncludeLinkedRecords = IncludeLinkedRecords,
                AppIds = ids,
            };

            var progress = new Progress<ExportProgress>(p =>
            {
                Progress = p.Percent;
                StatusText = p.Stage;
                ProgressDetail = p.Detail;
            });

            var result = await _exportService.ExportAsync(options, progress, _cts.Token).ConfigureAwait(true);

            ResultText = result.Report();
            HasResult = true;
            CanOpenTarget = result.Success;
            Progress = result.Success ? 100 : 0;
            StatusText = result.Success ? "导出完成" : "导出失败";
        }
        catch (Exception ex)
        {
            ResultText = "导出失败：" + ex.Message;
            HasResult = true;
            StatusText = "导出失败";
        }
        finally
        {
            IsBusy = false;
            IsExporting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenTarget()
    {
        if (!CanOpenTarget || string.IsNullOrWhiteSpace(TargetPath))
        {
            return;
        }

        try
        {
            var isFolder = Directory.Exists(TargetPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isFolder ? $"\"{TargetPath}\"" : $"/select,\"{TargetPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _dialogs.Warn("无法打开目标位置：" + ex.Message);
        }
    }

    private void SuggestTargetPath()
    {
        var root = Path.GetPathRoot(Core.AppPaths.ProgramDirectory) ?? "C:\\";
        TargetPath = FormatIndex switch
        {
            0 => Path.Combine(Core.AppPaths.ProgramDirectory, "..", DefaultFolderName()),
            1 => Path.Combine(root, DefaultFileName()),
            _ => Path.Combine(root, DefaultFileName().Replace(".zip", ".7z")),
        };

        TargetPath = Path.GetFullPath(TargetPath);
    }

    private static string DefaultFolderName() => "HNovaPortable-export-" + DateTime.Now.ToString("yyyyMMdd");

    private static string DefaultFileName() => "HNovaPortable-export-" + DateTime.Now.ToString("yyyyMMdd") + ".zip";

    private static string? FindInPath(string fileName)
    {
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var candidate = Path.Combine(path.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
