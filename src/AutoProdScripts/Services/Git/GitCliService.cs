using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AutoProdScripts.Models;

namespace AutoProdScripts.Services.Git;

/// <summary>
/// Invokes Git for Windows via the <c>git</c> CLI process (not LibGit2Sharp).
/// </summary>
public sealed class GitCliService
{
    private readonly string _workingDirectory;

    public GitCliService(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public bool IsGitAvailable(out string message)
    {
        var result = Run("version");
        if (result.Success)
        {
            message = result.StdOut.Trim();
            return true;
        }

        message = "Git for Windows не найден в PATH. Установите Git и перезапустите приложение.";
        return false;
    }

    public bool IsRepository(out string message)
    {
        var result = Run("rev-parse", "--is-inside-work-tree");
        if (result.Success && result.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            message = "Репозиторий Git найден.";
            return true;
        }

        message = "В выбранной папке не найден репозиторий Git (.git).";
        return false;
    }

    public async Task<IReadOnlyList<GitBranchInfo>> ListBranchesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "branch", "-a", "--no-color").ConfigureAwait(false);
        var list = new List<GitBranchInfo>();
        if (!result.Success)
            return list;

        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim();
            var isCurrent = line.StartsWith('*');
            if (isCurrent)
                line = line[1..].Trim();

            if (line.Contains("->", StringComparison.Ordinal))
                continue;

            var isRemote = line.StartsWith("remotes/", StringComparison.Ordinal);
            var name = isRemote
                ? Regex.Replace(line, @"^remotes/[^/]+/", string.Empty)
                : line;

