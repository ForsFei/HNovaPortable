using System.IO;
using HNovaPortable.Models;

namespace HNovaPortable.Services.Scanning;

/// <summary>目录扫描得到的一个候选主程序。</summary>
public sealed class ExeCandidate
{
    public required string FullPath { get; init; }

    /// <summary>相对于所在软件目录的路径，使用 '/' 分隔。</summary>
    public required string RelativePath { get; init; }

    public string FileName => Path.GetFileName(FullPath);

    public int Score { get; init; }

    public long SizeBytes { get; init; }

    public string? FileDescription { get; init; }

    public string? ProductName { get; init; }

    public string? Company { get; init; }

    public string? FileVersion { get; init; }

    public bool IsConsole { get; init; }

    /// <summary>得分原因，展示在导入向导里便于用户判断。</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    public string DisplayName
        => !string.IsNullOrWhiteSpace(ProductName) ? ProductName!
            : !string.IsNullOrWhiteSpace(FileDescription) ? FileDescription!
            : Path.GetFileNameWithoutExtension(FileName);

    /// <summary>用于下拉框展示，带上相对路径以区分同名 EXE。</summary>
    public string DisplayWithPath => RelativePath.Equals(FileName, StringComparison.OrdinalIgnoreCase)
        ? FileName
        : $"{FileName}  ·  {RelativePath}";

    public string ScoreSummary => Reasons.Count == 0 ? "—" : string.Join(" · ", Reasons);
}

/// <summary>对一个软件目录的分析结果。</summary>
public sealed class FolderAnalysis
{
    public required string FolderPath { get; init; }

    /// <summary>建议的软件名称（优先使用目录名）。</summary>
    public required string SuggestedName { get; init; }

    /// <summary>按得分从高到低排列的主程序候选。</summary>
    public IReadOnlyList<ExeCandidate> Candidates { get; init; } = [];

    public ExeCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;

    public long TotalSizeBytes { get; init; }

    public int ExeCount { get; init; }

    public PortabilityFlags Flags { get; init; }

    /// <summary>目录中是否根本没有可执行文件。</summary>
    public bool IsEmpty => ExeCount == 0;

    /// <summary>该条目是否只是“散装单个 EXE”（批量扫描时根目录下的独立程序）。</summary>
    public bool IsSingleExe { get; init; }

    /// <summary>散装 EXE 的绝对路径。</summary>
    public string? SingleExePath { get; init; }

    public string SizeText => FormatSize(TotalSizeBytes);

    public static string FormatSize(long bytes) => Core.Bytes.Format(bytes);
}

public readonly record struct ScanProgress(int Current, int Total, string Message)
{
    public double Percent => Total <= 0 ? 0 : Math.Min(100, Current * 100.0 / Total);
}
