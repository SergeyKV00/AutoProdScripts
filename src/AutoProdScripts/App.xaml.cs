using System.Windows;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Services.Logging;
using AutoProdScripts.Services.Settings;
using AutoProdScripts.Themes;

namespace AutoProdScripts;

public partial class App : Application
{
    public static AppSettingsService Settings { get; } = new();
    public static AppLogService Log { get; } = new();

    private static ConnectionStore? _connections;
    private static ScriptLibrary? _library;

    /// <summary>Локальная библиотека скриптов раздела «Запросы».</summary>
    public static ScriptLibrary Library =>
        _library ??= new ScriptLibrary(System.IO.Path.Combine(Settings.SettingsDirectory, "library"));

    /// <summary>Подключения раздела «Запросы»; читаются при первом обращении.</summary>
    public static ConnectionStore Connections =>
        _connections ??= new ConnectionStore(Settings.SettingsDirectory, new CredentialSecretStore());

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
        base.OnStartup(e);
        Settings.Load();
        ThemeService.Apply(ThemeService.Parse(Settings.Current.Theme));
        _ = SqlHighlightingLoader.GetDefinition();
        Log.Info("Приложение запущено.");
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window))
            WindowTheme.Apply(window);
    }
}
