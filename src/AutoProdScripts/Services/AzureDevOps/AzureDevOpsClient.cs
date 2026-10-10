using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoProdScripts.Models;

namespace AutoProdScripts.Services.AzureDevOps;

public sealed class AzureDevOpsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly string _repo;

    public AzureDevOpsClient(string orgUrl, string project, string repo, string pat)
    {
        _orgUrl = NormalizeOrgUrl(orgUrl);
        _project = project.Trim();
        _repo = repo.Trim();

        _http = new HttpClient();
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public static string NormalizeOrgUrl(string orgUrl)
    {
        var url = (orgUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            return "https://cit-damu.visualstudio.com";
        return url;
    }

    public async Task<(bool Ok, string Message)> ValidatePatAsync(CancellationToken ct = default)
    {
        try
        {
            // Lightweight call: connection data / profile
            var url = $"{_orgUrl}/_apis/connectionData?api-version=7.1-preview.1";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (true, "PAT принят. Подключение к Azure DevOps успешно.");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (false, $"Ошибка проверки PAT: {(int)response.StatusCode} {response.ReasonPhrase}. {TrimBody(body)}");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка сети при проверке PAT: {ex.Message}");
        }
    }

    public async Task<(bool Checked, PullRequestResult? Active)> FindActivePullRequestAsync(
        string sourceBranch,
        CancellationToken ct = default)
    {
        try
        {
            var source = Uri.EscapeDataString(ToRefsHeads(sourceBranch));
            var apiUrl =
                $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/git/repositories/{Uri.EscapeDataString(_repo)}/pullrequests?searchCriteria.sourceRefName={source}&searchCriteria.status=active&api-version=7.1";

            using var response = await _http.GetAsync(apiUrl, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, null);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                return (true, null);

            var first = value[0];
            var id = first.TryGetProperty("pullRequestId", out var idProp) && idProp.TryGetInt32(out var parsedId)
                ? parsedId
                : (int?)null;
            var title = first.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            var target = first.TryGetProperty("targetRefName", out var targetProp) ? targetProp.GetString() : null;
            var targetShort = string.IsNullOrWhiteSpace(target)
                ? null
                : target.Replace("refs/heads/", string.Empty, StringComparison.OrdinalIgnoreCase);

            var message = id.HasValue
                ? $"Для ветки «{sourceBranch.Trim()}» уже есть активный PR #{id.Value}"
                : $"Для ветки «{sourceBranch.Trim()}» уже есть активный Pull Request";
            if (!string.IsNullOrWhiteSpace(targetShort))
                message += $" в «{targetShort}»";
            message += ".";
            if (!string.IsNullOrWhiteSpace(title))
                message += $" {title}";

            return (true, new PullRequestResult(true, id, BuildPrWebUrl(id), message));
        }
        catch (Exception ex)
        {
            return (false, new PullRequestResult(false, null, null, ex.Message));
        }
    }

    public async Task<PullRequestResult> CreatePullRequestAsync(
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        CancellationToken ct = default)
    {
        try
        {
            var apiUrl =
                $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/git/repositories/{Uri.EscapeDataString(_repo)}/pullrequests?api-version=7.1";

            var payload = new
            {
                sourceRefName = ToRefsHeads(sourceBranch),
                targetRefName = ToRefsHeads(targetBranch),
                title,
                description
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(apiUrl, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new PullRequestResult(
                    false,
                    null,
                    null,
                    $"Не удалось создать PR: {(int)response.StatusCode} {response.ReasonPhrase}. {TrimBody(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var id = root.TryGetProperty("pullRequestId", out var idProp) ? idProp.GetInt32() : (int?)null;
            var url = BuildPrWebUrl(id);

            return new PullRequestResult(
                true,
                id,
                url,
                id.HasValue ? $"Pull Request #{id} создан." : "Pull Request создан.");
        }
        catch (Exception ex)
        {
            return new PullRequestResult(false, null, null, $"Ошибка Create PR: {ex.Message}");
        }
    }

    public string BuildPrWebUrl(int? pullRequestId)
    {
        if (!pullRequestId.HasValue)
            return $"{_orgUrl}/{_project}/_git/{_repo}/pullrequests";

        return $"{_orgUrl}/{_project}/_git/{_repo}/pullrequest/{pullRequestId.Value}";
    }

    private static string ToRefsHeads(string branch)
    {
        var b = branch.Trim();
        if (b.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            return b;
        if (b.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            b = b["origin/".Length..];
        return $"refs/heads/{b}";
    }

    private static string TrimBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;
        body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return body.Length > 400 ? body[..400] + "…" : body;
    }

    public void Dispose() => _http.Dispose();
}
