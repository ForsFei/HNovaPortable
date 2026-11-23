using HNovaPortable.Core;
using HNovaPortable.Data;
using HNovaPortable.Models;

namespace HNovaPortable.Services;

/// <summary>
/// 软件库的初始化与自检：建库、读取库标识、统计信息。
/// 后续的维护（重扫描、失效检测、备份）也落在这一层。
/// </summary>
public sealed class LibraryService(
    AppPaths paths,
    Database database,
    AppRepository appRepository,
    CategoryRepository categoryRepository,
    MetaRepository metaRepository)
{
    public AppPaths Paths { get; } = paths;

    public string LibraryId { get; private set; } = string.Empty;

    public int SchemaVersion { get; private set; }

    public LibraryStats Stats { get; private set; } = new(0, 0, 0, 0);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SchemaVersion = await Task.Run(database.Initialize, cancellationToken).ConfigureAwait(false);
        LibraryId = await metaRepository.GetLibraryIdAsync(cancellationToken).ConfigureAwait(false);
        await RefreshStatsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshStatsAsync(CancellationToken cancellationToken = default)
    {
        var apps = await appRepository.CountAsync(cancellationToken).ConfigureAwait(false);
        var favorites = await appRepository.CountFavoritesAsync(cancellationToken).ConfigureAwait(false);
        var unhealthy = await appRepository.CountUnhealthyAsync(cancellationToken).ConfigureAwait(false);
        var categories = await categoryRepository.ListAsync(cancellationToken).ConfigureAwait(false);

        Stats = new LibraryStats(apps, favorites, unhealthy, categories.Count);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
        => await categoryRepository.ListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>库根相对布局的展示文本（便于确认“托管软件在库内”）。</summary>
    public string AppsDirectory => Paths.AppsDirectory;

    public string DataDirectory => Paths.DataDirectory;
}
