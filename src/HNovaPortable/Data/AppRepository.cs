using System.Globalization;
using System.Text;
using HNovaPortable.Models;
using Microsoft.Data.Sqlite;

namespace HNovaPortable.Data;

/// <summary>软件条目读写。路径字段存储的是“可迁移”的表示形式，绝不含程序目录绝对路径。</summary>
public sealed class AppRepository(Database database)
{
    private const char TagSeparator = '\u001f';

    private const string ColumnList = """
        id, name, sort_name, mode, rel_path, linked_path, work_dir, arguments, category_id,
        is_favorite, notes, github_url, website_url, icon_key, exe_version, product_name, company, description,
        size_bytes, health, portability_flags, launch_count, last_launch_utc, last_scan_utc,
        created_utc, updated_utc
        """;

    private const string TagSubQuery = """
        (SELECT GROUP_CONCAT(t.name, char(31)) FROM app_tags at JOIN tags t ON t.id = at.tag_id WHERE at.app_id = a.id) AS tag_names
        """;

    private readonly Database _database = database;
    private readonly TagRepository _tags = new(database);

    public async Task<IReadOnlyList<AppEntry>> ListAsync(AppQuery query, CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder($"SELECT {ColumnList}, {TagSubQuery} FROM apps a WHERE 1 = 1");
        var parameters = new List<(string Name, object Value)>();

        if (!string.IsNullOrWhiteSpace(query.CategoryId))
        {
            sql.Append(" AND a.category_id = $categoryId");
            parameters.Add(("$categoryId", query.CategoryId));
        }

        if (query.FavoritesOnly)
        {
            sql.Append(" AND a.is_favorite = 1");
        }

        if (query.UnhealthyOnly)
        {
            sql.Append(" AND a.health <> 0");
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            sql.Append("""
                 AND (a.name LIKE $search
                      OR IFNULL(a.notes, '') LIKE $search
                      OR IFNULL(a.description, '') LIKE $search
                      OR IFNULL(a.product_name, '') LIKE $search
                      OR EXISTS (SELECT 1 FROM app_tags at3 JOIN tags t3 ON t3.id = at3.tag_id
                                 WHERE at3.app_id = a.id AND t3.name LIKE $search))
                """);
            parameters.Add(("$search", "%" + query.SearchText.Trim() + "%"));
        }

        if (!string.IsNullOrWhiteSpace(query.TagName))
        {
            sql.Append("""
                 AND EXISTS (SELECT 1 FROM app_tags at4 JOIN tags t4 ON t4.id = at4.tag_id
                             WHERE at4.app_id = a.id AND t4.name = $tagName COLLATE NOCASE)
                """);
            parameters.Add(("$tagName", query.TagName.Trim()));
        }

        var direction = query.Descending ? "DESC" : "ASC";
        sql.Append(query.Sort switch
        {
            AppSort.RecentlyAdded => $" ORDER BY a.created_utc {direction}",
            AppSort.RecentlyUsed => $" ORDER BY a.last_launch_utc IS NULL, a.last_launch_utc {direction}",
            AppSort.LaunchCount => $" ORDER BY a.launch_count {direction}",
            AppSort.Size => $" ORDER BY a.size_bytes {direction}",
            _ => $" ORDER BY a.sort_name {direction}, a.name {direction}",
        });

        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = new List<AppEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public Task<AppEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        GetCoreAsync(id, readOnly: false, cancellationToken);

    internal Task<AppEntry?> GetForStartupAsync(string id) =>
        GetCoreAsync(id, readOnly: true, CancellationToken.None);

    private async Task<AppEntry?> GetCoreAsync(string id, bool readOnly, CancellationToken cancellationToken)
    {
        await using var connection = readOnly ? Database.OpenAt(_database.FilePath, readOnly: true) : _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ColumnList}, {TagSubQuery} FROM apps a WHERE a.id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<HashSet<string>> ListIdsAsync(CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM apps;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => CountCoreAsync("SELECT COUNT(*) FROM apps;", cancellationToken);

    public Task<int> CountFavoritesAsync(CancellationToken cancellationToken = default)
        => CountCoreAsync("SELECT COUNT(*) FROM apps WHERE is_favorite = 1;", cancellationToken);

    public Task<int> CountUnhealthyAsync(CancellationToken cancellationToken = default)
        => CountCoreAsync("SELECT COUNT(*) FROM apps WHERE health <> 0;", cancellationToken);

    /// <summary>新增或更新条目（同一事务内同步标签）。</summary>
    public async Task UpsertAsync(AppEntry app, IEnumerable<string>? tagNames = null, CancellationToken cancellationToken = default)
    {
        app.UpdatedUtc = DateTimeOffset.UtcNow;

        await using var connection = _database.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO apps (
                    id, name, sort_name, mode, rel_path, linked_path, work_dir, arguments, category_id,
                    is_favorite, notes, github_url, website_url, icon_key, exe_version, product_name, company, description,
                    size_bytes, health, portability_flags, launch_count, last_launch_utc, last_scan_utc,
                    created_utc, updated_utc)
                VALUES (
                    $id, $name, $sortName, $mode, $relPath, $linkedPath, $workDir, $arguments, $categoryId,
                    $isFavorite, $notes, $githubUrl, $websiteUrl, $iconKey, $exeVersion, $productName, $company, $description,
                    $sizeBytes, $health, $flags, $launchCount, $lastLaunchUtc, $lastScanUtc,
                    $createdUtc, $updatedUtc)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    sort_name = excluded.sort_name,
                    mode = excluded.mode,
                    rel_path = excluded.rel_path,
                    linked_path = excluded.linked_path,
                    work_dir = excluded.work_dir,
                    arguments = excluded.arguments,
                    category_id = excluded.category_id,
                    is_favorite = excluded.is_favorite,
                    notes = excluded.notes,
                    github_url = excluded.github_url,
                    website_url = excluded.website_url,
                    icon_key = excluded.icon_key,
                    exe_version = excluded.exe_version,
                    product_name = excluded.product_name,
                    company = excluded.company,
                    description = excluded.description,
                    size_bytes = excluded.size_bytes,
                    health = excluded.health,
                    portability_flags = excluded.portability_flags,
                    last_scan_utc = excluded.last_scan_utc,
                    updated_utc = excluded.updated_utc;
                """;

            command.Parameters.AddWithValue("$id", app.Id);
            command.Parameters.AddWithValue("$name", app.Name);
            command.Parameters.AddWithValue("$sortName", string.IsNullOrWhiteSpace(app.SortName) ? BuildSortName(app.Name) : app.SortName);
            command.Parameters.AddWithValue("$mode", (int)app.Mode);
            command.Parameters.AddWithValue("$relPath", (object?)app.RelativePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$linkedPath", (object?)app.LinkedPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$workDir", (object?)app.WorkDirectory ?? DBNull.Value);
            command.Parameters.AddWithValue("$arguments", (object?)app.Arguments ?? DBNull.Value);
            command.Parameters.AddWithValue("$categoryId", (object?)app.CategoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("$isFavorite", app.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("$githubUrl", (object?)app.GithubUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$websiteUrl", (object?)app.WebsiteUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)app.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$iconKey", (object?)app.IconKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$exeVersion", (object?)app.ExeVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$productName", (object?)app.ProductName ?? DBNull.Value);
            command.Parameters.AddWithValue("$company", (object?)app.Company ?? DBNull.Value);
            command.Parameters.AddWithValue("$description", (object?)app.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("$sizeBytes", app.SizeBytes);
            command.Parameters.AddWithValue("$health", (int)app.Health);
            command.Parameters.AddWithValue("$flags", (int)app.PortabilityFlags);
            command.Parameters.AddWithValue("$launchCount", app.LaunchCount);
            command.Parameters.AddWithValue("$lastLaunchUtc", app.LastLaunchUtc?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$lastScanUtc", app.LastScanUtc?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$createdUtc", app.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updatedUtc", app.UpdatedUtc.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (tagNames is not null)
        {
            await _tags.SetAppTagsAsync(connection, transaction, app.Id, tagNames, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM apps WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetFavoriteAsync(string id, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE apps SET is_favorite = $value, updated_utc = $updated WHERE id = $id;";
        command.Parameters.AddWithValue("$value", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordLaunchAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenExisting();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE apps SET launch_count = launch_count + 1, last_launch_utc = $now, updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateHealthAsync(string id, AppHealth health, string? exeVersion, long? sizeBytes, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE apps SET health = $health, exe_version = COALESCE($version, exe_version),
                   size_bytes = COALESCE($size, size_bytes), last_scan_utc = $now, updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$health", (int)health);
        command.Parameters.AddWithValue("$version", (object?)exeVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)sizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLocationAsync(string id, AppLocationMode mode, string? relativePath, string? linkedPath, PortabilityFlags flags, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE apps SET mode = $mode, rel_path = $relPath, linked_path = $linkedPath,
                   portability_flags = $flags, health = 0, updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$mode", (int)mode);
        command.Parameters.AddWithValue("$relPath", (object?)relativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$linkedPath", (object?)linkedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$flags", (int)flags);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CountCoreAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    private static AppEntry Map(SqliteDataReader reader)
    {
        var app = new AppEntry
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            SortName = GetString(reader, "sort_name") ?? string.Empty,
            Mode = (AppLocationMode)reader.GetInt32(reader.GetOrdinal("mode")),
            RelativePath = GetString(reader, "rel_path"),
            LinkedPath = GetString(reader, "linked_path"),
            WorkDirectory = GetString(reader, "work_dir"),
            Arguments = GetString(reader, "arguments"),
            CategoryId = GetString(reader, "category_id"),
            IsFavorite = reader.GetInt32(reader.GetOrdinal("is_favorite")) != 0,
            Notes = GetString(reader, "notes"),
            GithubUrl = GetString(reader, "github_url"),
            WebsiteUrl = GetString(reader, "website_url"),
            IconKey = GetString(reader, "icon_key"),
            ExeVersion = GetString(reader, "exe_version"),
            ProductName = GetString(reader, "product_name"),
            Company = GetString(reader, "company"),
            Description = GetString(reader, "description"),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
            Health = (AppHealth)reader.GetInt32(reader.GetOrdinal("health")),
            PortabilityFlags = (PortabilityFlags)reader.GetInt32(reader.GetOrdinal("portability_flags")),
            LaunchCount = reader.GetInt32(reader.GetOrdinal("launch_count")),
            LastLaunchUtc = GetDateTime(reader, "last_launch_utc"),
            LastScanUtc = GetDateTime(reader, "last_scan_utc"),
            CreatedUtc = GetDateTime(reader, "created_utc") ?? DateTimeOffset.UtcNow,
            UpdatedUtc = GetDateTime(reader, "updated_utc") ?? DateTimeOffset.UtcNow,
        };

        var tagNames = GetString(reader, "tag_names");
        if (!string.IsNullOrEmpty(tagNames))
        {
            app.Tags.AddRange(tagNames.Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        return app;
    }

    private static string? GetString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? GetDateTime(SqliteDataReader reader, string column)
    {
        var text = GetString(reader, column);
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    /// <summary>排序键：去掉前导符号、统一小写，避免 “(x)” 排在最后。</summary>
    public static string BuildSortName(string name)
    {
        var trimmed = name.Trim().TrimStart('(', '[', '{', '\'', '"', '#', '*', '-').Trim();
        return trimmed.ToLowerInvariant();
    }
}
