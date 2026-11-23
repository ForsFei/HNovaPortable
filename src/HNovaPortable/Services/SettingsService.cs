using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HNovaPortable.Core;
using HNovaPortable.Models;

namespace HNovaPortable.Services;

/// <summary>
/// 设置与软件库同目录保存（Data/settings.json），保证整库复制后行为一致。
/// 写入采用“临时文件 + 替换”，避免断电导致配置损坏。
/// </summary>
public sealed class SettingsService(AppPaths paths)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths = paths;

    public AppSettings Current { get; private set; } = new();

    public string FilePath => _paths.SettingsFile;

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置损坏不应阻塞启动，回退到默认值并保留坏文件。
            TryBackupCorruptedFile();
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, SerializerOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置保存失败不影响主流程。
        }
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, Path.Combine(_paths.DataDirectory, "settings.corrupted.json"), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
