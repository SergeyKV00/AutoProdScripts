namespace AutoProdScripts.Models;

public sealed record GitResult(bool Success, string StdOut, string StdErr, int ExitCode)
{
    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}".Trim();
}

public sealed record GitBranchInfo(string Name, bool IsRemote, bool IsCurrent);

public sealed record GitStatusEntry(string Status, string Path);

public sealed record PullRequestResult(
    bool Success,
    int? PullRequestId,
    string? Url,
    string Message);
