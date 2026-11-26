using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoProdScripts.Core.Library;

/// <summary>Локальная библиотека: scripts\{id}.json, chains\{id}.json и state.json с избранным и датами запуска.</summary>
public sealed class ScriptLibrary
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Кириллица в файле остаётся читаемой.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class State
    {
        public HashSet<Guid> Favorites { get; set; } = new();
        public Dictionary<Guid, DateTime> LastRun { get; set; } = new();
    }

    private readonly string _scriptsDirectory;
    private readonly string _chainsDirectory;
    private readonly string _statePath;
    private readonly Dictionary<Guid, LibraryScript> _scripts = new();
    private readonly Dictionary<Guid, LibraryChain> _chains = new();
    private readonly List<string> _loadErrors = new();
    private State _state = new();

    public ScriptLibrary(string directory)
    {
        _scriptsDirectory = Path.Combine(directory, "scripts");
        _chainsDirectory = Path.Combine(directory, "chains");
        _statePath = Path.Combine(directory, "state.json");
        Load();
    }

    public event Action? Changed;

    /// <summary>Файлы, которые не удалось прочитать. Они остаются на диске нетронутыми.</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public IReadOnlyList<LibraryScript> GetAll() =>
        _scripts.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => s.Clone()).ToList();

    public LibraryScript? Find(Guid id) => _scripts.GetValueOrDefault(id)?.Clone();

    public IReadOnlyList<LibraryChain> GetChains() =>
        _chains.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => c.Clone()).ToList();

    public LibraryChain? FindChain(Guid id) => _chains.GetValueOrDefault(id)?.Clone();

    public IReadOnlyList<LibraryChain> ChainsUsing(Guid scriptId) =>
        GetChains().Where(c => c.Steps.Any(step => step.Inline is null && step.ScriptId == scriptId)).ToList();

    public IReadOnlyList<string> Groups() => _scripts.Values.Cast<LibraryItem>().Concat(_chains.Values)
        .Select(s => s.Group)
        .Where(g => g.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Order(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public static string NormalizeGroup(string group) => string.Join('/',
        group.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static List<string> ParseTags(string tags) => tags
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Имя уникально внутри группы среди записей того же вида (скрипт или цепочка).</summary>
    public string MakeUniqueName(string name, string group, Guid? ignoreId = null, bool chain = false)
    {
        var baseName = name.Trim();
        var candidate = baseName;
        var items = chain ? _chains.Values.Cast<LibraryItem>() : _scripts.Values;
        for (var i = 2; IsNameTaken(items, candidate, NormalizeGroup(group), ignoreId); i++)
            candidate = $"{baseName} ({i})";
        return candidate;
    }

    /// <returns>Сохранённая копия.</returns>
    public LibraryScript Save(LibraryScript script)
    {
        var copy = script.Clone();
        Prepare(copy, _scripts.Values, "Укажите название скрипта.", "скрипт");
        Write(PathFor(_scriptsDirectory, copy.Id), copy);
        _scripts[copy.Id] = copy;
        Changed?.Invoke();
        return copy.Clone();
    }

    public LibraryChain SaveChain(LibraryChain chain)
    {
        var copy = chain.Clone();
        Prepare(copy, _chains.Values, "Укажите название цепочки.", "цепочка");
        Write(PathFor(_chainsDirectory, copy.Id), copy);
        _chains[copy.Id] = copy;
        Changed?.Invoke();
        return copy.Clone();
    }

    public LibraryScript Duplicate(Guid id)
    {
        var copy = _scripts[id].Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = MakeUniqueName(copy.Name, copy.Group);
        return Save(copy);
    }

    public LibraryChain DuplicateChain(Guid id)
    {
        var copy = _chains[id].Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = MakeUniqueName(copy.Name, copy.Group, chain: true);
        return SaveChain(copy);
    }

    /// <summary>Удаляет скрипт или цепочку.</summary>
    public void Delete(Guid id)
    {
        File.Delete(PathFor(_chains.ContainsKey(id) ? _chainsDirectory : _scriptsDirectory, id));
        _scripts.Remove(id);
        _chains.Remove(id);
        _state.Favorites.Remove(id);
        _state.LastRun.Remove(id);
        Write(_statePath, _state);
        Changed?.Invoke();
    }

    private static void Prepare(LibraryItem item, IEnumerable<LibraryItem> siblings, string noName, string kind)
    {
        item.Version = LibraryItem.CurrentVersion;
        item.Name = item.Name.Trim();
        item.Group = NormalizeGroup(item.Group);
        item.Description = item.Description.Trim();
        item.Tags = ParseTags(string.Join(',', item.Tags));
        item.UpdatedUtc = DateTime.UtcNow;

        if (item.Name.Length == 0)
            throw new InvalidOperationException(noName);
        if (IsNameTaken(siblings, item.Name, item.Group, item.Id))
            throw new InvalidOperationException($"В этой группе уже есть {kind} с названием «{item.Name}».");
    }

    public bool IsFavorite(Guid id) => _state.Favorites.Contains(id);

    public void SetFavorite(Guid id, bool favorite)
    {
        if (favorite ? !_state.Favorites.Add(id) : !_state.Favorites.Remove(id))
            return;
        Write(_statePath, _state);
        Changed?.Invoke();
    }

    public DateTime? LastRun(Guid id) => _state.LastRun.TryGetValue(id, out var when) ? when : null;

    public void MarkRun(Guid id)
    {
        if (!_scripts.ContainsKey(id) && !_chains.ContainsKey(id))
            return;
        _state.LastRun[id] = DateTime.UtcNow;
        Write(_statePath, _state);
        Changed?.Invoke();
    }

    private static bool IsNameTaken(IEnumerable<LibraryItem> items, string name, string group, Guid? ignoreId) =>
        items.Any(s => s.Id != ignoreId
                       && string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase)
                       && string.Equals(s.Group, group, StringComparison.CurrentCultureIgnoreCase));

    private static string PathFor(string directory, Guid id) => Path.Combine(directory, $"{id:N}.json");

    private void LoadItems<T>(string directory, Dictionary<Guid, T> target) where T : LibraryItem
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
                if (item is null || !item.IsComplete)
                    _loadErrors.Add($"Не удалось прочитать {path}.");
                else if (item.Version > LibraryItem.CurrentVersion)
                    _loadErrors.Add($"Файл {path} создан более новой версией приложения.");
                else if (!target.TryAdd(item.Id, item))
                    _loadErrors.Add($"Повтор записи в {path}.");
                // Сохраняем всегда в файл по Id, поэтому переименованный вручную файл переименовываем обратно.
                else if (!string.Equals(path, PathFor(directory, item.Id), StringComparison.OrdinalIgnoreCase)
                         && !File.Exists(PathFor(directory, item.Id)))
                    File.Move(path, PathFor(directory, item.Id));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _loadErrors.Add($"Не удалось прочитать {path}.");
            }
        }
    }

    private void Load()
    {
        LoadItems(_scriptsDirectory, _scripts);
        LoadItems(_chainsDirectory, _chains);

        if (!File.Exists(_statePath))
            return;
        try
        {
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath), Json) ?? new State();
        }
        catch (JsonException)
        {
            // Избранное и даты запуска восстановимы, сами скрипты не затронуты.
            _loadErrors.Add("Список избранного и недавних повреждён и будет создан заново.");
        }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
