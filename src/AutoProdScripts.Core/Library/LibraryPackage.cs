using System.Text.Json;
using System.Text.RegularExpressions;
using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Core.Library;

/// <summary>
/// Файл обмена с коллегами. Паролей, адресов серверов, значений параметров (в том числе по умолчанию),
/// локальных Id подключений и личных отметок в нём нет.
/// </summary>
public sealed class LibraryPackage
{
    public const string FormatName = "AutoProdScripts.Library";
    public const int CurrentVersion = 1;
    public const string Extension = ".apsq";

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public List<LibraryScript> Scripts { get; set; } = new();
    public List<LibraryChain> Chains { get; set; } = new();

    public IEnumerable<LibraryItem> Items => Scripts.Cast<LibraryItem>().Concat(Chains);

    /// <summary>Имена подключений, на которые ссылается содержимое.</summary>
    public IReadOnlyList<string> ConnectionNames() => Scripts.Select(s => s.ConnectionName)
        .Concat(Chains.SelectMany(c => c.Steps).Select(s => s.ConnectionName))
        .Concat(Chains.SelectMany(c => c.Steps).Select(s => s.Inline?.ConnectionName ?? string.Empty))
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}

public sealed record ExportResult(LibraryPackage Package, IReadOnlyList<string> Warnings);

public enum ImportState
{
    New,
    Identical,
    Different
}

public enum ImportAction
{
    Add,
    Update,
    Copy,
    Skip
}

/// <param name="Problem">Замечание: скрипт не пройдёт проверку «только чтение» и не запустится.</param>
public sealed record ImportCandidate(LibraryItem Item, ImportState State, string? Problem);

public sealed record ImportResult(int Added, int Updated, int Skipped, IReadOnlyList<string> Errors);

/// <summary>Подключение на этом ПК, выбранное для имени из файла.</summary>
public sealed record MappedConnection(Guid? Id, string Name);

public static partial class LibraryExchange
{
    private const int MaxFileLength = 20_000_000;

    // 12 цифр подряд — так выглядит ИИН или БИН, вписанный прямо в текст.
    [GeneratedRegex(@"(?<!\d)\d{12}(?!\d)")]
    private static partial Regex LongNumber();

    /// <summary>Выбранные записи вместе со скриптами, на которые ссылаются цепочки.</summary>
    public static ExportResult Export(ScriptLibrary library, IEnumerable<Guid> ids)
    {
        var package = new LibraryPackage();
        var warnings = new List<string>();
        var scripts = new Dictionary<Guid, LibraryScript>();

        foreach (var id in ids.Distinct())
        {
            if (library.Find(id) is { } script)
            {
                scripts[id] = script;
                continue;
            }

            if (library.FindChain(id) is not { } chain)
                continue;

            package.Chains.Add(chain);
            foreach (var step in chain.Steps.Where(s => s.Inline is null))
            {
                if (library.Find(step.ScriptId) is { } referenced)
                    scripts[referenced.Id] = referenced;
                else
                    warnings.Add($"В цепочке «{chain.Name}» есть шаг без скрипта.");
            }
        }

        package.Scripts.AddRange(scripts.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));
        Strip(package);

        var texts = package.Scripts.Select(s => (s.Name, Text: s.Sql + "\n" + s.Description))
            .Concat(package.Chains.Select(c => (c.Name, Text: string.Join('\n',
                c.Steps.Select(s => s.Inline?.Sql + "\n" + s.EmptyMessage).Append(c.Description)))));
        var suspicious = texts.Where(t => LongNumber().IsMatch(t.Text)).Select(t => $"«{t.Name}»").ToList();
        if (suspicious.Count > 0)
            warnings.Add($"Похоже на ИИН или БИН в тексте: {string.Join(", ", suspicious)}. Проверьте перед отправкой.");

