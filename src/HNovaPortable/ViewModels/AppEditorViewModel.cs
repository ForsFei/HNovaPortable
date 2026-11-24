using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;
using HNovaPortable.Services;

namespace HNovaPortable.ViewModels;

/// <summary>软件详情编辑：名称、分类、标签、启动参数与备注。</summary>
public partial class AppEditorViewModel : ObservableObject
{
    private static readonly Category Unclassified = new() { Id = string.Empty, Name = "未分类" };

    private readonly AppEntry _entry;
    private readonly AppRepository _apps;
    private readonly IconService _icons;
    private readonly AppPaths _paths;

    public AppEditorViewModel(
        AppEntry entry,
        IEnumerable<Category> categories,
        AppRepository apps,
        IconService icons,
        AppPaths paths,
        DialogService dialogs)
    {
        _entry = entry;
        _apps = apps;
        _icons = icons;
        _paths = paths;

        Categories = [Unclassified];
        foreach (var category in categories)
        {
            Categories.Add(category);
        }

        Name = entry.Name;
        Category = Categories.FirstOrDefault(c => c.Id == entry.CategoryId) ?? Unclassified;
        TagsText = string.Join(", ", entry.Tags);
        Arguments = entry.Arguments ?? string.Empty;
        WorkDirectory = entry.WorkDirectory ?? string.Empty;
        Notes = entry.Notes ?? string.Empty;
        GithubUrl = entry.GithubUrl ?? string.Empty;
        WebsiteUrl = entry.WebsiteUrl ?? string.Empty;
        IsFavorite = entry.IsFavorite;
        Icon = icons.Load(entry.IconKey, entry.ResolveExecutablePath(paths.Root));

        PathText = entry.Mode == AppLocationMode.Managed ? entry.RelativePath ?? string.Empty : entry.LinkedPath ?? string.Empty;
        ModeText = entry.Mode == AppLocationMode.Managed ? "托管（文件在库内）" : "引用（文件在库外）";
        VersionText = string.IsNullOrWhiteSpace(entry.ExeVersion) ? "未知" : entry.ExeVersion!;
        SizeText = Bytes.Format(entry.SizeBytes);
        ExecutableExists = entry.ResolveExecutablePath(paths.Root) is not null;
    }

    public ObservableCollection<Category> Categories { get; }

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private Category? category;

    [ObservableProperty]
    private string tagsText = string.Empty;

    [ObservableProperty]
    private string arguments = string.Empty;

    [ObservableProperty]
    private string workDirectory = string.Empty;

    [ObservableProperty]
    private string notes = string.Empty;

    [ObservableProperty]
    private bool isFavorite;

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? icon;

    [ObservableProperty]
    private bool isSaving;

    [ObservableProperty]
    private string githubUrl = "";

    [ObservableProperty]
    private string websiteUrl = "";

    [ObservableProperty]
    private string updateStatus = "";

    [ObservableProperty]
    private bool hasUpdate;

    partial void OnUpdateStatusChanged(string value) => HasUpdate = false;

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateStatus = "正在检查更新…";
        var result = await AppUpdateService.CheckAsync(GithubUrl, WebsiteUrl, VersionText);
        UpdateStatus = result.Message;
        HasUpdate = result.HasUpdate;
    }

    [RelayCommand]
    private void OpenWebsite() => OpenPage(WebsiteUrl);

    [RelayCommand]
    private void OpenGithub() => OpenPage(GithubUrl);

    private void OpenPage(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            UpdateStatus = "请填写有效的 HTTPS 地址";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            UpdateStatus = "无法打开浏览器";
        }
    }

    public string PathText { get; }

    public string ModeText { get; }

    public string VersionText { get; }

    public string SizeText { get; }

    public bool ExecutableExists { get; }

    public string HealthText => ExecutableExists ? "程序文件正常" : "程序文件已丢失，可在主界面右键重新定位";

    public string WorkDirectoryHint => _entry.Mode == AppLocationMode.Managed
        ? "留空表示使用程序所在目录；相对路径相对于库根（例如 Apps/Foo）"
        : "可填写绝对路径；留空表示使用程序所在目录";

    [RelayCommand]
    private async Task SaveAsync()
    {
        var trimmedName = Name.Trim();
        if (trimmedName.Length == 0)
        {
            return;
        }

        IsSaving = true;
        try
        {
            _entry.Name = trimmedName;
            _entry.SortName = AppRepository.BuildSortName(trimmedName);
            _entry.CategoryId = string.IsNullOrWhiteSpace(Category?.Id) ? null : Category!.Id;
            _entry.Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments.Trim();
            _entry.WorkDirectory = string.IsNullOrWhiteSpace(WorkDirectory) ? null : WorkDirectory.Trim();
            _entry.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
            _entry.GithubUrl = string.IsNullOrWhiteSpace(GithubUrl) ? null : GithubUrl.Trim();
            _entry.WebsiteUrl = string.IsNullOrWhiteSpace(WebsiteUrl) ? null : WebsiteUrl.Trim();
            _entry.IsFavorite = IsFavorite;

            var tags = TagsText
                .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await _apps.UpsertAsync(_entry, tags).ConfigureAwait(true);
            CloseRequested?.Invoke(this, true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    [RelayCommand]
    private void RefreshIcon()
    {
        var executable = _entry.ResolveExecutablePath(_paths.Root);
        if (executable is null)
        {
            return;
        }

        Icon = _icons.Load(null, executable);
    }
}