            if (list.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !b.IsRemote && isRemote))
                continue;

            var existing = list.FindIndex(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var prev = list[existing];
                list[existing] = prev with { IsCurrent = prev.IsCurrent || isCurrent, IsRemote = prev.IsRemote || isRemote };
            }
            else
            {
                list.Add(new GitBranchInfo(name, isRemote, isCurrent));
            }
        }

        return list
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        return result.Success ? result.StdOut.Trim() : null;
    }

    public async Task<GitResult> FetchAsync(string remote = "origin", CancellationToken ct = default)
        => await RunAsync(ct, "fetch", remote, "--prune").ConfigureAwait(false);

    /// <summary>
    /// Fetch, then fast-forward the checked-out branch so working files match the remote.
    /// </summary>
    public async Task<GitResult> SyncCurrentBranchAsync(string remote = "origin", CancellationToken ct = default)
    {
        var fetch = await FetchAsync(remote, ct).ConfigureAwait(false);
        if (!fetch.Success)
            return fetch;

        var branch = await GetCurrentBranchAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(branch) || branch.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return fetch;

        var remoteBranch = $"{remote}/{branch}";
        var exists = await RunAsync(ct, "rev-parse", "--verify", "--quiet", remoteBranch).ConfigureAwait(false);
        if (!exists.Success)
        {
            return new GitResult(
                true,
                fetch.StdOut,
                "У текущей ветки нет копии на remote. Рабочие файлы не менялись.",
                0);
        }

        var merge = await RunAsync(ct, "merge", "--ff-only", remoteBranch).ConfigureAwait(false);
        if (!merge.Success)
            return merge;

        return new GitResult(
            true,
            JoinOutput(fetch.StdOut, merge.StdOut),
            JoinOutput(fetch.StdErr, merge.StdErr),
            0);
    }

    public async Task<GitResult> CreateBranchAsync(
        string branchName,
        string baseBranch,
        string remote = "origin",
        CancellationToken ct = default)
    {
        var previous = await GetCurrentBranchAsync(ct).ConfigureAwait(false);

        var fetch = await FetchAsync(remote, ct).ConfigureAwait(false);
        if (!fetch.Success)
            return fetch;

        var checkoutBase = await RunAsync(ct, "checkout", baseBranch).ConfigureAwait(false);
        if (!checkoutBase.Success)
            return checkoutBase;

        var remoteBranch = $"{remote}/{baseBranch}";
        var exists = await RunAsync(ct, "rev-parse", "--verify", "--quiet", remoteBranch).ConfigureAwait(false);
        if (exists.Success)
        {
            var ff = await RunAsync(ct, "merge", "--ff-only", remoteBranch).ConfigureAwait(false);
            if (!ff.Success)
            {
                if (!string.IsNullOrWhiteSpace(previous)
                    && !previous.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                    && !previous.Equals(baseBranch, StringComparison.OrdinalIgnoreCase))
                {
                    await RunAsync(ct, "checkout", previous).ConfigureAwait(false);
                }

                return ff;
            }
        }

        return await RunAsync(ct, "checkout", "-b", branchName).ConfigureAwait(false);
    }

    private static string JoinOutput(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return right;
        if (string.IsNullOrWhiteSpace(right))
            return left;
        return left + "\n" + right;
    }

    public async Task<GitResult> StatusPorcelainAsync(CancellationToken ct = default)
        => await RunAsync(ct, "status", "--porcelain").ConfigureAwait(false);

    public async Task<IReadOnlyList<GitStatusEntry>> GetStatusEntriesAsync(CancellationToken ct = default)
    {
        var result = await StatusPorcelainAsync(ct).ConfigureAwait(false);
        var entries = new List<GitStatusEntry>();
        if (!result.Success)
            return entries;

        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4)
                continue;

            var status = line[..2];
            var rest = line[3..];
            if (TrySplitRename(rest, out var oldPath, out var newPath))
                entries.Add(new GitStatusEntry(status, NormalizePath(newPath), NormalizePath(oldPath)));
            else
                entries.Add(new GitStatusEntry(status, NormalizePath(Unquote(rest))));
        }

        return entries;
    }

    public async Task<GitResult> RestoreToHeadAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return new GitResult(true, string.Empty, string.Empty, 0);

        var args = new List<string> { "restore", "--source=HEAD", "--staged", "--worktree", "--" };
        args.AddRange(paths);
        return await RunAsync(ct, args.ToArray()).ConfigureAwait(false);
    }

    public async Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return new GitResult(true, string.Empty, string.Empty, 0);

        var args = new List<string> { "reset", "-q", "HEAD", "--" };
        args.AddRange(paths);
        return await RunAsync(ct, args.ToArray()).ConfigureAwait(false);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        return value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static bool TrySplitRename(string rest, out string oldPath, out string newPath)
    {
        oldPath = string.Empty;
        newPath = string.Empty;
        var inQuotes = false;
        for (var i = 0; i < rest.Length - 3; i++)
        {
            if (rest[i] == '"')
                inQuotes = !inQuotes;
            if (inQuotes || !rest.AsSpan(i).StartsWith(" -> "))
                continue;

            oldPath = Unquote(rest[..i]);
            newPath = Unquote(rest[(i + 4)..]);
            return true;
        }

        return false;
    }

    public async Task<GitResult> AddAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var args = new List<string> { "add", "--" };
        args.AddRange(paths);
        return await RunAsync(ct, args.ToArray()).ConfigureAwait(false);
    }

    public async Task<GitResult> CommitAsync(string message, CancellationToken ct = default)
        => await RunAsync(ct, "commit", "-m", message).ConfigureAwait(false);

    public async Task<GitResult> PushAsync(string remote, string branch, bool setUpstream = true, CancellationToken ct = default)
    {
        if (setUpstream)
            return await RunAsync(ct, "push", "-u", remote, branch).ConfigureAwait(false);
        return await RunAsync(ct, "push", remote, branch).ConfigureAwait(false);
    }

    public async Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "remote", "get-url", remote).ConfigureAwait(false);
        return result.Success ? result.StdOut.Trim() : null;
    }

    public GitResult Run(params string[] args)
        => RunAsync(CancellationToken.None, args).GetAwaiter().GetResult();

    public Task<GitResult> RunAsync(CancellationToken ct, params string[] args)
        => ExecuteAsync(_workingDirectory, args, ct);

    public static async Task<GitResult> ExecuteAsync(string workingDirectory, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            if (!process.Start())
                return new GitResult(false, string.Empty, "Не удалось запустить процесс git.", -1);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return new GitResult(
                process.ExitCode == 0,
                stdout.ToString().TrimEnd(),
                stderr.ToString().TrimEnd(),
                process.ExitCode);
        }
        catch (Exception ex)
        {
            return new GitResult(false, string.Empty, ex.Message, -1);
        }
    }
}
