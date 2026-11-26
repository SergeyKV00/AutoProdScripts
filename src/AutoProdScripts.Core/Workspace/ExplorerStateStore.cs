using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoProdScripts.Core.Explorer;

namespace AutoProdScripts.Core.Workspace;

public enum ExplorerView
{
    Connections = 0,
    Library = 1
}

/// <summary>Состояние левой панели между запусками. Только этот ПК.</summary>
public sealed class ExplorerState
{
    public const int CurrentVersion = 1;
    public const int MaxExpanded = 2000;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>По умолчанию — «Подключения».</summary>
    public ExplorerView View { get; set; } = ExplorerView.Connections;

    public bool OpenEditorsCollapsed { get; set; }
    public bool HideSystemObjects { get; set; } = true;

    /// <summary>Скрытые папки объектов (фильтр по типу).</summary>
    public List<DbObjectKind> HiddenKinds { get; set; } = new();

    /// <summary>Ключи раскрытых узлов дерева.</summary>
    public List<string> Expanded { get; set; } = new();

    /// <summary>Подключения, которые были подключены при выходе: при запуске подключаются снова.</summary>
    public List<Guid> Connected { get; set; } = new();
}

public sealed class ExplorerStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public ExplorerStateStore(string directory) => _path = Path.Combine(directory, "explorer.json");

    /// <summary>Состояние по умолчанию, если файла нет или он повреждён.</summary>
    public ExplorerState Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new ExplorerState();
            return Normalize(JsonSerializer.Deserialize<ExplorerState>(File.ReadAllText(_path), Json));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ExplorerState();
        }
    }

    public void Save(ExplorerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Normalize(state), Json));
        File.Move(temp, _path, overwrite: true);
    }

    public static ExplorerState Normalize(ExplorerState? state)
    {
        if (state is null || state.Version > ExplorerState.CurrentVersion)
            return new ExplorerState();

        return new ExplorerState
        {
            View = Enum.IsDefined(state.View) ? state.View : ExplorerView.Connections,
            OpenEditorsCollapsed = state.OpenEditorsCollapsed,
            HideSystemObjects = state.HideSystemObjects,
            HiddenKinds = (state.HiddenKinds ?? new()).Where(Enum.IsDefined).Distinct().ToList(),
            Expanded = (state.Expanded ?? new()).Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal).Take(ExplorerState.MaxExpanded).ToList(),
            Connected = (state.Connected ?? new()).Where(id => id != Guid.Empty).Distinct().ToList()
        };
    }
}
