using System.Diagnostics;
using System.IO;

namespace HNovaPortable.Services;

public readonly record struct ExecutableMetadata(
    string? FileDescription,
    string? ProductName,
    string? Company,
    string? FileVersion,
    bool HasVersionInfo)
{
    public static ExecutableMetadata Read(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var description = Trimmed(info.FileDescription);
            var product = Trimmed(info.ProductName);
            var company = Trimmed(info.CompanyName);
            var version = Trimmed(info.FileVersion) ?? Trimmed(info.ProductVersion);
            return new ExecutableMetadata(description, product, company, version,
                !string.IsNullOrWhiteSpace(product) || !string.IsNullOrWhiteSpace(description));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }

        static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
