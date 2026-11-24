using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HNovaPortable.Services;

internal sealed record AppUpdateResult(string Message, bool HasUpdate = false);

internal static class AppUpdateService
{
    private static readonly HttpClient UpdateClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { UserAgent = { new("HNovaPortable", "1.0") } },
    };

    public static async Task<AppUpdateResult> CheckAsync(string? githubUrl, string? websiteUrl, string? installedVersion)
    {
        try
        {
            string? release = null;
            var repo = Regex.Match(githubUrl?.Trim() ?? "", @"^https://github\.com/([\w.-]+/[\w.-]+)/?$");
            if (repo.Success)
            {
                using var json = JsonDocument.Parse(await UpdateClient.GetStringAsync(
                    "https://api.github.com/repos/" + repo.Groups[1].Value + "/releases/latest"));
                release = json.RootElement.GetProperty("tag_name").GetString();
            }
            else if (Uri.TryCreate(websiteUrl, UriKind.Absolute, out var website))
            {
                var (page, pattern) = website.Host switch
                {
                    "voidtools.com" or "www.voidtools.com" => ("https://www.voidtools.com/downloads/", @"Everything-(\d+(?:\.\d+){3})\.x64\.zip"),
                    "diskanalyzer.com" or "www.diskanalyzer.com" => ("https://diskanalyzer.com/download", @"DOWNLOAD\s+PORTABLE\s+V\s*([\d.]+)"),
                    _ => ("", ""),
                };
                if (page.Length != 0)
                {
                    var html = await UpdateClient.GetStringAsync(page);
                    var content = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " "));
                    release = Regex.Match(content, pattern, RegexOptions.IgnoreCase).Groups[1].Value;
                }
            }

            if (!repo.Success && string.IsNullOrEmpty(release))
            {
                return new("未配置支持自动检查的发布源");
            }

            var current = ParseVersion(installedVersion);
            var latest = ParseVersion(release);
            return latest is null ? new("无法识别正式版本，请查看发布页")
                : current is null ? new($"最新正式版本 {release}，本地版本无法比较")
                : latest > current ? new($"有新版本 {release}（本地 {installedVersion}）", HasUpdate: true)
                : new($"本地 {installedVersion}，最新正式版本 {release}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return new("无法获取正式版本，请稍后重试或查看发布页");
        }
    }

    private static Version? ParseVersion(string? text)
    {
        var number = Regex.Match(text ?? "", @"^\s*[vV]?(\d+(?:\.\d+){1,3})(?:\s|[-+]|$)").Groups[1].Value;
        return Version.TryParse(number, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision))
            : null;
    }
}
