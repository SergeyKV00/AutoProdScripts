namespace AutoProdScripts.Core.Library;

/// <summary>
/// Откуда параметр шага берёт значение. Без записи — из общего параметра с тем же именем.
/// </summary>
public sealed class ChainBinding
{
    public string Parameter { get; set; } = string.Empty;

    /// <summary>Id шага, из результата которого берётся значение. null — общий параметр.</summary>
    public Guid? FromStep { get; set; }

    /// <summary>Имя колонки (для шага) или общего параметра.</summary>
    public string Source { get; set; } = string.Empty;

    public ChainBinding Clone() => (ChainBinding)MemberwiseClone();
}

public sealed class ChainStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScriptId { get; set; }

    /// <summary>Скрипт, хранящийся только в этой цепочке. Если задан, <see cref="ScriptId"/> не используется.</summary>
    public LibraryScript? Inline { get; set; }

    /// <summary>Пусто — подключение, указанное в самом скрипте.</summary>
    public Guid? ConnectionId { get; set; }

    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>Не выполнять следующие шаги, если этот не вернул ни одной строки.</summary>
    public bool StopWhenEmpty { get; set; } = true;

    public string EmptyMessage { get; set; } = string.Empty;

    /// <summary>Имя вида #employees, под которым результат будет доступен следующим шагам.</summary>
    public string OutputTable { get; set; } = string.Empty;

    public List<ChainBinding> Bindings { get; set; } = new();

    public ChainStep Clone()
    {
        var copy = (ChainStep)MemberwiseClone();
        copy.Bindings = Bindings.Select(b => b.Clone()).ToList();
        copy.Inline = Inline?.Clone();
        return copy;
    }
}

public sealed class LibraryChain : LibraryItem
{
    /// <summary>Общие параметры цепочки: подписи, типы и значения по умолчанию.</summary>
    public List<ScriptParameter> Parameters { get; set; } = new();

    public List<ChainStep> Steps { get; set; } = new();

    internal override bool IsComplete =>
        base.IsComplete && Parameters is not null && Steps is not null
        && Steps.All(s => s is { ConnectionName: not null, EmptyMessage: not null, OutputTable: not null, Bindings: not null }
                          && s.Inline is null or { IsComplete: true });

    public LibraryChain Clone()
    {
        var copy = (LibraryChain)MemberwiseClone();
        copy.Tags = Tags.ToList();
        copy.Parameters = Parameters.Select(p => p.Clone()).ToList();
        copy.Steps = Steps.Select(s => s.Clone()).ToList();
        return copy;
    }
}
