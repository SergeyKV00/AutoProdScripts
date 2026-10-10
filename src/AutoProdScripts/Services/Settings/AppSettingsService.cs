using System.IO;
using System.Text.Json;
using AutoProdScripts.Models;

namespace AutoProdScripts.Services.Settings;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsDir;
    private readonly string _settingsPath;
    private readonly SecureSecretStore _secrets;

    public AppSettings Current { get; private set; } = new();

    public AppSettingsService()
    {
        _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoProdScripts");
        _settingsPath = Path.Combine(_settingsDir, "settings.json");
        _secrets = new SecureSecretStore(_settingsDir);
    }

    public string SettingsDirectory => _settingsDir;

    public void Load()
    {
        Directory.CreateDirectory(_settingsDir);
        if (!File.Exists(_settingsPath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_settingsDir);
        Current = settings;
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    public void SavePat(string pat) => _secrets.SavePat(pat);

    public string? LoadPat() => _secrets.LoadPat();

    public void ClearPat() => _secrets.ClearPat();

    public bool HasPat() => !string.IsNullOrWhiteSpace(LoadPat());
}
