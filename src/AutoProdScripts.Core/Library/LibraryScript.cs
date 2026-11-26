using AutoProdScripts.Core.Execution;

namespace AutoProdScripts.Core.Library;

public sealed class ScriptParameter
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SqlParameterKind Kind { get; set; } = SqlParameterKind.NVarChar;

    /// <summary>Задаётся только явно в свойствах скрипта; введённые при запуске значения сюда не попадают.</summary>
    public string? DefaultValue { get; set; }

    public ScriptParameter Clone() => (ScriptParameter)MemberwiseClone();
}

/// <summary>Один скрипт библиотеки — один файл. Личные отметки (избранное, запуски) хранятся отдельно.</summary>
public sealed class LibraryScript
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Путь через «/», например «Заявки/Больничные». Пусто — без группы.</summary>
    public string Group { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    /// <summary>Подключение ищется по Id, а после переноса на другой ПК — по имени.</summary>
    public Guid? ConnectionId { get; set; }

    public string ConnectionName { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public List<ScriptParameter> Parameters { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }

    public LibraryScript Clone()
    {
        var copy = (LibraryScript)MemberwiseClone();
        copy.Tags = Tags.ToList();
        copy.Parameters = Parameters.Select(p => p.Clone()).ToList();
        return copy;
    }
}
