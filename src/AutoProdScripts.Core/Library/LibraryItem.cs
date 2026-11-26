namespace AutoProdScripts.Core.Library;

/// <summary>Общее для скриптов и цепочек: то, по чему их ищут и группируют.</summary>
public abstract class LibraryItem
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Путь через «/», например «Заявки/Больничные». Пусто — без группы.</summary>
    public string Group { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }

    /// <summary>false — в файле не хватает обязательных частей.</summary>
    internal virtual bool IsComplete => Id != Guid.Empty && Name is not null && Description is not null
                                        && Group is not null && Tags is not null;
}
