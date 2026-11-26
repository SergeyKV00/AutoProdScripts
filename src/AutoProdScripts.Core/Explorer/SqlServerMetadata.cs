using System.Text;
using AutoProdScripts.Core.Connections;
using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Explorer;

/// <summary>
/// Структура БД для дерева. Загрузка ленивая: каждый метод — одна папка одного узла.
/// Реализация на провайдера СУБД; пока есть только MS SQL Server.
/// </summary>
public interface IDbMetadataProvider
{
    /// <summary>Папки внутри базы, в порядке показа.</summary>
    IReadOnlyList<DbFolder> DatabaseFolders { get; }

    /// <summary>Папки внутри таблицы или представления.</summary>
    IReadOnlyList<DbFolder> ChildFolders(DbObjectKind parent);

    Task<IReadOnlyList<DbObjectInfo>> GetDatabasesAsync(string connectionString, CancellationToken cancellationToken);

    /// <param name="connectionString">Уже указывает на нужную базу.</param>
    Task<IReadOnlyList<DbObjectInfo>> GetObjectsAsync(string connectionString, DbObjectKind kind, CancellationToken cancellationToken);

    Task<IReadOnlyList<DbObjectInfo>> GetChildrenAsync(string connectionString, DbObjectInfo parent, DbObjectKind kind, CancellationToken cancellationToken);

    /// <summary>Столбцы таблицы/представления или параметры процедуры/функции.</summary>
    Task<IReadOnlyList<DbColumnInfo>> GetColumnsAsync(string connectionString, DbObjectInfo item, CancellationToken cancellationToken);

    Task<string> GetDefinitionAsync(string connectionString, DbObjectInfo item, CancellationToken cancellationToken);

    Task<DbObjectPermissions> GetPermissionsAsync(string connectionString, DbObjectInfo item, CancellationToken cancellationToken);

    /// <summary>Поиск объекта по имени (schema может быть null — тогда в любой схеме, сначала dbo).</summary>
    Task<DbObjectInfo?> FindObjectAsync(string connectionString, string? schema, string name, CancellationToken cancellationToken);
}

public static class DbMetadata
{
    public static IDbMetadataProvider For(DbProvider provider) => provider switch
    {
        _ => SqlServerMetadata.Instance
    };
}

/// <summary>MS SQL Server: только SELECT из каталожных представлений sys.*.</summary>
public sealed class SqlServerMetadata : IDbMetadataProvider
{
    public static readonly SqlServerMetadata Instance = new();

    private const int TimeoutSeconds = 30;

    private static readonly DbFolder[] Folders =
    [
        new(DbObjectKind.Table, "Таблицы"),
        new(DbObjectKind.View, "Представления"),
        new(DbObjectKind.Procedure, "Хранимые процедуры"),
        new(DbObjectKind.Function, "Функции"),
        new(DbObjectKind.Synonym, "Синонимы"),
        new(DbObjectKind.Sequence, "Последовательности"),
        new(DbObjectKind.Trigger, "Триггеры базы"),
        new(DbObjectKind.Schema, "Схемы"),
        new(DbObjectKind.User, "Пользователи"),
        new(DbObjectKind.Role, "Роли")
    ];

    private static readonly DbFolder[] TableFolders =
    [
        new(DbObjectKind.Column, "Столбцы"),
        new(DbObjectKind.Key, "Ключи"),
        new(DbObjectKind.Index, "Индексы"),
        new(DbObjectKind.Constraint, "Ограничения"),
        new(DbObjectKind.TableTrigger, "Триггеры")
    ];

    private static readonly DbFolder[] ViewFolders =
    [
        new(DbObjectKind.Column, "Столбцы"),
        new(DbObjectKind.Index, "Индексы"),
        new(DbObjectKind.TableTrigger, "Триггеры")
    ];

    public IReadOnlyList<DbFolder> DatabaseFolders => Folders;

    public IReadOnlyList<DbFolder> ChildFolders(DbObjectKind parent) => parent switch
    {
        DbObjectKind.Table => TableFolders,
        DbObjectKind.View => ViewFolders,
        _ => []
    };

