using System.Windows;
using AutoProdScripts.Services.Logging;
using AutoProdScripts.Services.Settings;
using AutoProdScripts.Themes;

namespace AutoProdScripts;

public partial class App : Application
{
    public static AppSettingsService Settings { get; } = new();
    public static AppLogService Log { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings.Load();
        _ = SqlHighlightingLoader.GetDefinition();
        Log.Info("Приложение запущено.");
    }
}
