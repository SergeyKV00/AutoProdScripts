using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoProdScripts.Core.Library;

/// <summary>Локальная библиотека: scripts\{id}.json и state.json с избранным и датами запуска.</summary>
public sealed class ScriptLibrary
{
    private static readonly JsonSerializerOptions Json = new()
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
    private readonly string _statePath;
    private readonly Dictionary<Guid, LibraryScript> _scripts = new();
    private readonly List<string> _loadErrors = new();
    private State _state = new();

    public ScriptLibrary(string directory)
    {
        _scriptsDirectory = Path.Combine(directory, "scripts");
        _statePath = Path.Combine(directory, "state.json");
        Load();
    }

    public event Action? Changed;

    /// <summary>Файлы, которые не удалось прочитать. Они остаются на диске нетронутыми.</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public IReadOnlyList<LibraryScript> GetAll() =>
        _scripts.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => s.Clone()).ToList();

    public LibraryScript? Find(Guid id) => _scripts.GetValueOrDefault(id)?.Clone();

    public IReadOnlyList<string> Groups() => _scripts.Values
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

    public string MakeUniqueName(string name, string group, Guid? ignoreId = null)
    {
        var baseName = name.Trim();
        var candidate = baseName;
        for (var i = 2; IsNameTaken(candidate, NormalizeGroup(group), ignoreId); i++)
            candidate = $"{baseName} ({i})";
        return candidate;
    }

    /// <returns>Сохранённая копия.</returns>
    public LibraryScript Save(LibraryScript script)
    {
        var copy = script.Clone();
        copy.Version = LibraryScript.CurrentVersion;
        copy.Name = copy.Name.Trim();
        copy.Group = NormalizeGroup(copy.Group);
        copy.Description = copy.Description.Trim();
        copy.Tags = ParseTags(string.Join(',', copy.Tags));
        copy.UpdatedUtc = DateTime.UtcNow;

        if (copy.Name.Length == 0)
            throw new InvalidOperationException("Укажите название скрипта.");
        if (IsNameTaken(copy.Name, copy.Group, copy.Id))
            throw new InvalidOperationException($"В этой группе уже есть скрипт «{copy.Name}».");

        Write(PathFor(copy.Id), copy);
        _scripts[copy.Id] = copy;
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

    public void Delete(Guid id)
    {
        File.Delete(PathFor(id));
        _scripts.Remove(id);
        _state.Favorites.Remove(id);
        _state.LastRun.Remove(id);
        Write(_statePath, _state);
        Changed?.Invoke();
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
        if (!_scripts.ContainsKey(id))
            return;
        _state.LastRun[id] = DateTime.UtcNow;
        Write(_statePath, _state);
        Changed?.Invoke();
    }

    private bool IsNameTaken(string name, string group, Guid? ignoreId) => _scripts.Values.Any(s =>
        s.Id != ignoreId
        && string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase)
        && string.Equals(s.Group, group, StringComparison.CurrentCultureIgnoreCase));

    private string PathFor(Guid id) => Path.Combine(_scriptsDirectory, $"{id:N}.json");

    private void Load()
    {
        if (Directory.Exists(_scriptsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_scriptsDirectory, "*.json"))
            {
                try
                {
                    var script = JsonSerializer.Deserialize<LibraryScript>(File.ReadAllText(path), Json);
                    if (script is null || script.Id == Guid.Empty || script.Name is null || script.Sql is null
                        || script.Group is null || script.Tags is null || script.Parameters is null
                        || script.Description is null || script.ConnectionName is null)
                        _loadErrors.Add($"Не удалось прочитать {path}.");
                    else if (script.Version > LibraryScript.CurrentVersion)
                        _loadErrors.Add($"Скрипт {path} создан более новой версией приложения.");
                    else if (!_scripts.TryAdd(script.Id, script))
                        _loadErrors.Add($"Повтор скрипта в {path}.");
                    // Сохраняем всегда в файл по Id, поэтому переименованный вручную файл переименовываем обратно.
                    else if (!string.Equals(path, PathFor(script.Id), StringComparison.OrdinalIgnoreCase)
                             && !File.Exists(PathFor(script.Id)))
                        File.Move(path, PathFor(script.Id));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _loadErrors.Add($"Не удалось прочитать {path}.");
                }
            }
        }

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
