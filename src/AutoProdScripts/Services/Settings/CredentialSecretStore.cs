using AutoProdScripts.Core.Connections;

namespace AutoProdScripts.Services.Settings;

/// <summary>Пароли подключений к базам — только в Windows Credential Manager, без копий на диске.</summary>
public sealed class CredentialSecretStore : ISecretStore
{
    public bool TrySave(string key, string secret) => CredentialManagerStore.TrySave(key, secret);

    public string? Load(string key) => CredentialManagerStore.TryLoad(key, out var secret) ? secret : null;

    public void Delete(string key) => CredentialManagerStore.TryDelete(key);
}