        return new ExportResult(package, warnings);
    }

    public static string Serialize(LibraryPackage package) => JsonSerializer.Serialize(package, ScriptLibrary.Json);

    /// <summary>Файл от другого человека: всё проверяется, лишнее отбрасывается.</summary>
    /// <exception cref="InvalidDataException">Это не файл обмена или он повреждён.</exception>
    public static LibraryPackage Parse(string json)
    {
        if (json.Length > MaxFileLength)
            throw new InvalidDataException("Файл слишком большой.");

        LibraryPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<LibraryPackage>(json, ScriptLibrary.Json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Файл повреждён или это не файл обмена AutoProdScripts.");
        }

        if (package is null || package.Format != LibraryPackage.FormatName || !json.Contains(LibraryPackage.FormatName))
            throw new InvalidDataException("Это не файл обмена AutoProdScripts.");
        if (package.Version > LibraryPackage.CurrentVersion
            || package.Scripts?.Any(s => s?.Version > LibraryItem.CurrentVersion) == true
            || package.Chains?.Any(c => c?.Version > LibraryItem.CurrentVersion) == true)
            throw new InvalidDataException("Файл создан более новой версией приложения. Обновите приложение.");
        if (package.Scripts is null || package.Chains is null
            || package.Scripts.Any(s => s is not { IsComplete: true } || s.Parameters.Any(p => p?.Name is null || p.Label is null)
                                        || s.Tags.Any(t => t is null))
            || package.Chains.Any(c => c is not { IsComplete: true } || c.Parameters.Any(p => p?.Name is null || p.Label is null)
                                       || c.Tags.Any(t => t is null)
                                       || c.Steps.Any(s => s.Inline is { } own && (own.Tags.Any(t => t is null)
                                           || own.Parameters.Any(p => p?.Name is null || p.Label is null)))
                                       || c.Steps.Any(s => s.Bindings.Any(b => b?.Parameter is null || b.Source is null))))
            throw new InvalidDataException("Файл повреждён: в нём не хватает обязательных данных.");
        if (package.Items.Select(i => i.Id).Distinct().Count() != package.Items.Count())
            throw new InvalidDataException("Файл повреждён: в нём повторяются записи.");

        Strip(package);
        return package;
    }

    public static IReadOnlyList<ImportCandidate> Preview(LibraryPackage package, ScriptLibrary library)
    {
        var changedScripts = package.Scripts
            .Where(s => library.Find(s.Id) is { } mine && Fingerprint(mine) != Fingerprint(s))
            .Select(s => s.Id)
            .ToHashSet();

        return package.Items.Select(item =>
        {
            LibraryItem? local = item is LibraryScript ? library.Find(item.Id) : library.FindChain(item.Id);
            // Цепочка с изменившимся скриптом тоже «отличается»: иначе она молча осталась бы на старом.
            var usesChanged = item is LibraryChain linked
                              && linked.Steps.Any(s => s.Inline is null && changedScripts.Contains(s.ScriptId));
            var state = local is null ? ImportState.New
                : !usesChanged && Fingerprint(local) == Fingerprint(item) ? ImportState.Identical
                : ImportState.Different;
            var sql = item switch
            {
                LibraryScript script => [script.Sql],
                LibraryChain chain => chain.Steps.Select(s => s.Inline?.Sql).OfType<string>().ToList(),
                _ => new List<string>()
            };
            var rejected = sql.Any(text => !ReadOnlyValidator.Analyze(text).IsAllowed);
            return new ImportCandidate(item, state, rejected ? "не проходит проверку «только чтение»" : null);
        }).ToList();
    }

    public static ImportResult Import(
        LibraryPackage package,
        ScriptLibrary library,
        IReadOnlyDictionary<Guid, ImportAction> actions,
        Func<string, MappedConnection> mapConnection)
    {
        int added = 0, updated = 0, skipped = 0;
        var errors = new List<string>();
        var copiedScripts = new Dictionary<Guid, Guid>();

        ImportAction ActionFor(LibraryItem item) => actions.GetValueOrDefault(item.Id, ImportAction.Skip);

        foreach (var source in package.Scripts)
        {
            var action = ActionFor(source);
            if (action == ImportAction.Skip)
            {
                skipped++;
                continue;
            }

            var script = source.Clone();
            var connection = mapConnection(script.ConnectionName);
            (script.ConnectionId, script.ConnectionName) = (connection.Id, connection.Name);
            var local = library.Find(script.Id);
            if (action == ImportAction.Copy || action == ImportAction.Add && local is not null)
            {
                script.Id = Guid.NewGuid();
                local = null;
            }

            // Свои значения по умолчанию при обновлении не теряются: в файле их нет по определению.
            if (local is not null)
                KeepDefaults(script.Parameters, local.Parameters);
            script.Name = library.MakeUniqueName(script.Name, script.Group, script.Id);

            if (Try(source, () => library.Save(script)))
            {
                if (script.Id != source.Id)
                    copiedScripts[source.Id] = script.Id;
                _ = local is null ? added++ : updated++;
            }
        }

        foreach (var source in package.Chains)
        {
            var action = ActionFor(source);
            if (action == ImportAction.Skip)
            {
                skipped++;
                continue;
            }

            var chain = source.Clone();
            foreach (var step in chain.Steps)
            {
                if (copiedScripts.TryGetValue(step.ScriptId, out var copy))
                    step.ScriptId = copy;
                var connection = mapConnection(step.ConnectionName);
                (step.ConnectionId, step.ConnectionName) = (connection.Id, connection.Name);
                if (step.Inline is { } inline)
                {
                    var own = mapConnection(inline.ConnectionName);
                    (inline.ConnectionId, inline.ConnectionName) = (own.Id, own.Name);
                }
            }

            var local = library.FindChain(chain.Id);
            if (action == ImportAction.Copy || action == ImportAction.Add && local is not null)
            {
                chain.Id = Guid.NewGuid();
                local = null;
            }

            if (local is not null)
                KeepDefaults(chain.Parameters, local.Parameters);
            chain.Name = library.MakeUniqueName(chain.Name, chain.Group, chain.Id, chain: true);

            if (Try(source, () => library.SaveChain(chain)))
                _ = local is null ? added++ : updated++;
        }

        return new ImportResult(added, updated, skipped, errors);

        bool Try(LibraryItem item, Action save)
        {
            try
            {
                save();
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"«{item.Name}»: {ex.Message}");
                return false;
            }
        }
    }

    private static void KeepDefaults(List<ScriptParameter> incoming, List<ScriptParameter> local)
    {
        foreach (var parameter in incoming)
        {
            parameter.DefaultValue = local
                .FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
                ?.DefaultValue;
        }
    }

    /// <summary>Убирает всё личное и относящееся только к этому ПК.</summary>
    private static void Strip(LibraryPackage package)
    {
        foreach (var script in package.Scripts)
            Strip(script);

        foreach (var chain in package.Chains)
        {
            chain.Parameters.ForEach(p => p.DefaultValue = null);
            foreach (var step in chain.Steps)
            {
                step.ConnectionId = null;
                if (step.Inline is not null)
                    Strip(step.Inline);
            }
        }
    }

    private static void Strip(LibraryScript script)
    {
        script.ConnectionId = null;
        script.Parameters.ForEach(p => p.DefaultValue = null);
    }

    // Подключения не сравниваем: на каждом ПК они называются по-своему после сопоставления.
    private static string Fingerprint(LibraryItem item)
    {
        var package = new LibraryPackage();
        if (item is LibraryScript script)
            package.Scripts.Add(script.Clone());
        else
            package.Chains.Add(((LibraryChain)item).Clone());
        Strip(package);

        foreach (var copy in package.Items)
            copy.UpdatedUtc = default;
        package.Scripts.ForEach(s => s.ConnectionName = string.Empty);
        foreach (var step in package.Chains.SelectMany(c => c.Steps))
        {
            step.ConnectionName = string.Empty;
            if (step.Inline is { } inline)
            {
                inline.ConnectionName = string.Empty;
                inline.Id = Guid.Empty;
                inline.UpdatedUtc = default;
            }
        }

        return Serialize(package);
    }
}
