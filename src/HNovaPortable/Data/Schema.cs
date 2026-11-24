namespace HNovaPortable.Data;

internal static class Schema
{
    /// <summary>
    /// v1 初始结构。所有时间字段均为 ISO-8601 UTC 字符串，路径统一使用 '/' 分隔。
    /// </summary>
    public const string V1 = """
        CREATE TABLE IF NOT EXISTS categories (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL UNIQUE,
            color       TEXT NULL,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tags (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL UNIQUE,
            created_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS apps (
            id                TEXT PRIMARY KEY,
            name              TEXT NOT NULL,
            sort_name         TEXT NOT NULL DEFAULT '',
            mode              INTEGER NOT NULL DEFAULT 0,
            rel_path          TEXT NULL,
            linked_path       TEXT NULL,
            work_dir          TEXT NULL,
            arguments         TEXT NULL,
            category_id       TEXT NULL REFERENCES categories(id) ON DELETE SET NULL,
            is_favorite       INTEGER NOT NULL DEFAULT 0,
            notes             TEXT NULL,
            icon_key          TEXT NULL,
            exe_version       TEXT NULL,
            product_name      TEXT NULL,
            company           TEXT NULL,
            description       TEXT NULL,
            size_bytes        INTEGER NOT NULL DEFAULT 0,
            health            INTEGER NOT NULL DEFAULT 0,
            portability_flags INTEGER NOT NULL DEFAULT 0,
            launch_count      INTEGER NOT NULL DEFAULT 0,
            last_launch_utc   TEXT NULL,
            last_scan_utc     TEXT NULL,
            created_utc       TEXT NOT NULL,
            updated_utc       TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_apps_sort_name ON apps(sort_name);
        CREATE INDEX IF NOT EXISTS ix_apps_category  ON apps(category_id);
        CREATE INDEX IF NOT EXISTS ix_apps_favorite  ON apps(is_favorite);

        CREATE TABLE IF NOT EXISTS app_tags (
            app_id TEXT NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY (app_id, tag_id)
        );

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        INSERT OR IGNORE INTO categories (id, name, color, sort_order, created_utc) VALUES
            ('cat-dev',     '开发工具', '#3B82F6', 10, '2024-01-01T00:00:00.000Z'),
            ('cat-office',  '办公文档', '#8B5CF6', 20, '2024-01-01T00:00:00.000Z'),
            ('cat-graphic', '图形图像', '#EC4899', 30, '2024-01-01T00:00:00.000Z'),
            ('cat-media',   '影音多媒体', '#F59E0B', 40, '2024-01-01T00:00:00.000Z'),
            ('cat-network', '网络工具', '#06B6D4', 50, '2024-01-01T00:00:00.000Z'),
            ('cat-system',  '系统工具', '#10B981', 60, '2024-01-01T00:00:00.000Z'),
            ('cat-game',    '游戏娱乐', '#EF4444', 70, '2024-01-01T00:00:00.000Z'),
            ('cat-other',   '其他',     '#64748B', 999, '2024-01-01T00:00:00.000Z');
        """;
}
