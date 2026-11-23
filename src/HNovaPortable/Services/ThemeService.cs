using System.Windows;
using HNovaPortable.Models;
using Microsoft.Win32;

namespace HNovaPortable.Services;

/// <summary>
/// 主题切换：替换 Colors.*.xaml 资源字典（控件样式全部使用 DynamicResource，因此可实时生效）。
/// </summary>
public sealed class ThemeService
{
    private const string LightColorsUri = "pack://application:,,,/HNovaPortable;component/Themes/Colors.Light.xaml";
    private const string DarkColorsUri = "pack://application:,,,/HNovaPortable;component/Themes/Colors.Dark.xaml";
    private const string ColorsMarker = "Themes/Colors.";

    public AppTheme Mode { get; private set; } = AppTheme.System;

    public bool IsDark { get; private set; }

    public string ModeLabel => Mode switch
    {
        AppTheme.Light => "浅色",
        AppTheme.Dark => "深色",
        _ => "跟随系统",
    };

    public event EventHandler? ThemeChanged;

    public void Apply(AppTheme mode)
    {
        Mode = mode;
        IsDark = mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !IsSystemUsingLightTheme(),
        };

        SwapColorDictionary(IsDark ? DarkColorsUri : LightColorsUri);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>跟随系统时，系统主题变化可调用此方法刷新。</summary>
    public void RefreshIfFollowingSystem()
    {
        if (Mode == AppTheme.System)
        {
            Apply(AppTheme.System);
        }
    }

    private static void SwapColorDictionary(string uri)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null && source.Contains(ColorsMarker, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        var replacement = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };
        if (index >= 0)
        {
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }
    }

    private static bool IsSystemUsingLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}
