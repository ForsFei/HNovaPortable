using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HNovaPortable.Models;
using HNovaPortable.Services.Scanning;

namespace HNovaPortable.ViewModels;

/// <summary>导入向导中的一个待导入软件（对应一个目录或一个散装 EXE）。</summary>
public partial class ImportCandidateViewModel : ObservableObject
{
    public ImportCandidateViewModel(FolderAnalysis analysis, Category? defaultCategory)
    {
        Analysis = analysis;
        Name = analysis.SuggestedName;
        Category = defaultCategory;
        Candidates = [.. analysis.Candidates];
        MainExecutable = analysis.Best;
        Mode = AppLocationMode.Managed;
        IsSelected = true;

        // 单独选中一个 EXE 时，如果它旁边还有别的文件，默认连目录一起复制，
        // 否则很多便携软件会缺少依赖而无法启动。
        CopyWholeFolder = !analysis.IsSingleExe || HasSiblingFiles(analysis.SingleExePath);
    }

    private static bool HasSiblingFiles(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(exePath);
            return directory is not null && System.IO.Directory.EnumerateFileSystemEntries(directory).Count() > 1;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public FolderAnalysis Analysis { get; }

    public ObservableCollection<ExeCandidate> Candidates { get; }

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private AppLocationMode mode;

    [ObservableProperty]
    private Category? category;

    [ObservableProperty]
    private ExeCandidate? mainExecutable;

    [ObservableProperty]
    private string arguments = string.Empty;

    /// <summary>是否把整个目录复制进库（仅对“单独 EXE”来源有意义）。</summary>
    [ObservableProperty]
    private bool copyWholeFolder;

    public bool IsSingleExe => Analysis.IsSingleExe;

    public string SourcePath => Analysis.IsSingleExe ? Analysis.SingleExePath ?? Analysis.FolderPath : Analysis.FolderPath;

    public string SourceText => IsSingleExe ? SourcePath : Analysis.FolderPath;

    public string SizeText => Analysis.SizeText;

    public string ExeCountText => Analysis.ExeCount == 0 ? "无可用程序" : $"{Analysis.ExeCount} 个 EXE";

    public bool CanChooseMain => Candidates.Count > 1;

    public bool HasFlags => Analysis.Flags != PortabilityFlags.None;

    public string FlagsText => Analysis.Flags switch
    {
        PortabilityFlags.None => "未发现明显迁移风险",
        _ => string.Join("  ·  ", Describe(Analysis.Flags)),
    };

    public bool IsManaged
    {
        get => Mode == AppLocationMode.Managed;
        set
        {
            if (value)
            {
                Mode = AppLocationMode.Managed;
            }
        }
    }

    public bool IsLinked
    {
        get => Mode == AppLocationMode.Linked;
        set
        {
            if (value)
            {
                Mode = AppLocationMode.Linked;
            }
        }
    }

    partial void OnModeChanged(AppLocationMode value)
    {
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(IsLinked));
        OnPropertyChanged(nameof(ModeBadge));
        OnPropertyChanged(nameof(ModeHint));
    }

    public string ModeBadge => Mode == AppLocationMode.Managed ? "托管" : "引用";

    public string ModeHint => Mode == AppLocationMode.Managed
        ? "复制进库内 Apps 目录，随库一起迁移。"
        : "只记录原路径，不复制文件；换电脑后需要重新定位。";

    partial void OnMainExecutableChanged(ExeCandidate? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(Name))
        {
            Name = value.DisplayName;
        }

        OnPropertyChanged(nameof(MainExecutableText));
    }

    public string MainExecutableText => MainExecutable is null
        ? "未识别到主程序"
        : $"{MainExecutable.FileName}（{MainExecutable.ScoreSummary}）";

    private static IEnumerable<string> Describe(PortabilityFlags flags)
    {
        if (flags.HasFlag(PortabilityFlags.HasUninstaller))
        {
            yield return "含卸载程序";
        }

        if (flags.HasFlag(PortabilityFlags.NoLocalDataDirectory))
        {
            yield return "配置可能在 AppData";
        }

        if (flags.HasFlag(PortabilityFlags.ConsoleSubsystem))
        {
            yield return "控制台程序";
        }
    }
}
