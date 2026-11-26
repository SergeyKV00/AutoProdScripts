using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoProdScripts.Core.Connections;

public interface ISecretStore
{
    bool TrySave(string key, string secret);
    string? Load(string key);
    void Delete(string key);
}

/// <summary>Список подключений в connections.json; пароли — отдельно, в <see cref="ISecretStore"/>.</summary>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ISecretStore _secrets;
    private readonly List<DbConnectionProfile> _items = new();

    public ConnectionStore(string directory, ISecretStore secrets)
    {
        _path = Path.Combine(directory, "connections.json");
        _secrets = secrets;
        Load();
    }

    public event Action? Changed;

    /// <summary>Заполняется, если файл подключений оказался повреждён.</summary>
    public string? LoadError { get; private set; }

    public IReadOnlyList<DbConnectionProfile> GetAll() =>
        _items.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => p.Clone()).ToList();

    public static string SecretKey(Guid id) => $"AutoProdScripts:Sql:{id:N}";

    public string? LoadPassword(Guid id) => _secrets.Load(SecretKey(id));

    public string MakeUniqueName(string name, Guid? ignoreId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Подключение" : name.Trim();
        var candidate = baseName;
        for (var i = 2; IsNameTaken(candidate, ignoreId); i++)
            candidate = $"{baseName} ({i})";
        return candidate;
    }

    /// <param name="password">null — пароль не менять.</param>
    public void Save(DbConnectionProfile profile, string? password)
    {
        var copy = profile.Clone();
        copy.Name = copy.Name.Trim();
        copy.Group = NormalizeGroup(copy.Group);
        copy.LegacyEnvironment = null;
        if (copy.Port is <= 0 or > 65535)
            throw new InvalidOperationException("Порт — число от 1 до 65535.");
        if (copy.Name.Length == 0)
            throw new InvalidOperationException("Укажите имя подключения.");
        if (string.IsNullOrWhiteSpace(copy.Server))
            throw new InvalidOperationException("Укажите сервер.");
        if (IsNameTaken(copy.Name, copy.Id))
            throw new InvalidOperationException($"Подключение с именем «{copy.Name}» уже есть.");

        if (copy.AuthMode == DbAuthMode.Windows)
        {
            copy.UserName = string.Empty;
            _secrets.Delete(SecretKey(copy.Id));
        }
        else if (password is not null)
        {
            if (password.Length == 0)
                _secrets.Delete(SecretKey(copy.Id));
            else if (!_secrets.TrySave(SecretKey(copy.Id), password))
                throw new InvalidOperationException("Не удалось сохранить пароль в Windows Credential Manager.");
        }

        _items.RemoveAll(p => p.Id == copy.Id);
        _items.Add(copy);
        Persist();
    }

    public DbConnectionProfile Duplicate(Guid id)
    {
        var source = _items.First(p => p.Id == id);
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = MakeUniqueName(source.Name);
        Save(copy, LoadPassword(id));
        return copy;
    }

    public void Delete(Guid id)
    {
        _items.RemoveAll(p => p.Id == id);
        _secrets.Delete(SecretKey(id));
        Persist();
    }

    private bool IsNameTaken(string name, Guid? ignoreId) =>
        _items.Any(p => p.Id != ignoreId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            _items.AddRange(JsonSerializer.Deserialize<List<DbConnectionProfile>>(File.ReadAllText(_path), Json) ?? new());
            if (MigrateEnvironments(_items))
                TryPersist();
        }
        catch (JsonException)
        {
            // Не теряем повреждённый файл: следующее сохранение иначе перезаписало бы его.
            var backup = $"{_path}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, backup);
            LoadError = $"Файл подключений повреждён, сохранён как {backup}.";
        }
    }

    /// <summary>Группы, в которых есть подключения, по алфавиту.</summary>
    public IReadOnlyList<string> Groups() => _items
        .Select(p => NormalizeGroup(p.Group))
        .Where(g => g.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Order(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public static string NormalizeGroup(string? group) => string.Join('/',
        (group ?? string.Empty).Split(['/', '\\'],StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Переименовать подключение (имя уникально).</summary>
    public void Rename(Guid id, string name)
    {
        var profile = _items.First(p => p.Id == id).Clone();
        profile.Name = name;
        Save(profile, null);
    }

    /// <summary>Перенести подключение в группу; пусто — без группы.</summary>
    public void MoveToGroup(Guid id, string group)
    {
        var profile = _items.First(p => p.Id == id).Clone();
        profile.Group = group;
        Save(profile, null);
    }

    /// <summary>Переименовать группу вместе с вложенными.</summary>
    public void RenameGroup(string group, string newName)
    {
        var from = NormalizeGroup(group);
        var to = NormalizeGroup(newName);
        if (from.Length == 0)
            return;
        var changed = false;
        foreach (var profile in _items)
        {
            var current = NormalizeGroup(profile.Group);
            if (string.Equals(current, from, StringComparison.CurrentCultureIgnoreCase))
                profile.Group = to;
            else if (current.StartsWith(from + "/", StringComparison.CurrentCultureIgnoreCase))
                profile.Group = to.Length == 0 ? current[(from.Length + 1)..] : to + current[from.Length..];
            else
                continue;
            changed = true;
        }

        if (changed)
            Persist();
    }

    /// <summary>Среда старых версий становится группой PROD/TEST/DEV.</summary>
    /// <returns>true — что-то перенесено.</returns>
    public static bool MigrateEnvironments(IEnumerable<DbConnectionProfile> profiles)
    {
        var changed = false;
        foreach (var profile in profiles.Where(p => p.LegacyEnvironment is not null))
        {
            if (string.IsNullOrWhiteSpace(profile.Group))
            {
                profile.Group = profile.LegacyEnvironment!.Trim().ToLowerInvariant() switch
                {
                    "test" or "1" => "TEST",
                    "dev" or "2" => "DEV",
                    _ => "PROD"
                };
            }

            profile.LegacyEnvironment = null;
            changed = true;
        }

        return changed;
    }

    private void TryPersist()
    {
        try
        {
            Persist();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Перенос повторится при следующем запуске.
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_items, Json));
        File.Move(temp, _path, overwrite: true);
        Changed?.Invoke();
    }
}
