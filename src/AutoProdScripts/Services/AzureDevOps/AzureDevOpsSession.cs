using AutoProdScripts.Models;

namespace AutoProdScripts.Services.AzureDevOps;

public sealed class AzureDevOpsSession
{
    private AzureDevOpsSession(AzureDevOpsAuthorization authorization) => Authorization = authorization;

    public AzureDevOpsAuthorization Authorization { get; }

    public AzureDevOpsClient CreateClient(string orgUrl, string project, string repo) =>
        new(orgUrl, project, repo, Authorization);

    public static async Task<(AzureDevOpsSession? Session, string? Error)> OpenAsync(
        AppSettings settings,
        CancellationToken ct = default)
    {
        if (settings.AuthMode == AzureAuthMode.MicrosoftAccount)
        {
            var (ok, message, token) = await MicrosoftAccountAuthService.AcquireTokenAsync(ct).ConfigureAwait(true);
            if (!ok || string.IsNullOrWhiteSpace(token))
                return (null, message);
            return (new AzureDevOpsSession(AzureDevOpsAuthorization.FromAccessToken(token)), null);
        }

        var pat = App.Settings.LoadPat();
        if (string.IsNullOrWhiteSpace(pat))
            return (null, "PAT не задан. Откройте Настройки.");

        return (new AzureDevOpsSession(AzureDevOpsAuthorization.FromPat(pat)), null);
    }
}
