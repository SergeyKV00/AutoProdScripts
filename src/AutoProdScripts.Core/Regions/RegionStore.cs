using System.Text.Json;

namespace AutoProdScripts.Core.Regions;

/// <param name="Errors">Номера строк и что в них не так.</param>
public sealed record RegionListResult(IReadOnlyList<Region> Regions, IReadOnlyList<string> Errors);

/// <summary>Справочник регионов в regions.json.</summary>
public sealed class RegionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private List<Region> _items = new();

    public RegionStore(string directory)
    {
        _path = Path.Combine(directory, "regions.json");
        Load();
    }

    public event Action? Changed;

    public string? LoadError { get; private set; }

    public IReadOnlyList<Region> GetAll() => _items.Select(r => r.Clone()).ToList();

    /// <summary>Заменяет весь справочник.</summary>
    public void Save(IReadOnlyList<Region> regions)
    {
        var copy = regions.Select(r => r.Clone()).ToList();
        foreach (var region in copy)
        {
            region.Code = region.Code.Trim();
            region.Name = region.Name.Trim();
            if (!RegionDatabase.IsValidCode(region.Code))
                throw new InvalidOperationException(
                    $"Код региона «{region.Code}»: только латинские буквы, цифры и «_».");
            if (region.ConnectionName.Length == 0)
                throw new InvalidOperationException($"Регион «{region.Code}»: не выбран кластер.");
        }

        var duplicate = copy.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Код региона «{duplicate.Key}» повторяется.");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, Json));
        File.Move(temp, _path, overwrite: true);
        _items = copy;
        Changed?.Invoke();
    }

    /// <summary>
    /// Два вида списка, их можно смешивать:
    /// «код; кластер» или «код; название; кластер» по региону в строке (разделитель — «;», запятая, табуляция);
    /// строка «Кластер:» и под ней коды (можно с маркерами списка и «код; название»).
    /// </summary>
    public static RegionListResult ParseList(string text)
    {
        var regions = new List<Region>();
        var errors = new List<string>();
        var lines = text.Split('\n');
        string? cluster = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('•', '·', '-', '*', '–').Trim();
            if (line.Length == 0)
                continue;

            if (line.EndsWith(':'))
            {
                cluster = line.TrimEnd(':').Trim();
                continue;
            }

            var parts = line.Split([';', ',', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            // Под заголовком кластер уже известен: «код» или «код; название».
            var grouped = !string.IsNullOrEmpty(cluster);
            var valid = grouped ? parts.Length is 1 or 2 : parts.Length is 2 or 3;
            if (!valid || !RegionDatabase.IsValidCode(parts[0]))
            {
                errors.Add($"Строка {i + 1}: ожидается «код; кластер» или код под строкой «Кластер:».");
                continue;
            }

            regions.Add(new Region
            {
                Code = parts[0],
                Name = parts.Length == (grouped ? 2 : 3) ? parts[1] : string.Empty,
                ConnectionName = grouped ? cluster! : parts[^1]
            });
        }

        return new RegionListResult(regions, errors);
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            _items = (JsonSerializer.Deserialize<List<Region>>(File.ReadAllText(_path), Json) ?? new())
                .Where(r => r is { Code: not null, Name: not null, ConnectionName: not null })
                .ToList();
        }
        catch (JsonException)
        {
            var backup = $"{_path}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, backup);
            LoadError = $"Файл регионов повреждён, сохранён как {backup}.";
        }
    }
}
