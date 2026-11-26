using System.Windows;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Workspace;
using AutoProdScripts.Core.Regions;
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
    private static RegionStore? _regions;
    private static OpenTabsStore? _openTabs;

    /// <summary>Открытые вкладки раздела «Запросы» — восстанавливаются после перезапуска.</summary>
    public static OpenTabsStore OpenTabs => _openTabs ??= new OpenTabsStore(Settings.SettingsDirectory);

    private static ExplorerStateStore? _explorerStates;

    /// <summary>Состояние левой панели «Запросов»: раскрытые узлы, фильтры, вид.</summary>
    public static ExplorerStateStore ExplorerStates => _explorerStates ??= new ExplorerStateStore(Settings.SettingsDirectory);

    /// <summary>Справочник «регион → кластер».</summary>
    public static RegionStore Regions => _regions ??= new RegionStore(Settings.SettingsDirectory);

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
