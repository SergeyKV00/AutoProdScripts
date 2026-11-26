using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoProdScripts.Core.Workspace;

/// <summary>
/// Открытая вкладка раздела «Запросы». Это не скрипт библиотеки: вкладка может быть связана
/// со скриптом (<see cref="ScriptId"/>) или быть черновиком, и у неё своё подключение.
/// </summary>
public sealed class OpenTabState
{
    /// <summary>Сохранённый скрипт библиотеки, открытый во вкладке. null — черновик.</summary>
    public Guid? ScriptId { get; set; }

    /// <summary>Цепочка библиотеки. Взаимоисключается со <see cref="ScriptId"/>.</summary>
    public Guid? ChainId { get; set; }

    /// <summary>Название черновика (для связанного скрипта берётся из библиотеки).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Несохранённый текст. null — текст такой же, как в библиотеке.</summary>
    public string? Text { get; set; }

    /// <summary>Подключение вкладки: ищется по Id, а если его нет — по имени.</summary>
    public Guid? ConnectionId { get; set; }

    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>База, выбранная во вкладке для подключения к кластеру. Пусто — база подключения.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>true — запуск по регионам с шаблоном <see cref="RegionDatabase"/>.</summary>
    public bool Regional { get; set; }

    public string RegionDatabase { get; set; } = string.Empty;
    public List<string> Regions { get; set; } = new();
}

public sealed class OpenTabsState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<OpenTabState> Tabs { get; set; } = new();

    /// <summary>Номер активной вкладки в <see cref="Tabs"/>.</summary>
    public int Active { get; set; }
}

/// <summary>
/// Открытые вкладки в open-tabs.json — только на этом ПК, в обмен (*.apsq) не попадают.
/// Значения параметров и результаты сюда не пишутся.
/// </summary>
public sealed class OpenTabsStore
{
    /// <summary>Защита от случайно огромного файла: текст больше этого не восстанавливаем.</summary>
    public const int MaxTextLength = 5_000_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;

    public OpenTabsStore(string directory) => _path = Path.Combine(directory, "open-tabs.json");

    /// <summary>Пустое состояние, если файла нет или он повреждён: вкладки восстановимы, библиотека не затронута.</summary>
    public OpenTabsState Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new OpenTabsState();
            var state = JsonSerializer.Deserialize<OpenTabsState>(File.ReadAllText(_path), Json);
            return Normalize(state);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new OpenTabsState();
        }
    }

    public void Save(OpenTabsState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Normalize(state), Json));
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Отбрасывает неполные и бессмысленные записи, чтобы восстановление не падало.</summary>
    public static OpenTabsState Normalize(OpenTabsState? state)
    {
        if (state is null || state.Version > OpenTabsState.CurrentVersion || state.Tabs is null)
            return new OpenTabsState();

        var tabs = state.Tabs
            .Where(t => t is not null)
            .Select(t => new OpenTabState
            {
                ScriptId = t.ChainId is null ? t.ScriptId : null,
                ChainId = t.ChainId,
                Name = t.Name ?? string.Empty,
                Text = t.ChainId is null && t.Text is { Length: <= MaxTextLength } ? t.Text : null,
                ConnectionId = t.ConnectionId,
                ConnectionName = t.ConnectionName ?? string.Empty,
                Database = t.Database?.Trim() ?? string.Empty,
                Regional = t.Regional,
                RegionDatabase = t.RegionDatabase ?? string.Empty,
                Regions = (t.Regions ?? new()).Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            // Черновик без текста восстанавливать незачем: его содержимое потеряно.
            .Where(t => t.ScriptId is not null || t.ChainId is not null || t.Text is not null)
            .ToList();

        // Одна цепочка за раз: редактор цепочки в разделе один.
        var firstChain = tabs.FindIndex(t => t.ChainId is not null);
        tabs = tabs.Where((t, i) => t.ChainId is null || i == firstChain).ToList();

        return new OpenTabsState
        {
            Tabs = tabs,
            Active = tabs.Count == 0 ? 0 : Math.Clamp(state.Active, 0, tabs.Count - 1)
        };
    }
}
