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
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
        base.OnStartup(e);
        Settings.Load();
        _ = SqlHighlightingLoader.GetDefinition();
        Log.Info("Приложение запущено.");
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window))
            WindowTheme.Apply(window);
    }
}
