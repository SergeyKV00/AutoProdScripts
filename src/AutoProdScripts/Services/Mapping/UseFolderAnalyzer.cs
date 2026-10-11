using System.IO;
using AutoProdScripts.Models;

namespace AutoProdScripts.Services.Mapping;

public static class UseFolderAnalyzer
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", ".nuget", "mapping"
    };

    public static UseFolderReport Analyze(string projectRoot)
    {
        var report = new UseFolderReport();
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return report;

        var byFolder = new Dictionary<string, FolderUseGroup>(StringComparer.OrdinalIgnoreCase);
        var byDatabase = new Dictionary<string, DatabaseFolderGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateSql(projectRoot))
        {
            var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            var folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? ".";
            if (string.IsNullOrWhiteSpace(folder))
                folder = ".";
            if (!byFolder.TryGetValue(folder, out var group))
            {
                group = new FolderUseGroup { Folder = folder };
                byFolder[folder] = group;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var database = UseStatementParser.TryExtract(text);
            if (string.IsNullOrWhiteSpace(database))
            {
                group.FilesWithoutUse++;
                continue;
            }

            var hit = group.Uses.FirstOrDefault(item =>
                item.Database.Equals(database, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                hit = new UseHit { Database = database };
                group.Uses.Add(hit);
            }

            hit.FileCount++;
            if (hit.Examples.Count < 3)
                hit.Examples.Add(relative);

            if (!byDatabase.TryGetValue(database, out var places))
            {
                places = new DatabaseFolderGroup { Database = database };
                byDatabase[database] = places;
            }

            if (!places.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                places.Folders.Add(folder);
        }

        report.Folders.AddRange(byFolder.Values);
        report.Databases.AddRange(byDatabase.Values);
        return report;
    }

    private static IEnumerable<string> EnumerateSql(string directory)
    {
        IEnumerable<string> files;
        IEnumerable<string> dirs;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.sql");
            dirs = Directory.EnumerateDirectories(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
            yield return file;

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (Excluded.Contains(name) || name.StartsWith('.'))
                continue;
            foreach (var file in EnumerateSql(dir))
                yield return file;
        }
    }
}
