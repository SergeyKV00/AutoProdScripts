namespace AutoProdScripts.Core.Library;

/// <summary>Скрипт вместе с тем, куда он подключается (имя подключения и база на этом ПК).</summary>
public sealed record LibraryEntry(LibraryScript Script, string ConnectionName, string Database);

public static class LibrarySearch
{
    /// <summary>
    /// Все слова запроса должны найтись. Выше те, где они есть в названии, затем в описании и прочих
    /// свойствах, затем только в тексте SQL.
    /// </summary>
    public static IReadOnlyList<LibraryEntry> Find(
        IEnumerable<LibraryEntry> entries,
        string? text,
        string? connectionName = null,
        string? tag = null)
    {
        var words = Normalize(text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return entries
            .Where(e => connectionName is null
                        || string.Equals(e.ConnectionName, connectionName, StringComparison.CurrentCultureIgnoreCase))
            .Where(e => tag is null || e.Script.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
            .Select(e => (Entry: e, Rank: Rank(e, words)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Entry.Script.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Entry)
            .ToList();
    }

    private static int Rank(LibraryEntry entry, string[] words)
    {
        if (words.Length == 0)
            return 0;

        var script = entry.Script;
        var name = Normalize(script.Name);
        if (words.All(name.Contains))
            return 0;

        var properties = Normalize(string.Join('\n',
            script.Name, script.Description, script.Group, string.Join('\n', script.Tags),
            entry.ConnectionName, entry.Database, string.Join('\n', script.Parameters.Select(p => p.Label))));
        if (words.All(properties.Contains))
            return 1;

        var everything = properties + "\n" + Normalize(script.Sql);
        return words.All(everything.Contains) ? 2 : -1;
    }

    private static string Normalize(string text) => text.ToLowerInvariant().Replace('ё', 'е');
}
