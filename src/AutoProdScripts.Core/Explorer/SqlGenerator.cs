using System.Text;

namespace AutoProdScripts.Core.Explorer;

/// <summary>
/// Текст SQL для объектов дерева (диалект MS SQL Server). Только генерирует текст: выполнять
/// изменяющие запросы раздел не даст — их не пропустит проверка «только чтение».
/// </summary>
public static class SqlGenerator
{
    public const int PreviewRows = 1000;

    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Полное имя с учётом схемы: [dbo].[Users]. С базой — [Db].[dbo].[Users].</summary>
    public static string FullName(string? schema, string name, string? database = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(database))
            parts.Add(Quote(database));
        if (!string.IsNullOrEmpty(schema))
            parts.Add(Quote(schema));
        else if (parts.Count > 0)
            parts.Add(string.Empty); // [Db]..[name]
        parts.Add(Quote(name));
        return string.Join('.', parts);
    }

    public static string FullName(DbObjectInfo item, string? database = null) =>
        item.Kind is DbObjectKind.Database or DbObjectKind.Schema or DbObjectKind.User or DbObjectKind.Role
            ? Quote(item.Name)
            : FullName(item.Schema, item.Name, database);

    /// <summary>Строка-литерал N'...' для имени объекта.</summary>
    public static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <param name="top">null — без ограничения (для экспорта).</param>
    public static string Select(string? schema, string name, IReadOnlyList<DbColumnInfo> columns, int? top = PreviewRows)
    {
        var list = columns.Count == 0
            ? "*"
            : string.Join(",\n       ", columns.Select(c => Quote(c.Name)));
        var topText = top is { } n ? $"TOP ({n}) " : string.Empty;
        return $"SELECT {topText}{list}\nFROM {FullName(schema, name)};\n";
    }

    /// <summary>INSERT с параметрами @Столбец (без identity и вычисляемых).</summary>
    public static string Insert(string? schema, string name, IReadOnlyList<DbColumnInfo> columns)
    {
        var writable = columns.Where(c => !c.IsIdentity && !c.IsComputed).ToList();
        if (writable.Count == 0)
            return $"INSERT INTO {FullName(schema, name)} DEFAULT VALUES;\n";
        return $"INSERT INTO {FullName(schema, name)}\n"
               + $"       ({string.Join(", ", writable.Select(c => Quote(c.Name)))})\n"
               + $"VALUES ({string.Join(", ", writable.Select(c => Variable(c.Name)))});\n";
    }

    public static string Update(string? schema, string name, IReadOnlyList<DbColumnInfo> columns)
    {
        var keys = KeyColumns(columns);
        var set = columns.Where(c => !c.IsIdentity && !c.IsComputed && !c.IsKey).ToList();
        if (set.Count == 0)
            set = columns.Where(c => !c.IsIdentity && !c.IsComputed).ToList();
        var assignments = set.Count == 0
            ? "    /* нет изменяемых столбцов */"
            : string.Join(",\n", set.Select(c => $"    {Quote(c.Name)} = {Variable(c.Name)}"));
        return $"UPDATE {FullName(schema, name)}\nSET\n{assignments}\n{Where(keys)};\n";
    }

    public static string Delete(string? schema, string name, IReadOnlyList<DbColumnInfo> columns) =>
        $"DELETE FROM {FullName(schema, name)}\n{Where(KeyColumns(columns))};\n";

    /// <summary>EXEC процедуры с именованными параметрами.</summary>
    public static string Execute(string? schema, string name, IReadOnlyList<DbColumnInfo> parameters)
    {
        var builder = new StringBuilder($"EXEC {FullName(schema, name)}");
        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            var parameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            builder.Append(i == 0 ? "\n    " : ",\n    ")
                .Append(parameterName).Append(" = ").Append(parameterName)
                .Append(p.IsOutput ? " OUTPUT" : string.Empty);
        }

        return builder.Append(";\n").ToString();
    }

    /// <summary>Вызов функции: табличная — SELECT * FROM, скалярная — SELECT f(...).</summary>
    public static string CallFunction(string? schema, string name, IReadOnlyList<DbColumnInfo> parameters, bool tableValued)
    {
        var args = string.Join(", ", parameters.Select(p => p.Name.StartsWith('@') ? p.Name : "@" + p.Name));
        return tableValued
            ? $"SELECT TOP ({PreviewRows}) *\nFROM {FullName(schema, name)}({args});\n"
            : $"SELECT {FullName(schema, name)}({args});\n";
    }

    /// <summary>Запрос «структура объекта»: столбцы с типами, только чтение каталога.</summary>
    public static string Structure(string? schema, string name) => $"""
        SELECT c.column_id AS [№],
               c.name AS [Столбец],
               TYPE_NAME(c.user_type_id) AS [Тип],
               CASE WHEN c.max_length = -1 THEN 'max'
                    WHEN TYPE_NAME(c.user_type_id) IN ('nchar', 'nvarchar') THEN CAST(c.max_length / 2 AS varchar(10))
                    ELSE CAST(c.max_length AS varchar(10)) END AS [Длина],
               c.precision AS [Точность],
               c.scale AS [Масштаб],
               c.is_nullable AS [NULL],
               c.is_identity AS [Identity],
               c.is_computed AS [Вычисляемый],
               OBJECT_DEFINITION(c.default_object_id) AS [По умолчанию]
        FROM sys.columns AS c
        WHERE c.object_id = OBJECT_ID({Literal(FullName(schema, name))})
        ORDER BY c.column_id;

        """;

    private static List<DbColumnInfo> KeyColumns(IReadOnlyList<DbColumnInfo> columns)
    {
        var keys = columns.Where(c => c.IsKey).ToList();
        if (keys.Count == 0)
            keys = columns.Where(c => c.IsIdentity).ToList();
        return keys;
    }

    private static string Where(IReadOnlyList<DbColumnInfo> keys) => keys.Count == 0
        ? "WHERE /* условие */ 1 = 0"
        : "WHERE " + string.Join("\n  AND ", keys.Select(c => $"{Quote(c.Name)} = {Variable(c.Name)}"));

    /// <summary>@Имя для параметра: всё, кроме букв, цифр и «_», заменяем на «_».</summary>
    public static string Variable(string column)
    {
        var builder = new StringBuilder("@");
        foreach (var ch in column)
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (builder.Length == 1 || char.IsDigit(builder[1]))
            builder.Insert(1, '_');
        return builder.ToString();
    }

    /// <summary>Тип столбца для DDL: nvarchar(50), decimal(18, 2), varchar(max).</summary>
    public static string FormatType(string type, int maxLength, int precision, int scale)
    {
        var lower = type.ToLowerInvariant();
        return lower switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength.ToString())})",
            "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
            "decimal" or "numeric" => $"{type}({precision}, {scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{type}({scale})",
            "float" when precision != 53 => $"{type}({precision})",
            _ => type
        };
    }

    /// <summary>
    /// Разбор имени под курсором: «[Db].dbo.Users» → части без кавычек. Не больше трёх частей
    /// (база, схема, имя); имя сервера отбрасывается.
    /// </summary>
    public static IReadOnlyList<string>? ParseName(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '[')
            {
                var end = i + 1;
                var name = new StringBuilder();
                while (end < text.Length)
                {
                    if (text[end] == ']')
                    {
                        if (end + 1 < text.Length && text[end + 1] == ']')
                        {
                            name.Append(']');
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    name.Append(text[end++]);
                }

                if (end >= text.Length)
                    return null;
                current.Append(name);
                i = end + 1;
            }
            else if (ch == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0)
                    return null;
                current.Append(text, i + 1, end - i - 1);
                i = end + 1;
            }
            else if (ch == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
            }
            else if (char.IsLetterOrDigit(ch) || ch is '_' or '#' or '@' or '$')
            {
                current.Append(ch);
                i++;
            }
            else
            {
                return null;
            }
        }

        parts.Add(current.ToString());
        if (parts.Count > 4 || parts[^1].Length == 0 || parts[^1].StartsWith('@'))
            return null;
        return parts.Count == 4 ? parts.Skip(1).ToList() : parts;
    }
}
