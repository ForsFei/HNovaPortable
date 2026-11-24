using HNovaPortable.Models;

namespace HNovaPortable.Data;

public sealed class CategoryRepository(Database database)
{
    private readonly Database _database = database;

    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.name, c.color, c.sort_order, c.created_utc, COUNT(a.id) AS app_count
            FROM categories c
            LEFT JOIN apps a ON a.category_id = c.id
            GROUP BY c.id, c.name, c.color, c.sort_order, c.created_utc
            ORDER BY c.sort_order, c.name;
            """;

        var result = new List<Category>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new Category
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Color = reader.IsDBNull(2) ? null : reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                CreatedUtc = DateTimeOffset.TryParse(reader.GetString(4), out var created) ? created : DateTimeOffset.UtcNow,
                AppCount = reader.GetInt32(5),
            });
        }

        return result;
    }

    /// <summary>按名称取分类 id，不存在则创建（用于导入库包时合并分类）。</summary>
    public async Task<string> GetOrCreateByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        await using var connection = _database.Open();

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id FROM categories WHERE name = $name COLLATE NOCASE LIMIT 1;";
            select.Parameters.AddWithValue("$name", trimmed);
            if (await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string existing)
            {
                return existing;
            }
        }

        var id = "cat-" + Guid.NewGuid().ToString("N")[..8];
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO categories (id, name, color, sort_order, created_utc)
            VALUES ($id, $name, NULL, 500, $created);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$name", trimmed);
        insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<(bool Success, string Message)> DeleteAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM categories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", categoryId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0 ? (true, "分类已删除") : (false, "分类不存在");
    }
}