    public Task<IReadOnlyList<DbObjectInfo>> GetDatabasesAsync(string connectionString, CancellationToken cancellationToken) =>
        ReadAsync(connectionString, """
            SELECT d.name, d.database_id, d.state_desc, HAS_DBACCESS(d.name)
            FROM sys.databases AS d
            ORDER BY d.name;
            """, null, r =>
        {
            var state = r.GetString(2);
            var access = !r.IsDBNull(3) && r.GetInt32(3) == 1;
            var online = state == "ONLINE";
            return new DbObjectInfo(DbObjectKind.Database, r.GetString(0),
                Detail: !online ? state.ToLowerInvariant() : !access ? "нет доступа" : null,
                IsSystem: r.GetInt32(1) <= 4,
                IsAvailable: online && access);
        }, cancellationToken);

    public Task<IReadOnlyList<DbObjectInfo>> GetObjectsAsync(string connectionString, DbObjectKind kind, CancellationToken cancellationToken)
    {
        var (sql, map) = kind switch
        {
            DbObjectKind.Table => (Objects("sys.tables", "o.is_ms_shipped"), Schemaed(DbObjectKind.Table)),
            DbObjectKind.View => (Objects("sys.views", "o.is_ms_shipped"), Schemaed(DbObjectKind.View)),
            DbObjectKind.Procedure => (Objects("sys.procedures", "o.is_ms_shipped"), Schemaed(DbObjectKind.Procedure)),
            DbObjectKind.Function => ("""
                SELECT s.name, o.name, CAST(o.is_ms_shipped AS int),
                       CASE o.type WHEN 'FN' THEN 'скалярная' WHEN 'FS' THEN 'скалярная CLR'
                                   WHEN 'IF' THEN 'табличная' WHEN 'TF' THEN 'табличная' ELSE 'табличная CLR' END
                FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                WHERE o.type IN ('FN', 'IF', 'TF', 'FS', 'FT')
                ORDER BY s.name, o.name;
                """, Schemaed(DbObjectKind.Function)),
            DbObjectKind.Synonym => ("""
                SELECT s.name, o.name, CAST(o.is_ms_shipped AS int), o.base_object_name
                FROM sys.synonyms AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                ORDER BY s.name, o.name;
                """, Schemaed(DbObjectKind.Synonym)),
            DbObjectKind.Sequence => ("""
                SELECT s.name, o.name, CAST(o.is_ms_shipped AS int), TYPE_NAME(o.user_type_id)
                FROM sys.sequences AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                ORDER BY s.name, o.name;
                """, Schemaed(DbObjectKind.Sequence)),
            DbObjectKind.Trigger => ("""
                SELECT NULL, t.name, CAST(t.is_ms_shipped AS int), CASE WHEN t.is_disabled = 1 THEN 'отключён' END
                FROM sys.triggers AS t
                WHERE t.parent_class = 0
                ORDER BY t.name;
                """, Plain(DbObjectKind.Trigger)),
            DbObjectKind.Schema => ("""
                SELECT NULL, s.name,
                       CASE WHEN s.schema_id BETWEEN 16384 AND 16399 OR s.name IN ('sys', 'INFORMATION_SCHEMA', 'guest') THEN 1 ELSE 0 END,
                       USER_NAME(s.principal_id)
                FROM sys.schemas AS s
                ORDER BY s.name;
                """, Plain(DbObjectKind.Schema)),
            DbObjectKind.User => ("""
                SELECT NULL, p.name, CASE WHEN p.principal_id < 5 OR p.name LIKE '##%' THEN 1 ELSE 0 END, LOWER(p.type_desc)
                FROM sys.database_principals AS p
                WHERE p.type IN ('S', 'U', 'G', 'E', 'X', 'C', 'K')
                ORDER BY p.name;
                """, Plain(DbObjectKind.User)),
            DbObjectKind.Role => ("""
                SELECT NULL, p.name, CASE WHEN p.is_fixed_role = 1 OR p.principal_id = 0 THEN 1 ELSE 0 END,
                       CASE WHEN p.type = 'A' THEN 'роль приложения' END
                FROM sys.database_principals AS p
                WHERE p.type IN ('R', 'A')
                ORDER BY p.name;
                """, Plain(DbObjectKind.Role)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return ReadAsync(connectionString, sql, null, map, cancellationToken);
    }

    public Task<IReadOnlyList<DbObjectInfo>> GetChildrenAsync(string connectionString, DbObjectInfo parent, DbObjectKind kind, CancellationToken cancellationToken)
    {
        var objectName = SqlGenerator.FullName(parent.Schema, parent.Name);
        var (sql, map) = kind switch
        {
            DbObjectKind.Column => ("""
                SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
                       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.indexes AS i
                                           ON i.object_id = ic.object_id AND i.index_id = ic.index_id AND i.is_primary_key = 1
                                         WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END
                FROM sys.columns AS c
                WHERE c.object_id = OBJECT_ID(@name)
                ORDER BY c.column_id;
                """, (Func<SqlDataReader, DbObjectInfo>)(r =>
            {
                var type = SqlGenerator.FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4));
                var detail = new StringBuilder(type).Append(r.GetBoolean(5) ? ", NULL" : ", NOT NULL");
                if (r.GetInt32(8) == 1)
                    detail.Insert(0, "PK, ");
                if (r.GetBoolean(6))
                    detail.Append(", identity");
                if (r.GetBoolean(7))
                    detail.Append(", вычисляемый");
                return new DbObjectInfo(DbObjectKind.Column, r.GetString(0), Detail: detail.ToString());
            })),
            DbObjectKind.Key => ("""
                SELECT k.name, CASE k.type WHEN 'PK' THEN 'первичный' ELSE 'уникальный' END
                FROM sys.key_constraints AS k WHERE k.parent_object_id = OBJECT_ID(@name)
                UNION ALL
                SELECT f.name, N'внешний → ' + OBJECT_SCHEMA_NAME(f.referenced_object_id) + N'.' + OBJECT_NAME(f.referenced_object_id)
                FROM sys.foreign_keys AS f WHERE f.parent_object_id = OBJECT_ID(@name)
                ORDER BY 1;
                """, Child(DbObjectKind.Key)),
            DbObjectKind.Index => ("""
                SELECT i.name, LOWER(i.type_desc) + CASE WHEN i.is_unique = 1 THEN ', уникальный' ELSE '' END
                       + CASE WHEN i.is_disabled = 1 THEN ', отключён' ELSE '' END
                FROM sys.indexes AS i
                WHERE i.object_id = OBJECT_ID(@name) AND i.name IS NOT NULL
                ORDER BY i.name;
                """, Child(DbObjectKind.Index)),
            DbObjectKind.Constraint => ("""
                SELECT c.name, N'check' FROM sys.check_constraints AS c WHERE c.parent_object_id = OBJECT_ID(@name)
                UNION ALL
                SELECT d.name, N'default → ' + COL_NAME(d.parent_object_id, d.parent_column_id)
                FROM sys.default_constraints AS d WHERE d.parent_object_id = OBJECT_ID(@name)
                ORDER BY 1;
                """, Child(DbObjectKind.Constraint)),
            DbObjectKind.TableTrigger => ("""
                SELECT t.name, CASE WHEN t.is_disabled = 1 THEN N'отключён' END
                FROM sys.triggers AS t WHERE t.parent_id = OBJECT_ID(@name)
                ORDER BY t.name;
                """, Child(DbObjectKind.TableTrigger)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        // У вложенных объектов схема — схема родителя: так строится их полное имя.
        return ReadAsync(connectionString, sql, objectName,
            r => map(r) with { Schema = kind == DbObjectKind.TableTrigger ? parent.Schema : null }, cancellationToken);
    }

    public Task<IReadOnlyList<DbColumnInfo>> GetColumnsAsync(string connectionString, DbObjectInfo item, CancellationToken cancellationToken)
    {
        var name = SqlGenerator.FullName(item.Schema, item.Name);
        if (item.Kind is DbObjectKind.Procedure or DbObjectKind.Function)
        {
            return ReadAsync(connectionString, """
                SELECT p.name, TYPE_NAME(p.user_type_id), p.max_length, p.precision, p.scale, p.is_output
                FROM sys.parameters AS p
                WHERE p.object_id = OBJECT_ID(@name) AND p.parameter_id > 0
                ORDER BY p.parameter_id;
                """, name, r => new DbColumnInfo(r.GetString(0),
                SqlGenerator.FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4)),
                IsOutput: r.GetBoolean(5)), cancellationToken);
        }

        return ReadAsync(connectionString, """
            SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
                   CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.indexes AS i
                                       ON i.object_id = ic.object_id AND i.index_id = ic.index_id AND i.is_primary_key = 1
                                     WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@name)
            ORDER BY c.column_id;
            """, name, r => new DbColumnInfo(r.GetString(0),
            SqlGenerator.FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4)),
            r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetInt32(8) == 1), cancellationToken);
    }

    public async Task<string> GetDefinitionAsync(string connectionString, DbObjectInfo item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case DbObjectKind.Table:
                return await TableDdlAsync(connectionString, item, cancellationToken);
            case DbObjectKind.Synonym:
                return $"CREATE SYNONYM {SqlGenerator.FullName(item.Schema, item.Name)} FOR {item.Detail};\n";
            case DbObjectKind.Schema:
                return $"CREATE SCHEMA {SqlGenerator.Quote(item.Name)}"
                       + (item.Detail is { Length: > 0 } owner ? $" AUTHORIZATION {SqlGenerator.Quote(owner)}" : string.Empty) + ";\n";
            case DbObjectKind.Sequence:
                var sequence = await ReadAsync(connectionString, """
                    SELECT TYPE_NAME(s.user_type_id), CAST(s.start_value AS nvarchar(50)), CAST(s.increment AS nvarchar(50))
                    FROM sys.sequences AS s WHERE s.object_id = OBJECT_ID(@name);
                    """, SqlGenerator.FullName(item.Schema, item.Name),
                    r => $"CREATE SEQUENCE {SqlGenerator.FullName(item.Schema, item.Name)} AS {r.GetString(0)}\n"
                         + $"    START WITH {r.GetString(1)}\n    INCREMENT BY {r.GetString(2)};\n", cancellationToken);
                return sequence.FirstOrDefault() ?? throw new InvalidOperationException("Последовательность не найдена.");
            case DbObjectKind.Trigger:
                var ddlTrigger = await ReadAsync(connectionString,
                    "SELECT OBJECT_DEFINITION(t.object_id) FROM sys.triggers AS t WHERE t.parent_class = 0 AND t.name = @name;",
                    item.Name, r => r.IsDBNull(0) ? null : r.GetString(0), cancellationToken);
                return ddlTrigger.FirstOrDefault() ?? throw NoDefinition();
            case DbObjectKind.View or DbObjectKind.Procedure or DbObjectKind.Function or DbObjectKind.TableTrigger:
                var text = await ReadAsync(connectionString, "SELECT OBJECT_DEFINITION(OBJECT_ID(@name));",
                    SqlGenerator.FullName(item.Schema, item.Name), r => r.IsDBNull(0) ? null : r.GetString(0), cancellationToken);
                return text.FirstOrDefault() ?? throw NoDefinition();
            default:
                throw new InvalidOperationException("Для этого объекта DDL не строится.");
        }
    }

    private static InvalidOperationException NoDefinition() =>
        new("Текст объекта недоступен: он зашифрован или нет права VIEW DEFINITION.");

    /// <summary>CREATE TABLE по каталогу: столбцы, первичный ключ, внешние ключи. Индексы — отдельными CREATE INDEX.</summary>
    private async Task<string> TableDdlAsync(string connectionString, DbObjectInfo table, CancellationToken cancellationToken)
    {
        var name = SqlGenerator.FullName(table.Schema, table.Name);
        var columns = await ReadAsync(connectionString, """
            SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable,
                   c.is_identity, CAST(ic.seed_value AS nvarchar(50)), CAST(ic.increment_value AS nvarchar(50)),
                   cc.definition, dc.definition
            FROM sys.columns AS c
            LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            WHERE c.object_id = OBJECT_ID(@name)
            ORDER BY c.column_id;
            """, name, r =>
        {
            var column = SqlGenerator.Quote(r.GetString(0));
            if (!r.IsDBNull(9))
                return $"{column} AS {r.GetString(9)}";
            var line = new StringBuilder($"{column} {SqlGenerator.FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4))}");
            if (r.GetBoolean(6))
                line.Append($" IDENTITY({r.GetString(7)}, {r.GetString(8)})");
            line.Append(r.GetBoolean(5) ? " NULL" : " NOT NULL");
            if (!r.IsDBNull(10))
                line.Append(" DEFAULT ").Append(r.GetString(10));
            return line.ToString();
        }, cancellationToken);
        if (columns.Count == 0)
            throw new InvalidOperationException("Таблица не найдена или нет прав на её описание.");

        var keys = await ReadAsync(connectionString, """
            SELECT k.name, k.type, i.type_desc,
                   STUFF((SELECT N', ' + QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END
                          FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'')
            FROM sys.key_constraints AS k
            JOIN sys.indexes AS i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
            WHERE k.parent_object_id = OBJECT_ID(@name)
            ORDER BY CASE k.type WHEN 'PK' THEN 0 ELSE 1 END, k.name;
            """, name, r => $"CONSTRAINT {SqlGenerator.Quote(r.GetString(0))} "
                          + (r.GetString(1) == "PK" ? "PRIMARY KEY " : "UNIQUE ")
                          + r.GetString(2) + $" ({r.GetString(3)})", cancellationToken);

        var foreignKeys = await ReadAsync(connectionString, """
            SELECT f.name, QUOTENAME(OBJECT_SCHEMA_NAME(f.referenced_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(f.referenced_object_id)),
                   STUFF((SELECT N', ' + QUOTENAME(COL_NAME(fc.parent_object_id, fc.parent_column_id))
                          FROM sys.foreign_key_columns AS fc WHERE fc.constraint_object_id = f.object_id
                          ORDER BY fc.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N''),
                   STUFF((SELECT N', ' + QUOTENAME(COL_NAME(fc.referenced_object_id, fc.referenced_column_id))
                          FROM sys.foreign_key_columns AS fc WHERE fc.constraint_object_id = f.object_id
                          ORDER BY fc.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N''),
                   f.delete_referential_action_desc, f.update_referential_action_desc
            FROM sys.foreign_keys AS f
            WHERE f.parent_object_id = OBJECT_ID(@name)
            ORDER BY f.name;
            """, name, r => $"ALTER TABLE {name} ADD CONSTRAINT {SqlGenerator.Quote(r.GetString(0))}\n"
                          + $"    FOREIGN KEY ({r.GetString(2)}) REFERENCES {r.GetString(1)} ({r.GetString(3)})"
                          + (r.GetString(4) != "NO_ACTION" ? " ON DELETE " + r.GetString(4).Replace('_', ' ') : string.Empty)
                          + (r.GetString(5) != "NO_ACTION" ? " ON UPDATE " + r.GetString(5).Replace('_', ' ') : string.Empty)
                          + ";", cancellationToken);

        var indexes = await ReadAsync(connectionString, """
            SELECT i.name, i.is_unique, i.type_desc,
                   STUFF((SELECT N', ' + QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END
                          FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N''),
                   STUFF((SELECT N', ' + QUOTENAME(c.name)
                          FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                          ORDER BY ic.index_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N''),
                   i.filter_definition
            FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID(@name) AND i.name IS NOT NULL
              AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.type IN (1, 2)
            ORDER BY i.name;
            """, name, r => $"CREATE {(r.GetBoolean(1) ? "UNIQUE " : string.Empty)}{r.GetString(2)} INDEX {SqlGenerator.Quote(r.GetString(0))}\n"
                          + $"    ON {name} ({(r.IsDBNull(3) ? string.Empty : r.GetString(3))})"
                          + (r.IsDBNull(4) ? string.Empty : $"\n    INCLUDE ({r.GetString(4)})")
                          + (r.IsDBNull(5) ? string.Empty : $"\n    WHERE {r.GetString(5)}")
                          + ";", cancellationToken);

        var body = columns.Concat(keys).Select(line => "    " + line);
        var ddl = new StringBuilder($"CREATE TABLE {name}\n(\n{string.Join(",\n", body)}\n);\n");
        foreach (var statement in foreignKeys.Concat(indexes))
            ddl.Append('\n').Append(statement).Append('\n');
        return ddl.ToString();
    }

    public async Task<DbObjectPermissions> GetPermissionsAsync(string connectionString, DbObjectInfo item, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(connectionString, """
            SELECT ISNULL(HAS_PERMS_BY_NAME(@name, 'OBJECT', 'SELECT'), 0),
                   ISNULL(HAS_PERMS_BY_NAME(@name, 'OBJECT', 'VIEW DEFINITION'), 0);
            """, SqlGenerator.FullName(item.Schema, item.Name),
            r => new DbObjectPermissions(r.GetInt32(0) == 1, r.GetInt32(1) == 1), cancellationToken);
        return rows.FirstOrDefault() ?? new DbObjectPermissions(false, false);
    }

    public async Task<DbObjectInfo?> FindObjectAsync(string connectionString, string? schema, string name, CancellationToken cancellationToken)
    {
        var found = await ReadAsync(connectionString, """
            SELECT TOP (1) s.name, o.name, o.type
            FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.name = @name AND (@schema IS NULL OR s.name = @schema)
              AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'SN', 'SO', 'TR')
            ORDER BY CASE WHEN s.name = SCHEMA_NAME() THEN 0 WHEN s.name = 'dbo' THEN 1 ELSE 2 END;
            """, name, r => (Schema: r.GetString(0), Name: r.GetString(1), Type: r.GetString(2).Trim()), cancellationToken,
            ("@schema", (object?)schema ?? DBNull.Value));
        if (found.Count == 0)
            return null;

        var (foundSchema, foundName, type) = found[0];
        var kind = type switch
        {
            "U" => DbObjectKind.Table,
            "V" => DbObjectKind.View,
            "P" or "PC" => DbObjectKind.Procedure,
            "SN" => DbObjectKind.Synonym,
            "SO" => DbObjectKind.Sequence,
            "TR" => DbObjectKind.TableTrigger,
            _ => DbObjectKind.Function
        };
        return new DbObjectInfo(kind, foundName, foundSchema);
    }

    private static string Objects(string view, string system) => $"""
        SELECT s.name, o.name, CAST({system} AS int), NULL
        FROM {view} AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        ORDER BY s.name, o.name;
        """;

    private static Func<SqlDataReader, DbObjectInfo> Schemaed(DbObjectKind kind) => r =>
        new DbObjectInfo(kind, r.GetString(1), r.GetString(0), DetailOf(r), r.GetInt32(2) == 1);

    private static Func<SqlDataReader, DbObjectInfo> Plain(DbObjectKind kind) => r =>
        new DbObjectInfo(kind, r.GetString(1), null, DetailOf(r), r.GetInt32(2) == 1);

    private static Func<SqlDataReader, DbObjectInfo> Child(DbObjectKind kind) => r =>
        new DbObjectInfo(kind, r.GetString(0), Detail: r.IsDBNull(1) ? null : r.GetString(1));

    private static string? DetailOf(SqlDataReader r) => r.IsDBNull(3) ? null : r.GetValue(3)?.ToString();

    /// <summary>Один каталожный SELECT. Имя объекта — только параметром, не подстановкой в текст.</summary>
    private static async Task<IReadOnlyList<T>> ReadAsync<T>(string connectionString, string sql, string? name,
        Func<SqlDataReader, T> map, CancellationToken cancellationToken, params (string Name, object Value)[] extra)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = TimeoutSeconds };
        if (name is not null)
            command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 776) { Value = name });
        foreach (var (parameterName, value) in extra)
            command.Parameters.Add(new SqlParameter(parameterName, System.Data.SqlDbType.NVarChar, 128) { Value = value });
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(map(reader));
        return result;
    }
}
