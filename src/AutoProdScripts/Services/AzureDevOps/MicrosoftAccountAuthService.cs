using System.IO;
using System.Windows;
using System.Windows.Interop;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace AutoProdScripts.Services.AzureDevOps;

public static class MicrosoftAccountAuthService
{
    public const string CacheName = "AutoProdScripts.MicrosoftAccount.v3";

    // The same public client Visual Studio uses. A browser login to this client
    // rejects personal Microsoft accounts. Visual Studio itself asks Windows (WAM)
    // and enables legacy MSA passthrough, which is what lets a Gmail Microsoft account in.
    private const string AzureDevOpsClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";

    private static readonly string[] Scopes =
    {
        "499b84ac-1321-427f-aa17-267ca6975798/user_impersonation"
    };

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutoProdScripts");

    private static string RecordPath => Path.Combine(AppDataDir, "microsoft-account.json");

    public static bool HasSignedInAccount()
    {
        var record = TryLoadRecord();
        return record is not null && !string.IsNullOrWhiteSpace(record.Username);
    }

    public static string? GetSignedInUsername() => TryLoadRecord()?.Username;

    public static async Task<(bool Ok, string Message)> SignInAsync(
        string orgUrl,
        string project,
        string repo,
        CancellationToken ct = default)
    {
        var (ok, message, token) = await AcquireCoreAsync(forceInteractive: true, ct).ConfigureAwait(false);
        if (!ok || string.IsNullOrWhiteSpace(token))
            return (false, message);

        using var client = new AzureDevOpsClient(
            orgUrl,
            project,
            repo,
            AzureDevOpsAuthorization.FromAccessToken(token));
        var (connected, status) = await client.ValidateConnectionAsync(ct).ConfigureAwait(false);
        return connected ? (true, status) : (false, status);
    }

    public static Task<(bool Ok, string Message, string? Token)> AcquireTokenAsync(CancellationToken ct = default) =>
        AcquireCoreAsync(forceInteractive: false, ct);

    public static void SignOut()
    {
        TryDelete(RecordPath);
        DeleteTokenCaches();
    }

    private static async Task<(bool Ok, string Message, string? Token)> AcquireCoreAsync(
        bool forceInteractive,
        CancellationToken ct)
    {
        try
        {
            var record = forceInteractive ? null : TryLoadRecord();
            var credential = CreateCredential(record);
            var context = new TokenRequestContext(Scopes);

            if (forceInteractive || record is null)
                await AuthenticateAndSaveAsync(credential, context, ct).ConfigureAwait(false);

            AccessToken access;
            try
            {
                access = await GetTokenOffUiAsync(credential, context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!forceInteractive && IsInteractionRequired(ex))
            {
                await AuthenticateAndSaveAsync(credential, context, ct).ConfigureAwait(false);
                access = await GetTokenOffUiAsync(credential, context, ct).ConfigureAwait(false);
            }

            var username = TryLoadRecord()?.Username;
            var message = string.IsNullOrWhiteSpace(username)
                ? "Вход через учётную запись Microsoft выполнен."
                : $"Вход выполнен: {username}";
            return (true, message, access.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, DescribeAuthError(ex), null);
        }
    }

    private static async Task AuthenticateAndSaveAsync(
        InteractiveBrowserCredential credential,
        TokenRequestContext context,
        CancellationToken ct)
    {
        // Browser login must not run on the WPF UI thread: it deadlocks the window.
        var authenticated = await Task.Run(
            () => credential.Authenticate(context, ct),
            ct).ConfigureAwait(false);
        SaveRecord(authenticated);
    }

    private static Task<AccessToken> GetTokenOffUiAsync(
        InteractiveBrowserCredential credential,
        TokenRequestContext context,
        CancellationToken ct)
    {
        return Task.Run(async () =>
            await credential.GetTokenAsync(context, ct).ConfigureAwait(false), ct);
    }

    private static InteractiveBrowserCredential CreateCredential(AuthenticationRecord? record)
    {
        var options = new InteractiveBrowserCredentialBrokerOptions(GetParentWindowHandle())
        {
            ClientId = AzureDevOpsClientId,
            TenantId = "organizations",
            RedirectUri = new Uri("http://localhost"),
            AuthenticationRecord = record,
            LoginHint = record?.Username,
            DisableAutomaticAuthentication = true,
            IsLegacyMsaPassthroughEnabled = true,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = CacheName
            }
        };
        options.AdditionallyAllowedTenants.Add("*");
        return new InteractiveBrowserCredential(options);
    }

    private static IntPtr GetParentWindowHandle()
    {
        var app = Application.Current;
        if (app is null)
            return IntPtr.Zero;
        if (app.Dispatcher.CheckAccess())
            return HandleOfActiveWindow(app);
        return app.Dispatcher.Invoke(() => HandleOfActiveWindow(app));
    }

    private static IntPtr HandleOfActiveWindow(Application app)
    {
        var window = app.Windows.OfType<Window>().FirstOrDefault(item => item.IsActive) ?? app.MainWindow;
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }

    private static bool IsInteractionRequired(Exception ex) =>
        ex is AuthenticationRequiredException or CredentialUnavailableException ||
        (ex is AuthenticationFailedException &&
         ex.Message.Contains("interaction", StringComparison.OrdinalIgnoreCase));

    private static string DescribeAuthError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("отмен", StringComparison.OrdinalIgnoreCase))
            return "Вход отменён.";

        if (message.Contains("личной", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("personal", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("AADSTS50020", StringComparison.OrdinalIgnoreCase))
            return "Microsoft открыл вход только для рабочей учётной записи. Войдите ещё раз и в окне Windows выберите ту же учётную запись, что и в Visual Studio.";

        if (message.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase))
            return "Администратор организации должен разрешить доступ к Azure DevOps. " + message;

        if (message.Length > 500)
            message = message[..500] + "…";
        return "Не удалось войти через учётную запись Microsoft: " + message;
    }

    private static void SaveRecord(AuthenticationRecord record)
    {
        Directory.CreateDirectory(AppDataDir);
        using var stream = File.Create(RecordPath);
        record.Serialize(stream);
    }

    private static AuthenticationRecord? TryLoadRecord()
    {
        if (!File.Exists(RecordPath))
            return null;

        try
        {
            using var stream = File.OpenRead(RecordPath);
            var record = AuthenticationRecord.Deserialize(stream);
            if (string.IsNullOrWhiteSpace(record.Username) ||
                !string.Equals(record.ClientId, AzureDevOpsClientId, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(RecordPath);
                return null;
            }

            return record;
        }
        catch
        {
            TryDelete(RecordPath);
            return null;
        }
    }

    private static void DeleteTokenCaches()
    {
        var identityDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ".IdentityService");
        if (!Directory.Exists(identityDir))
            return;

        foreach (var file in Directory.EnumerateFiles(identityDir, "AutoProdScripts.MicrosoftAccount*"))
            TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The next sign-in can replace the record.
        }
    }
}
