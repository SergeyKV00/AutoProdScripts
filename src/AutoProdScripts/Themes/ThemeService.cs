using System.Windows;
using System.Windows.Media;

namespace AutoProdScripts.Themes;

public enum AppTheme
{
    Dark,
    Light,
    Gta6
}

/// <summary>
/// Переключение цветовых схем: подменяет словарь ресурсов приложения (ключи кистей одинаковы во всех темах).
/// </summary>
public static class ThemeService
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action? ThemeChanged;

    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) ? theme : AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{FileName(theme)}.xaml", UriKind.Absolute)
        };
        if (dictionaries.Count > 0)
            dictionaries[0] = next;
        else
            dictionaries.Add(next);

        Current = theme;
        foreach (Window window in Application.Current.Windows)
            WindowTheme.Apply(window);
        ThemeChanged?.Invoke();
    }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static string Text(string key) => (string)Application.Current.FindResource(key);

    private static string FileName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "LightTheme",
        AppTheme.Gta6 => "Gta6Theme",
        _ => "DarkTheme"
    };
}
