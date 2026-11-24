using HNovaPortable.Core;
using Microsoft.Data.Sqlite;

namespace HNovaPortable.Data;

/// <summary>
/// SQLite 访问入口：连接工厂 + 结构迁移。
/// 采用 rollback journal（DELETE）而非 WAL，避免在 U 盘 / 网络盘上产生不可靠的副作用。
/// </summary>
public sealed class Database
{
    public const int CurrentSchemaVersion = 2;

    public Database(AppPaths paths)
    {
        FilePath = paths.DatabaseFile;
    }

    public string FilePath { get; }

    public SqliteConnection Open() => OpenAt(FilePath);

    public SqliteConnection OpenExisting() => OpenAt(FilePath, createIfMissing: false);

    /// <summary>打开任意位置的库文件（用于读取导出的库包、备份校验等）。</summary>
    public static SqliteConnection OpenAt(string databasePath, bool readOnly = false, bool createIfMissing = true)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly
                : createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,

            // 外部库（备份/库包）不参与连接池：否则关闭连接后仍持有文件句柄，
            // 会导致随后的哈希、打包、删除操作失败。
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = readOnly
            ? "PRAGMA foreign_keys=ON;"
            : "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>建库/升级结构，并确保库标识存在。返回当前结构版本。</summary>
    public int Initialize()
    {
        using var connection = Open();
        var version = GetUserVersion(connection);

        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Schema.V1;
                command.ExecuteNonQuery();
            }

            SetUserVersion(connection, transaction, 1);
            transaction.Commit();
            version = 1;
        }

        if (version < 2)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                ALTER TABLE apps ADD COLUMN github_url TEXT NULL;
                ALTER TABLE apps ADD COLUMN website_url TEXT NULL;
                UPDATE meta SET value = '2' WHERE key = 'schema_version';
                """;
            command.ExecuteNonQuery();
            SetUserVersion(connection, transaction, 2);
            transaction.Commit();
            version = 2;
        }

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT OR IGNORE INTO meta (key, value) VALUES
                    ('schema_version', $version),
                    ('library_id', $libraryId),
                    ('created_utc', $createdUtc);
                """;
            seed.Parameters.AddWithValue("$version", version.ToString());
            seed.Parameters.AddWithValue("$libraryId", Guid.NewGuid().ToString("N"));
            seed.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
            seed.ExecuteNonQuery();
        }

        return version;
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}
