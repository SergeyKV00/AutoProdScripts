namespace AutoProdScripts.Core.Explorer;

/// <summary>Виды объектов в дереве. Какие из них есть — решает провайдер СУБД.</summary>
public enum DbObjectKind
{
    Database,
    Schema,
    Table,
    View,
    Procedure,
    Function,
    Synonym,
    Sequence,
    Trigger,
    User,
    Role,

    // Вложенные в таблицу или представление.
    Column,
    Key,
    Index,
    Constraint,
    TableTrigger
}

/// <summary>Папка дерева: «Таблицы», «Столбцы» и т.п.</summary>
public sealed record DbFolder(DbObjectKind Kind, string Title);

/// <summary>Объект БД, как его показывает дерево.</summary>
/// <param name="Schema">Схема; null — у объекта её нет (база, схема, пользователь).</param>
/// <param name="Detail">Короткая подпись справа: тип столбца, таблица ключа и т.п.</param>
/// <param name="Parameters">Для процедур и функций: параметры в порядке объявления.</param>
public sealed record DbObjectInfo(
    DbObjectKind Kind,
    string Name,
    string? Schema = null,
    string? Detail = null,
    bool IsSystem = false,
    bool IsAvailable = true)
{
    public IReadOnlyList<DbColumnInfo> Columns { get; init; } = [];

    /// <summary>schema.name без кавычек — для подписи и поиска.</summary>
    public string DisplayName => Schema is null ? Name : $"{Schema}.{Name}";
}

/// <summary>Столбец таблицы или параметр процедуры — то, что нужно генератору SQL.</summary>
public sealed record DbColumnInfo(
    string Name,
    string Type,
    bool IsNullable = true,
    bool IsIdentity = false,
    bool IsComputed = false,
    bool IsKey = false,
    bool IsOutput = false);

/// <summary>Права текущего логина на объект — от них зависит доступность действий в меню.</summary>
public sealed record DbObjectPermissions(bool CanSelect, bool CanViewDefinition);
