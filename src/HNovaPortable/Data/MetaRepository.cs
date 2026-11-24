using Microsoft.Data.Sqlite;

namespace HNovaPortable.Data;

/// <summary>library.db 内的键值元数据（库标识、结构版本、创建时间）。</summary>
public sealed class MetaRepository(Database database)
{
    public const string LibraryIdKey = "library_id";
    public const string SchemaVersionKey = "schema_version";
    public const string CreatedUtcKey = "created_utc";

    private readonly Database _database = database;

    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        return await GetValueAsync(connection, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetLibraryIdAsync(CancellationToken cancellationToken = default)
        => await GetValueAsync(LibraryIdKey, cancellationToken).ConfigureAwait(false) ?? string.Empty;

    public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> GetValueAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
