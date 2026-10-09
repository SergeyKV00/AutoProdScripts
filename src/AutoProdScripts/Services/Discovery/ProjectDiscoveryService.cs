using System.IO;

namespace AutoProdScripts.Services.Discovery;

public sealed class ProjectDiscoveryService
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", ".nuget"
    };

    public IReadOnlyList<string> DiscoverWorkFolders(string projectRoot, int maxDepth = 3)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return Array.Empty<string>();

        var result = new List<string>();
        Walk(projectRoot, projectRoot, 0, maxDepth, result);
        return result
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<FileSystemNode> BuildTree(string projectRoot, int maxDepth = 4)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return Array.Empty<FileSystemNode>();

        return BuildChildren(projectRoot, 0, maxDepth);
    }

    private static void Walk(string root, string current, int depth, int maxDepth, List<string> result)
    {
        if (depth > maxDepth)
            return;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(current);
        }
        catch
        {
            return;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (ExcludedNames.Contains(name) || name.StartsWith('.'))
                continue;

            var relative = Path.GetRelativePath(root, dir);
            result.Add(relative.Replace('\\', '/'));
            Walk(root, dir, depth + 1, maxDepth, result);
        }
    }

    private static List<FileSystemNode> BuildChildren(string directory, int depth, int maxDepth)
    {
        var nodes = new List<FileSystemNode>();
        if (depth > maxDepth)
            return nodes;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (ExcludedNames.Contains(name) || name.StartsWith('.'))
                    continue;

                nodes.Add(new FileSystemNode
                {
                    Name = name,
                    FullPath = dir,
                    IsDirectory = true,
                    Children = BuildChildren(dir, depth + 1, maxDepth)
                });
            }

            foreach (var file in Directory.EnumerateFiles(directory)
                         .Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                nodes.Add(new FileSystemNode
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false
                });
            }
        }
        catch
        {
            // ignore access errors
        }

        return nodes;
    }
}

public sealed class FileSystemNode
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public List<FileSystemNode> Children { get; init; } = new();
}
