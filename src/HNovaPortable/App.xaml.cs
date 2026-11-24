using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Services;
using HNovaPortable.Services.Scanning;
using HNovaPortable.ViewModels;
using HNovaPortable.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HNovaPortable;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            Services = BuildServices(e.Args);

            // 诊断入口：HNovaPortable.exe --selftest-scan "<目录>"，把扫描结果写到 Data/cache/selftest.json
            if (e.Args.Length >= 2 && e.Args[0].Equals("--selftest-scan", StringComparison.OrdinalIgnoreCase))
            {
                RunScanSelfTest(e.Args[1]);
                Shutdown(0);
                return;
            }

            // 诊断入口：--selftest-ui，逐个加载窗口并记录绑定错误，用于无界面环境下的验证
            if (e.Args.Length >= 1 && e.Args[0].Equals("--selftest-ui", StringComparison.OrdinalIgnoreCase))
            {
                RunUiSelfTest();
                Shutdown(0);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0].Equals("--startup-app", StringComparison.OrdinalIgnoreCase))
            {
                var apps = Services.GetRequiredService<AppRepository>();
                var entry = apps.GetAsync(e.Args[1]).GetAwaiter().GetResult();
                if (entry is not null && StartupService.IsEnabled(entry))
                {
                    Services.GetRequiredService<LaunchService>().LaunchAsync(entry).GetAwaiter().GetResult();
                }

                Shutdown(0);
                return;
            }

            var settings = Services.GetRequiredService<SettingsService>();
            settings.Load();

            Services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);

            var window = Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"HNova Portable 启动失败：\n\n{ex.Message}",
                "HNova Portable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static IServiceProvider BuildServices(string[] args)
    {
        var services = new ServiceCollection();

        var paths = AppPaths.Create(args);
        services.AddSingleton(paths);
        services.AddSingleton<Database>();

        services.AddSingleton<MetaRepository>();
        services.AddSingleton<AppRepository>();
        services.AddSingleton<CategoryRepository>();

        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<LibraryService>();

        services.AddSingleton<ScanService>();
        services.AddSingleton<IconService>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<LaunchService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<MaintenanceService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<PackageImportService>();

        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>扫描自检：把识别结果落盘，便于在真实目录上验证“主程序识别”是否准确。</summary>
    private static void RunScanSelfTest(string target)
    {
        var paths = Services.GetRequiredService<AppPaths>();
        var scanner = Services.GetRequiredService<ScanService>();
        var outputPath = System.IO.Path.Combine(paths.CacheDirectory, "selftest.json");

        try
        {
            var analyses = new List<FolderAnalysis>();
            if (System.IO.Directory.Exists(target))
            {
                var isSingleApp = System.IO.Directory
                    .EnumerateFiles(target, "*.exe", System.IO.SearchOption.TopDirectoryOnly)
                    .Any();

                if (isSingleApp)
                {
                    analyses.Add(scanner.AnalyzeFolderAsync(target).GetAwaiter().GetResult());
                }
                else
                {
                    analyses.AddRange(scanner.ScanRootAsync(target).GetAwaiter().GetResult());
                }
            }

            var payload = analyses.Select(a => new
            {
                a.SuggestedName,
                a.FolderPath,
                a.ExeCount,
                Size = a.SizeText,
                Flags = a.Flags.ToString(),
                a.IsSingleExe,
                Best = a.Best?.FileName,
                BestScore = a.Best?.Score,
                Candidates = a.Candidates.Take(3).Select(c => new
                {
                    c.FileName,
                    c.Score,
                    c.FileVersion,
                    c.ProductName,
                    Console = c.IsConsole,
                    c.ScoreSummary,
                }),
            });

            System.IO.File.WriteAllText(outputPath, System.Text.Json.JsonSerializer.Serialize(
                payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outputPath, "扫描自检失败：" + ex);
        }
    }

    /// <summary>
    /// UI 自检：依次创建主窗口、导入窗口、导出窗口与编辑窗口，
    /// 收集 XAML 加载异常和绑定错误，结果写入 Data/cache/uitest.log。
    /// </summary>
    private static void RunUiSelfTest()
    {
        var paths = Services.GetRequiredService<AppPaths>();
        var log = new List<string>();
        var listener = new UiTraceListener(log);

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            Pump(2200);
            log.Add($"[main] loaded={mainWindow.IsLoaded} size={mainWindow.ActualWidth}x{mainWindow.ActualHeight} " +
                    $"apps={mainViewModel.Library.Apps.Count} nav={mainViewModel.NavSections.Count} status={mainViewModel.Library.StatusMessage}");

            var importViewModel = new ImportWizardViewModel(
                Services.GetRequiredService<ScanService>(),
                Services.GetRequiredService<ImportService>(),
                Services.GetRequiredService<CategoryRepository>(),
                Services.GetRequiredService<DialogService>());

            var importWindow = new ImportWindow(importViewModel) { Owner = mainWindow };
            importWindow.Show();
            Pump(1200);
            log.Add($"[import] loaded={importWindow.IsLoaded} size={importWindow.ActualWidth}x{importWindow.ActualHeight} categories={importViewModel.Categories.Count}");

            // 用真实目录跑一次扫描，确保候选列表与详情模板都被实例化
            var probeFolder = Directory.Exists(@"C:\Program Files\7-Zip")
                ? @"C:\Program Files\7-Zip"
                : AppPaths.ProgramDirectory;
            _ = importViewModel.ScanFolderCoreAsync(probeFolder);
            Pump(3000);
            log.Add($"[import] items={importViewModel.Items.Count} selected={importViewModel.SelectedItem?.Name} " +
                    $"main={importViewModel.SelectedItem?.MainExecutable?.FileName} status={importViewModel.StatusText}");
            importWindow.Close();

            var exportViewModel = new ExportViewModel(
                Services.GetRequiredService<ExportService>(),
                Services.GetRequiredService<AppRepository>(),
                Services.GetRequiredService<DialogService>());

            var exportWindow = new ExportWindow(exportViewModel) { Owner = mainWindow };
            exportWindow.Show();
            Pump(1400);
            log.Add($"[export] loaded={exportWindow.IsLoaded} size={exportWindow.ActualWidth}x{exportWindow.ActualHeight} apps={exportViewModel.Apps.Count} target={exportViewModel.TargetPath}");
            exportWindow.Close();

            var apps = mainViewModel.Library.Apps;
            if (apps.Count > 0)
            {
                var editorViewModel = new AppEditorViewModel(
                    apps[0].Entry,
                    mainViewModel.Library.Categories,
                    Services.GetRequiredService<AppRepository>(),
                    Services.GetRequiredService<IconService>(),
                    paths,
                    Services.GetRequiredService<DialogService>());

                var editWindow = new EditAppWindow(editorViewModel) { Owner = mainWindow };
                editWindow.Show();
                Pump(900);
                log.Add($"[edit] loaded={editWindow.IsLoaded} size={editWindow.ActualWidth}x{editWindow.ActualHeight} tags='{editorViewModel.TagsText}'");
                editWindow.Close();
            }

            mainWindow.Close();
            log.Add("[done] all windows loaded");
        }
        catch (Exception ex)
        {
            log.Add("[error] " + ex);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(paths.CacheDirectory, "uitest.log"), log, Encoding.UTF8);
        }

        static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(milliseconds),
                DispatcherPriority.Background,
                (_, _) => frame.Continue = false,
                Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    private sealed class UiTraceListener(List<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                sink.Add("[binding] " + message);
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生未处理的错误：\n\n{e.Exception.Message}",
            "HNova Portable",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
