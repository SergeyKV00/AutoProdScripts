using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoProdScripts.Services.Settings;

/// <summary>
/// Stores the Azure DevOps PAT using Windows DPAPI when available.
/// Falls back to a local encrypted blob under %AppData%\AutoProdScripts.
/// </summary>
public sealed class SecureSecretStore
{
    private const string TargetName = "AutoProdScripts:AzureDevOpsPat";
    private readonly string _patPath;

    public SecureSecretStore(string settingsDirectory)
    {
        _patPath = Path.Combine(settingsDirectory, "pat.dpapi");
    }

    public void SavePat(string pat)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_patPath)!);

        // Prefer Windows Credential Manager when possible.
        if (CredentialManagerStore.TrySave(TargetName, pat))
        {
            // Keep DPAPI file in sync as a secondary store for portability within the same user profile.
        }

        var bytes = Encoding.UTF8.GetBytes(pat);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_patPath, protectedBytes);
    }

    public string? LoadPat()
    {
        if (CredentialManagerStore.TryLoad(TargetName, out var fromCred) &&
            !string.IsNullOrWhiteSpace(fromCred))
        {
            return fromCred;
        }

        if (!File.Exists(_patPath))
            return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(_patPath);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void ClearPat()
    {
        CredentialManagerStore.TryDelete(TargetName);
        if (File.Exists(_patPath))
            File.Delete(_patPath);
    }
}
