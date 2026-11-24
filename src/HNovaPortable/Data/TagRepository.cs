using HNovaPortable.Models;
using Microsoft.Data.Sqlite;

namespace HNovaPortable.Data;

public sealed class TagRepository(Database database)
{
    private const char TagSeparator = '\u001f';

    private readonly Database _database = database;

    public async Task<IReadOnlyList<Tag>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.name, t.created_utc, COUNT(at.app_id)
            FROM tags t
            LEFT JOIN app_tags at ON at.tag_id = t.id
            GROUP BY t.id, t.name, t.created_utc
            ORDER BY t.name;
            """;

        var result = new List<Tag>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new Tag
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                CreatedUtc = DateTimeOffset.TryParse(reader.GetString(2), out var created) ? created : DateTimeOffset.UtcNow,
                AppCount = reader.GetInt32(3),
            });
        }

        return result;
    }

    /// <summary>用一组标签名覆盖某个软件的标签（自动创建不存在的标签）。</summary>
    public async Task SetAppTagsAsync(SqliteConnection connection, SqliteTransaction? transaction, string appId, IEnumerable<string> tagNames, CancellationToken cancellationToken = default)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM app_tags WHERE app_id = $appId;";
            clear.Parameters.AddWithValue("$appId", appId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var rawName in tagNames)
        {
            var name = rawName.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var tagId = await GetOrCreateTagIdAsync(connection, transaction, name, cancellationToken).ConfigureAwait(false);

            await using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO app_tags (app_id, tag_id) VALUES ($appId, $tagId);";
            link.Parameters.AddWithValue("$appId", appId);
            link.Parameters.AddWithValue("$tagId", tagId);
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetAppTagsAsync(string appId, IEnumerable<string> tagNames, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await SetAppTagsAsync(connection, null, appId, tagNames, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> GetOrCreateTagIdAsync(SqliteConnection connection, SqliteTransaction? transaction, string name, CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT id FROM tags WHERE name = $name COLLATE NOCASE LIMIT 1;";
        select.Parameters.AddWithValue("$name", name);
        if (await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string existing)
        {
            return existing;
        }

        var id = Guid.NewGuid().ToString("N");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO tags (id, name, created_utc) VALUES ($id, $name, $created);";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>解析 GROUP_CONCAT 结果。</summary>
    public static IReadOnlyList<string> SplitTagNames(string? concatenated)
        => string.IsNullOrEmpty(concatenated)
            ? []
            : concatenated.Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>把多个标签名合并成 GROUP_CONCAT 使用的分隔字符串（仅用于测试/导出）。</summary>
    public static string JoinTagNames(IEnumerable<string> names) => string.Join(TagSeparator, names);
}
