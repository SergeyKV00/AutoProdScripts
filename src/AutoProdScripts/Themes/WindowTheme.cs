using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoProdScripts.Themes;

public static class WindowTheme
{
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int BorderColor = 34;
    private const int UseImmersiveDarkMode = 20;

    private static ImageSource? _icon;

    public static void Apply(Window window)
    {
        window.Icon ??= Icon();

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));

        var caption = ToColorRef(0x10, 0x1C, 0x2E);
        var text = ToColorRef(0xD6, 0xE2, 0xF0);
        var border = ToColorRef(0x24, 0x36, 0x4C);
        _ = DwmSetWindowAttribute(hwnd, CaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, TextColor, ref text, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, BorderColor, ref border, sizeof(int));
    }

    private static ImageSource Icon()
    {
        if (_icon is not null)
            return _icon;

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        _icon = image;
        return _icon;
    }

    private static int ToColorRef(int red, int green, int blue) => red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
