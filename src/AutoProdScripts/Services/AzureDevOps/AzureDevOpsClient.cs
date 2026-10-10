using System.Net;
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

    public AzureDevOpsClient(string orgUrl, string project, string repo, AzureDevOpsAuthorization authorization)
    {
        _orgUrl = NormalizeOrgUrl(orgUrl);
        _project = project.Trim();
        _repo = repo.Trim();

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(authorization.Scheme, authorization.Parameter);
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

    public async Task<(bool Ok, string Message)> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_orgUrl}/_apis/connectionData?api-version=7.1-preview.1";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var name = TryReadAuthenticatedUser(body);
                return (true, string.IsNullOrWhiteSpace(name)
                    ? "Подключение к Azure DevOps успешно."
                    : $"Подключение к Azure DevOps успешно: {name}.");
            }

            return (false, $"Ошибка проверки подключения: {(int)response.StatusCode} {response.ReasonPhrase}. {TrimBody(body)}");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка сети при проверке подключения: {ex.Message}");
        }
    }

    private static string? TryReadAuthenticatedUser(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("authenticatedUser", out var user))
                return null;

            foreach (var property in new[] { "providerDisplayName", "customDisplayName" })
            {
                if (user.TryGetProperty(property, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
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

            return (true, new PullRequestResult(true, id, BuildPrWebUrl(id), message, title));
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
        IReadOnlyList<SavedReviewer>? reviewers = null,
        CancellationToken ct = default)
    {
        try
        {
            var apiUrl =
                $"{_orgUrl}/{Uri.EscapeDataString(_project)}/_apis/git/repositories/{Uri.EscapeDataString(_repo)}/pullrequests?api-version=7.1";

            var reviewerPayload = (reviewers ?? Array.Empty<SavedReviewer>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    id = g.Key,
                    isRequired = g.Any(x => x.IsRequired)
                })
                .ToArray();

            var payload = new Dictionary<string, object?>
            {
                ["sourceRefName"] = ToRefsHeads(sourceBranch),
                ["targetRefName"] = ToRefsHeads(targetBranch),
                ["title"] = title,
                ["description"] = description
            };
            if (reviewerPayload.Length > 0)
                payload["reviewers"] = reviewerPayload;

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

    public async Task<ProjectMembersResult> ListProjectMembersAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_project))
            return new ProjectMembersResult(Array.Empty<ProjectMember>(), "Не указан проект Azure DevOps.");

        try
        {
            var teamsUrl = $"{_orgUrl}/_apis/projects/{Uri.EscapeDataString(_project)}/teams?api-version=7.1";
            var (teams, teamsError) = await GetPagedValuesAsync(teamsUrl, ct).ConfigureAwait(false);
            if (teams.Count == 0)
            {
                return new ProjectMembersResult(
                    Array.Empty<ProjectMember>(),
                    teamsError ?? "В проекте не найдено команд Azure DevOps.");
            }

            var people = new Dictionary<string, ProjectMember>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(teamsError))
                warnings.Add(teamsError);

            foreach (var team in teams)
            {
                var teamId = team.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var teamName = team.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : teamId;
                if (string.IsNullOrWhiteSpace(teamId))
                    continue;

                var membersUrl =
                    $"{_orgUrl}/_apis/projects/{Uri.EscapeDataString(_project)}/teams/{Uri.EscapeDataString(teamId)}/members?api-version=7.1";
                var (members, membersError) = await GetPagedValuesAsync(membersUrl, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(membersError))
                {
                    warnings.Add($"{teamName}: {membersError}");
                    continue;
                }

                foreach (var member in members)
                {
                    if (TryReadPerson(member, out var person))
                        people.TryAdd(person.Id, person);
                }
            }

            var list = people.Values
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            string? error = null;
            if (list.Count == 0 && warnings.Count > 0)
                error = "Не удалось получить участников команд. " + string.Join(" ", warnings.Take(2));
            else if (warnings.Count > 0)
                error = "Часть команд не прочитана. " + string.Join(" ", warnings.Take(2));
            else if (list.Count == 0)
                error = "Участники команд проекта не найдены.";

            return new ProjectMembersResult(list, error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProjectMembersResult(Array.Empty<ProjectMember>(), $"Ошибка сети при чтении участников: {ex.Message}");
        }
    }

    private async Task<(List<JsonElement> Items, string? Error)> GetPagedValuesAsync(string url, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var skip = 0;
        const int pageSize = 200;
        string? firstIdOfPreviousPage = null;

        for (var page = 0; page < 30; page++)
        {
            var pageUrl = url + (url.Contains('?') ? "&" : "?") + "$top=" + pageSize + "&$skip=" + skip;
            using var response = await _http.GetAsync(pageUrl, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (items, FormatApiError(response, body));

            List<JsonElement> pageItems;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                    break;

                pageItems = new List<JsonElement>();
                foreach (var item in value.EnumerateArray())
                    pageItems.Add(item.Clone());
            }
            catch (JsonException)
            {
                return (items, "Некорректный ответ Azure DevOps.");
            }

            if (pageItems.Count == 0)
                break;

            var firstId = TryGetId(pageItems[0]);
            if (firstId is not null && firstId == firstIdOfPreviousPage)
                break;

            items.AddRange(pageItems);
            if (pageItems.Count < pageSize)
                break;

            firstIdOfPreviousPage = firstId;
            skip += pageItems.Count;
        }

        return (items, null);
    }

    private static bool TryReadPerson(JsonElement member, out ProjectMember person)
    {
        person = null!;
        if (!member.TryGetProperty("identity", out var identity))
            return false;

        if (identity.TryGetProperty("isContainer", out var container) && container.ValueKind == JsonValueKind.True)
            return false;

        if (identity.TryGetProperty("descriptor", out var descriptor) &&
            descriptor.ValueKind == JsonValueKind.String &&
            descriptor.GetString() is string desc &&
            desc.StartsWith("vssgp.", StringComparison.OrdinalIgnoreCase))
            return false;

        var id = identity.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
        var displayName = identity.TryGetProperty("displayName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString()
            : null;
        var uniqueName = identity.TryGetProperty("uniqueName", out var uniqueProp) && uniqueProp.ValueKind == JsonValueKind.String
            ? uniqueProp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(uniqueName) &&
            identity.TryGetProperty("directoryAlias", out var alias) &&
            alias.ValueKind == JsonValueKind.String)
            uniqueName = alias.GetString();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
            return false;

        uniqueName ??= string.Empty;
        if (uniqueName.StartsWith("vstfs://", StringComparison.OrdinalIgnoreCase) ||
            uniqueName.StartsWith('['))
            return false;

        if (displayName.Contains("Build Service", StringComparison.OrdinalIgnoreCase))
            return false;

        person = new ProjectMember(id, displayName.Trim(), uniqueName.Trim());
        return true;
    }

    private static string? TryGetId(JsonElement item)
    {
        if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();

        if (item.TryGetProperty("identity", out var identity) &&
            identity.TryGetProperty("id", out var identityId) &&
            identityId.ValueKind == JsonValueKind.String)
            return identityId.GetString();

        return null;
    }

    private static string FormatApiError(HttpResponseMessage response, string body)
    {
        var message = $"Ошибка {(int)response.StatusCode} {response.ReasonPhrase}. {TrimBody(body)}";
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            message += " Проверьте доступ: для списка людей нужна область Project and Team (Read).";
        else if (response.StatusCode == HttpStatusCode.NotFound)
            message += " Проверьте имя проекта Azure DevOps.";
        return message;
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

public readonly record struct AzureDevOpsAuthorization(string Scheme, string Parameter)
{
    public static AzureDevOpsAuthorization FromPat(string pat)
    {
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        return new("Basic", token);
    }

    public static AzureDevOpsAuthorization FromAccessToken(string accessToken) =>
        new("Bearer", accessToken);
}

public sealed record ProjectMember(string Id, string DisplayName, string UniqueName);

public sealed record ProjectMembersResult(IReadOnlyList<ProjectMember> Members, string? Error);
