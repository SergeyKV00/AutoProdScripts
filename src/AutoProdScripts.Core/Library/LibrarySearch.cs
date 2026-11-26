namespace AutoProdScripts.Core.Library;

/// <summary>
/// Скрипт или цепочка вместе с тем, куда подключается на этом ПК. <paramref name="Body"/> — текст SQL,
/// <paramref name="Labels"/> — подписи параметров и прочие названия.
/// </summary>
public sealed record LibraryEntry(
    LibraryItem Item,
    IReadOnlyList<string> Connections,
    IReadOnlyList<string> Databases,
    string Body,
    string Labels = "");

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
                        || e.Connections.Contains(connectionName, StringComparer.CurrentCultureIgnoreCase))
            .Where(e => tag is null || e.Item.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
            .Select(e => (Entry: e, Rank: Rank(e, words)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Entry.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Entry)
            .ToList();
    }

    private static int Rank(LibraryEntry entry, string[] words)
    {
        if (words.Length == 0)
            return 0;

        var item = entry.Item;
        var name = Normalize(item.Name);
        if (words.All(name.Contains))
            return 0;

        var properties = Normalize(string.Join('\n',
            item.Name, item.Description, item.Group, string.Join('\n', item.Tags),
            string.Join('\n', entry.Connections), string.Join('\n', entry.Databases), entry.Labels));
        if (words.All(properties.Contains))
            return 1;

        var everything = properties + "\n" + Normalize(entry.Body);
        return words.All(everything.Contains) ? 2 : -1;
    }

    private static string Normalize(string text) => text.ToLowerInvariant().Replace('ё', 'е');
}
