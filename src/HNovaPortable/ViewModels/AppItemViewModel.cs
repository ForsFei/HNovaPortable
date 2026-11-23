using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using HNovaPortable.Core;
using HNovaPortable.Models;

namespace HNovaPortable.ViewModels;

/// <summary>列表/卡片中的一条软件。图标与分类名在构建时注入，避免绑定期查库。</summary>
public sealed partial class AppItemViewModel(AppEntry entry, BitmapSource? icon, string? categoryName) : ObservableObject
{
    public AppEntry Entry { get; } = entry;

    [ObservableProperty]
    private BitmapSource? icon = icon;

    [ObservableProperty]
    private bool isFavorite = entry.IsFavorite;

    public string Name => Entry.Name;

    public string Initial => string.IsNullOrEmpty(Entry.Name) ? "?" : Entry.Name[..1].ToUpperInvariant();

    public bool HasIcon => Icon is not null;

    public bool IsManaged => Entry.Mode == AppLocationMode.Managed;

    public string ModeText => IsManaged ? "托管" : "引用";

    public string CategoryText => categoryName ?? "未分类";

    public string SizeText => Bytes.Format(Entry.SizeBytes);

    public string VersionText => string.IsNullOrWhiteSpace(Entry.ExeVersion) ? "版本未知" : "v" + Entry.ExeVersion;

    public string TagsText => Entry.Tags.Count == 0 ? string.Empty : string.Join("  ·  ", Entry.Tags);

    public bool HasTags => Entry.Tags.Count > 0;

    public string PathText => IsManaged ? Entry.RelativePath ?? string.Empty : Entry.LinkedPath ?? string.Empty;

    public bool HasProblem => Entry.Health != AppHealth.Ok;

    public string HealthText => Entry.Health switch
    {
        AppHealth.Missing => "程序文件缺失",
        AppHealth.VersionChanged => "版本已变化",
        AppHealth.NeedsRelocate => "需要重新定位",
        _ => "正常",
    };

    public string? Company => Entry.Company;

    public string? Description => Entry.Description;

    public string LaunchCountText => Entry.LaunchCount == 0 ? "从未启动" : $"已启动 {Entry.LaunchCount} 次";

    public string LastLaunchText => Entry.LastLaunchUtc is null
        ? "—"
        : Entry.LastLaunchUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>兼容性提示文本，空字符串表示无明显风险。</summary>
    public string PortabilityHint
    {
        get
        {
            var hints = new List<string>();
            if (Entry.PortabilityFlags.HasFlag(PortabilityFlags.HasUninstaller))
            {
                hints.Add("目录内存在卸载程序，可能是安装版的拷贝，配置可能写在注册表。");
            }

            if (Entry.PortabilityFlags.HasFlag(PortabilityFlags.NoLocalDataDirectory))
            {
                hints.Add("未发现本地配置目录，软件可能把设置保存到 AppData。");
            }

            if (Entry.PortabilityFlags.HasFlag(PortabilityFlags.LinkedOutsideLibrary))
            {
                hints.Add("引用模式的软件不在库内，换电脑后需要重新定位。");
            }

            return hints.Count == 0 ? string.Empty : string.Join("\n", hints);
        }
    }

    public bool HasPortabilityHint => PortabilityHint.Length > 0;

    public void SyncFromEntry()
    {
        IsFavorite = Entry.IsFavorite;
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(LaunchCountText));
        OnPropertyChanged(nameof(LastLaunchText));
        OnPropertyChanged(nameof(PortabilityHint));
        OnPropertyChanged(nameof(HasPortabilityHint));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(IsManaged));
    }
}
