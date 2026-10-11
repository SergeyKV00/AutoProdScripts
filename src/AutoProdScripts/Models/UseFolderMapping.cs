namespace AutoProdScripts.Models;

public sealed class UseFolderMapping
{
    public const string RelativeFile = "mapping/use-folders.yaml";

    public string FilePath { get; set; } = string.Empty;
    public string OnMissingMapping { get; set; } = "ask";
    public Dictionary<string, string> Mappings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetFolder(string database, out string folder) =>
        Mappings.TryGetValue(database.Trim(), out folder!);
}

public sealed class QuickScriptPlan
{
    public string FileName { get; init; } = string.Empty;
    public string RelativeFolder { get; init; } = string.Empty;
    public string Sql { get; init; } = string.Empty;
}

public sealed class UseFolderReport
{
    public List<FolderUseGroup> Folders { get; } = new();
    public List<DatabaseFolderGroup> Databases { get; } = new();

    public IEnumerable<FolderUseGroup> UnambiguousFolders =>
        Folders.Where(folder => folder.IsUnambiguous && !string.IsNullOrWhiteSpace(folder.DominantUse));
}

public sealed class FolderUseGroup
{
    public string Folder { get; init; } = string.Empty;
    public List<UseHit> Uses { get; } = new();
    public int FilesWithoutUse { get; set; }

    public string? DominantUse =>
        Uses.Count == 1 ? Uses[0].Database : null;

    public bool IsUnambiguous => Uses.Count == 1;
}

public sealed class UseHit
{
    public string Database { get; init; } = string.Empty;
    public int FileCount { get; set; }
    public List<string> Examples { get; } = new();
}

public sealed class DatabaseFolderGroup
{
    public string Database { get; init; } = string.Empty;
    public List<string> Folders { get; } = new();
    public bool IsAmbiguous => Folders.Count > 1;
}
