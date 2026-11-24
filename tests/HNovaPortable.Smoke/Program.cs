using System.IO;
using System.Text;
using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;
using HNovaPortable.Services;
using HNovaPortable.Services.Scanning;

namespace HNovaPortable.Smoke;

/// <summary>
/// 端到端冒烟测试：扫描 → 导入（托管/引用）→ 重扫描 → 导出 → 导入库包。
/// 全程使用临时目录，不触碰真实软件库。
/// </summary>
internal static class Program
{
    private static readonly List<string> Log = [];
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "HNovaSmoke-" + DateTime.Now.ToString("HHmmss"));
        var libraryA = Path.Combine(workspace, "LibraryA");
        var libraryB = Path.Combine(workspace, "LibraryB");
        var exportTarget = Path.Combine(workspace, "Export");

        try
        {
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "placeholder.txt"), "smoke");

            var source = FindTestSource();
            if (source is null)
            {
                Console.WriteLine("找不到可用于测试的源目录，跳过");
                return 2;
            }

            Info($"测试来源：{source}");

            // ---------- 库 A ----------
            var pathsA = AppPaths.Create(["--root", libraryA]);
            var dbA = new Database(pathsA);
            dbA.Initialize();

            var metaA = new MetaRepository(dbA);
            var appsA = new AppRepository(dbA);
            var categoriesA = new CategoryRepository(dbA);
            var libraryA_Service = new LibraryService(pathsA, dbA, appsA, categoriesA, metaA);
            libraryA_Service.InitializeAsync().GetAwaiter().GetResult();

            var scanner = new ScanService();
            var iconsA = new IconService(pathsA);
            var importerA = new ImportService(pathsA, appsA, iconsA);
            var maintenanceA = new MaintenanceService(pathsA, appsA, iconsA);
            var exporter = new ExportService(pathsA, appsA);

            // 扫描
            var analysis = scanner.AnalyzeFolderAsync(source).GetAwaiter().GetResult();
            Check(analysis.Candidates.Count > 0, $"扫描应识别到候选主程序（找到 {analysis.Candidates.Count}）");
            Check(analysis.Best is not null, "扫描应选出最佳主程序");
            Info($"识别结果：{analysis.SuggestedName} → {analysis.Best?.FileName}（{analysis.Best?.ScoreSummary}）");

            // 托管导入
            var managed = importerA.ImportAsync(new ImportRequest
            {
                SourcePath = analysis.FolderPath,
                MainExecutablePath = analysis.Best!.FullPath,
                Name = "SmokeApp",
                Mode = AppLocationMode.Managed,
                CopyWholeFolder = true,
                Flags = analysis.Flags,
            }).GetAwaiter().GetResult();

            Check(managed.Success, "托管导入应成功：" + managed.Error);
            Check(Directory.Exists(Path.Combine(pathsA.AppsDirectory, "SmokeApp")), "托管目录应已复制到 Apps");
            Check(File.Exists(managed.ExecutablePath), "导入后的主程序应存在");

            var entries = appsA.ListAsync(new AppQuery()).GetAwaiter().GetResult();
            foreach (var e in entries)
            {
                Info($"记录：id={e.Id} name={e.Name} mode={e.Mode} rel={e.RelativePath} tags={string.Join('|', e.Tags)}");
            }

            using (var raw = Database.OpenAt(pathsA.DatabaseFile, readOnly: true))
            using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM apps;";
                Info("raw count = " + cmd.ExecuteScalar());
            }

            Check(entries.Count == 1, $"库中应有 1 条记录（实际 {entries.Count}）");
            var managedEntry = entries[0];
            Check(managedEntry.Mode == AppLocationMode.Managed, "记录应为托管模式");
            Check(managedEntry.RelativePath?.StartsWith("Apps/", StringComparison.Ordinal) == true,
                "托管记录必须存库内相对路径（实际：" + managedEntry.RelativePath + "）");
            Check(!Path.IsPathRooted(managedEntry.RelativePath ?? "x"), "相对路径不应包含盘符");
            Check(managedEntry.IconKey is not null, "应生成图标缓存");
            Check(File.Exists(Path.Combine(pathsA.IconsDirectory, managedEntry.IconKey!)), "图标文件应存在");
            Info($"托管条目：{managedEntry.RelativePath} · 图标 {managedEntry.IconKey}");

            // 引用导入（同一来源，另起一条）
            var linked = importerA.ImportAsync(new ImportRequest
            {
                SourcePath = analysis.Best!.FullPath,
                Name = "SmokeLinked",
                Mode = AppLocationMode.Linked,
                CopyWholeFolder = false,
            }).GetAwaiter().GetResult();

            Check(linked.Success, "引用导入应成功：" + linked.Error);
            var linkedEntry = appsA.ListAsync(new AppQuery()).GetAwaiter().GetResult()
                .FirstOrDefault(a => a.Name == "SmokeLinked");
            Check(linkedEntry is not null, "引用条目应存在");
            Check(linkedEntry!.Mode == AppLocationMode.Linked && Path.IsPathRooted(linkedEntry.LinkedPath ?? ""),
                "引用条目应保存绝对路径");
            Check(linkedEntry.PortabilityFlags.HasFlag(PortabilityFlags.LinkedOutsideLibrary),
                "库外引用应带有迁移风险标记");

            // 重扫描
            var rescan = maintenanceA.RescanAsync().GetAwaiter().GetResult();
            Check(rescan.Checked == 2, $"重扫描应检查 2 条（实际 {rescan.Checked}）");
            Check(rescan.Missing == 0, $"重扫描不应发现缺失（实际缺失 {rescan.Missing}）");

            // 导出（文件夹形态，含引用记录但不复制）
            var export = exporter.ExportAsync(new ExportOptions
            {
                TargetPath = exportTarget,
                Format = ExportFormat.Folder,
                IncludeProgramFiles = false,
                ConvertLinkedToManaged = false,
                IncludeLinkedRecords = true,
            }).GetAwaiter().GetResult();

            Check(export.Success, "导出应成功：" + export.Error);
            Check(File.Exists(Path.Combine(exportTarget, "export-manifest.json")), "导出应生成 manifest");
            Check(File.Exists(Path.Combine(exportTarget, "Data", "library.db")), "导出应包含 library.db");
            Check(Directory.Exists(Path.Combine(exportTarget, "Apps", "SmokeApp")), "导出应包含托管软件目录");
            Check(File.Exists(Path.Combine(exportTarget, managedEntry.RelativePath!)),
                "导出包内的主程序应可执行");

            var exportedDb = Path.Combine(exportTarget, "Data", "library.db");
            using (var connection = Database.OpenAt(exportedDb, readOnly: true))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM apps;";
                var count = Convert.ToInt32(command.ExecuteScalar());
                Check(count == 2, $"导出库应包含 2 条记录（实际 {count}）");
            }

            // ---------- 库 B：导入库包 ----------
            var pathsB = AppPaths.Create(["--root", libraryB]);
            var dbB = new Database(pathsB);
            dbB.Initialize();

            var metaB = new MetaRepository(dbB);
            var appsB = new AppRepository(dbB);
            var categoriesB = new CategoryRepository(dbB);
            var libraryServiceB = new LibraryService(pathsB, dbB, appsB, categoriesB, metaB);
            libraryServiceB.InitializeAsync().GetAwaiter().GetResult();

            var iconsB = new IconService(pathsB);
            var packageImporter = new PackageImportService(pathsB, appsB, categoriesB, iconsB);
            var packageResult = packageImporter.ImportAsync(exportTarget).GetAwaiter().GetResult();

            Check(packageResult.Success, "库包导入应成功：" + packageResult.Error);
            Check(packageResult.Imported == 2, $"应导入 2 条（实际 {packageResult.Imported}）");

            var entriesB = appsB.ListAsync(new AppQuery()).GetAwaiter().GetResult();
            Check(entriesB.Count == 2, $"库 B 应有 2 条记录（实际 {entriesB.Count}）");

            var movedEntry = entriesB.FirstOrDefault(a => a.Name == "SmokeApp");
            Check(movedEntry is not null, "库 B 应包含 SmokeApp");
            Check(movedEntry!.RelativePath?.StartsWith("Apps/", StringComparison.Ordinal) == true,
                "导入后仍应是相对路径");
            Check(movedEntry.ResolveExecutablePath(pathsB.Root) is not null, "库 B 中主程序应可解析");
            Check(File.Exists(movedEntry.ResolveExecutablePath(pathsB.Root)!), "库 B 中主程序文件应存在");

            var relocateEntry = entriesB.First(a => a.Name == "SmokeLinked");
            Check(relocateEntry.Health == AppHealth.NeedsRelocate,
                "库包内不存在的引用应标记为需要重新定位（实际 " + relocateEntry.Health + "）");

            // ---------- ZIP 往返：引用模式转为托管后打包 ----------
            var zipPath = Path.Combine(workspace, "library-export.zip");
            var zipExport = exporter.ExportAsync(new ExportOptions
            {
                TargetPath = zipPath,
                Format = ExportFormat.Zip,
                IncludeProgramFiles = false,
                ConvertLinkedToManaged = true,
                IncludeLinkedRecords = false,
            }).GetAwaiter().GetResult();

            Check(zipExport.Success, "ZIP 导出应成功：" + zipExport.Error);
            Check(File.Exists(zipPath), "ZIP 文件应存在");
            Check(new FileInfo(zipPath).Length > 0, "ZIP 文件不应为空");

            var libraryC = Path.Combine(workspace, "LibraryC");
            var pathsC = AppPaths.Create(["--root", libraryC]);
            var dbC = new Database(pathsC);
            dbC.Initialize();
            var appsC = new AppRepository(dbC);
            var categoriesC = new CategoryRepository(dbC);
            var metaC = new MetaRepository(dbC);
            new LibraryService(pathsC, dbC, appsC, categoriesC, metaC).InitializeAsync().GetAwaiter().GetResult();

            var iconsC = new IconService(pathsC);
            var zipImport = new PackageImportService(pathsC, appsC, categoriesC, iconsC)
                .ImportAsync(zipPath).GetAwaiter().GetResult();

            Check(zipImport.Success, "ZIP 导入应成功：" + zipImport.Error);
            Check(zipImport.Imported == 2, $"ZIP 应导入 2 条（实际 {zipImport.Imported}）");

            var entriesC = appsC.ListAsync(new AppQuery()).GetAwaiter().GetResult();
            Check(entriesC.Count == 2, $"库 C 应有 2 条记录（实际 {entriesC.Count}）");
            Check(entriesC.All(e => e.Mode == AppLocationMode.Managed), "转换为托管后条目都应使用托管模式");
            Check(entriesC.All(e => e.ResolveExecutablePath(pathsC.Root) is not null), "库 C 中所有主程序都应可解析");

            // 迁移后重新读取统计，验证整库可用
            var statsC = new LibraryService(pathsC, dbC, appsC, categoriesC, metaC);
            statsC.InitializeAsync().GetAwaiter().GetResult();
            Check(statsC.Stats.AppCount == 2, $"统计应读到 2 个软件（实际 {statsC.Stats.AppCount}）");

            Info("全部断言通过");
        }
        catch (Exception ex)
        {
            _failures++;
            Log.Add("[exception] " + ex);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "hnova-smoke.log"), Log, Encoding.UTF8);
            TryCleanup(workspace);
        }

        Console.WriteLine(string.Join(Environment.NewLine, Log));
        Console.WriteLine(_failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static string? FindTestSource()
    {
        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\7-Zip",
                     @"C:\Program Files (x86)\7-Zip",
                     @"C:\Windows\System32\WindowsPowerShell",
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Log.Add("[pass] " + message);
            return;
        }

        _failures++;
        Log.Add("[FAIL] " + message);
    }

    private static void Info(string message) => Log.Add("[info] " + message);

    private static void TryCleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
