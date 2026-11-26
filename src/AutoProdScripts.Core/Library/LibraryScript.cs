using AutoProdScripts.Core.Execution;

namespace AutoProdScripts.Core.Library;

public sealed class ScriptParameter
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SqlParameterKind Kind { get; set; } = SqlParameterKind.NVarChar;

    /// <summary>Задаётся только явно в свойствах; введённые при запуске значения сюда не попадают.</summary>
    public string? DefaultValue { get; set; }

    public ScriptParameter Clone() => (ScriptParameter)MemberwiseClone();
}

/// <summary>Один скрипт библиотеки — один файл. Личные отметки (избранное, запуски) хранятся отдельно.</summary>
public sealed class LibraryScript : LibraryItem
{
    /// <summary>Подключение ищется по Id, а после переноса на другой ПК — по имени.</summary>
    public Guid? ConnectionId { get; set; }

    public string ConnectionName { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;

    /// <summary>Шаблон базы вида «KMIS_{region}»: скрипт выполняется по регионам, а не на одном подключении.</summary>
    public string RegionDatabase { get; set; } = string.Empty;
    public List<ScriptParameter> Parameters { get; set; } = new();

    internal override bool IsComplete =>
        base.IsComplete && Sql is not null && ConnectionName is not null && Parameters is not null && RegionDatabase is not null;

    public LibraryScript Clone()
    {
        var copy = (LibraryScript)MemberwiseClone();
        copy.Tags = Tags.ToList();
        copy.Parameters = Parameters.Select(p => p.Clone()).ToList();
        return copy;
    }
}
