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

    public async Task<GitResult> CreateBranchAsync(string branchName, string baseBranch, CancellationToken ct = default)
    {
        var checkoutBase = await RunAsync(ct, "checkout", baseBranch).ConfigureAwait(false);
        if (!checkoutBase.Success)
            return checkoutBase;

        return await RunAsync(ct, "checkout", "-b", branchName).ConfigureAwait(false);
    }

    public async Task<GitResult> StatusPorcelainAsync(CancellationToken ct = default)
        => await RunAsync(ct, "status", "--porcelain").ConfigureAwait(false);

    public async Task<IReadOnlyList<GitStatusEntry>> GetStatusEntriesAsync(CancellationToken ct = default)
    {
        var result = await StatusPorcelainAsync(ct).ConfigureAwait(false);
        var entries = new List<GitStatusEntry>();
        if (!result.Success)
            return entries;

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4)
                continue;
            var status = line[..2].Trim();
            var path = line[3..].Trim().Trim('"');
            entries.Add(new GitStatusEntry(status, path));
        }

        return entries;
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
