using System.IO;
using AutoProdScripts.Models;

namespace AutoProdScripts.Services.Mapping;

public static class UseFolderMappingFile
{
    public static string GetPath() =>
        Path.Combine(AppContext.BaseDirectory, "mapping", "use-folders.yaml");

    public static UseFolderMapping Load(out string? error) => LoadFrom(GetPath(), out error);

    public static UseFolderMapping LoadFrom(string path, out string? error)
    {
        var mapping = new UseFolderMapping { FilePath = path };
        error = null;
        if (!File.Exists(path))
            return mapping;

        var lines = File.ReadAllLines(path);
        var section = string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = StripComment(lines[i]).Trim();
            if (raw.Length == 0)
                continue;

            if (raw.Equals("mappings:", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("defaults:", StringComparison.OrdinalIgnoreCase))
            {
                section = raw[..^1].ToLowerInvariant();
                continue;
            }

            var split = raw.IndexOf(':');
            if (split <= 0)
                return Fail(mapping, $"Строка {i + 1}: ожидалась пара «ключ: значение».", out error);

            var key = Unquote(raw[..split].Trim());
            var value = Unquote(raw[(split + 1)..].Trim());
            if (string.IsNullOrWhiteSpace(key))
                return Fail(mapping, $"Строка {i + 1}: пустой ключ.", out error);

            if (section.Length == 0)
                return Fail(mapping, $"Строка {i + 1}: ключ «{key}» вне секции mappings или defaults.", out error);

            if (section == "mappings")
            {
                if (!seen.Add(key))
                    return Fail(mapping, $"Строка {i + 1}: ключ «{key}» указан дважды.", out error);

                var folderError = ValidateRelativeFolder(value);
                if (folderError is not null)
                    return Fail(mapping, $"Строка {i + 1}: {folderError}", out error);

                mapping.Mappings[key] = value.Replace('\\', '/').Trim('/');
            }
            else if (section == "defaults" && key.Equals("onMissingMapping", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not ("ask" or "skip" or "createFolder"))
                    return Fail(mapping, $"Строка {i + 1}: onMissingMapping должен быть ask, skip или createFolder.", out error);

                mapping.OnMissingMapping = value;
            }
        }

        return mapping;
    }

    private static UseFolderMapping Fail(UseFolderMapping mapping, string message, out string error)
    {
        mapping.Mappings.Clear();
        mapping.OnMissingMapping = "ask";
        error = message;
        return mapping;
    }

    public static void Save(UseFolderMapping mapping)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(mapping.FilePath)!);
        var lines = new List<string>
        {
            "# use (имя БД из USE) -> относительный путь папки от корня репозитория",
            "mappings:"
        };
        foreach (var pair in mapping.Mappings.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"  {QuoteIfNeeded(pair.Key)}: {QuoteIfNeeded(pair.Value)}");
        lines.Add("defaults:");
        lines.Add($"  onMissingMapping: {mapping.OnMissingMapping}");
        File.WriteAllLines(mapping.FilePath, lines);
    }

    public static int MergeUnambiguous(UseFolderMapping mapping, UseFolderReport report, out IReadOnlyList<string> skipped)
    {
        var ambiguousUses = report.Databases
            .Where(item => item.IsAmbiguous)
            .Select(item => item.Database)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var added = 0;
        foreach (var folder in report.UnambiguousFolders)
        {
            var database = folder.DominantUse!;
            if (ambiguousUses.Contains(database))
            {
                notes.Add($"{database}: встречается в нескольких папках, в файл не записан.");
                continue;
            }

            if (mapping.Mappings.TryGetValue(database, out var existing))
            {
                if (!existing.Equals(folder.Folder, StringComparison.OrdinalIgnoreCase))
                    notes.Add($"{database}: уже сопоставлен с «{existing}», папка «{folder.Folder}» не перезаписана.");
                continue;
            }

            mapping.Mappings[database] = folder.Folder;
            added++;
        }

        if (added > 0)
            Save(mapping);
        skipped = notes;
        return added;
    }

    public static string FormatReport(UseFolderReport report)
    {
        var lines = new List<string>();
        if (report.Folders.Count == 0)
        {
            lines.Add("В проекте не найдено .sql с оператором USE.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add("Папки");
        lines.Add("");
        foreach (var folder in report.Folders.OrderBy(item => item.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var status = folder.IsUnambiguous ? "однозначное соответствие" : "конфликт / неоднозначность";
            lines.Add($"{folder.Folder} — {status}");
            foreach (var use in folder.Uses.OrderBy(item => item.Database, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"  USE {use.Database}: {use.FileCount}");
                foreach (var example in use.Examples)
                    lines.Add($"    {example}");
            }

            if (folder.FilesWithoutUse > 0)
                lines.Add($"  без USE: {folder.FilesWithoutUse}");
            lines.Add("");
        }

        var shared = report.Databases.Where(item => item.IsAmbiguous).ToList();
        if (shared.Count > 0)
        {
            lines.Add("Один USE в нескольких папках");
            lines.Add("");
            foreach (var item in shared.OrderBy(entry => entry.Database, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"USE {item.Database}");
                foreach (var folder in item.Folders)
                    lines.Add($"  {folder}");
                lines.Add("");
            }
        }

        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    private static string? ValidateRelativeFolder(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "{}")
            return "пустой путь папки.";
        if (Path.IsPathRooted(value) || value.Contains("..", StringComparison.Ordinal))
            return "путь должен быть относительным и без «..».";
        return null;
    }

    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (ch == '"' && !inSingle)
                inDouble = !inDouble;
            else if (ch == '#' && !inSingle && !inDouble)
                return line[..i];
        }

        return line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private static string QuoteIfNeeded(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains(':') || value.Contains('#')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
}
