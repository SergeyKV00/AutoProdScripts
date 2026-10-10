namespace AutoProdScripts.Models;

public sealed record GitResult(bool Success, string StdOut, string StdErr, int ExitCode)
{
    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}".Trim();
}

public sealed record GitBranchInfo(string Name, bool IsRemote, bool IsCurrent);

public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed
}

public sealed record GitStatusEntry(string Status, string Path, string? OldPath = null);

public static class GitStatusFormat
{
    public static GitChangeKind KindOf(string status)
    {
        var x = status.Length > 0 ? status[0] : ' ';
        var y = status.Length > 1 ? status[1] : ' ';
        if (x == '?' || y == '?')
            return GitChangeKind.Added;
        if (x == 'A')
            return y == 'D' ? GitChangeKind.Deleted : GitChangeKind.Added;
        if (x == 'D' || y == 'D')
            return GitChangeKind.Deleted;
        if (x == 'R' || y == 'R')
            return GitChangeKind.Renamed;
        return GitChangeKind.Modified;
    }

    public static string Label(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added => "добавлен",
        GitChangeKind.Deleted => "удалён",
        GitChangeKind.Renamed => "переименован",
        _ => "изменён"
    };

    public static bool DiscardAsNewFile(string status)
    {
        if (string.IsNullOrEmpty(status))
            return false;
        return status[0] is '?' or 'A';
    }
}

public sealed record PullRequestResult(
    bool Success,
    int? PullRequestId,
    string? Url,
    string Message);
