using System.IO;
using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace AutoProdScripts.Themes;

/// <summary>
/// Loads the muted SQL highlighting definition shipped as an embedded resource
/// and recolors it for the active theme.
/// </summary>
public static class SqlHighlightingLoader
{
    public const string DefinitionName = "SQL-Muted";
    private const string ResourceName = "AutoProdScripts.Resources.SqlMutedDark.xshd";

    private static readonly Dictionary<AppTheme, IHighlightingDefinition> Cache = new();

    // Цвета тёмной палитры из xshd -> цвета для остальных тем.
    private static readonly string[] DarkColors =
        ["#6D7C8A", "#8FCBB0", "#6CB6FF", "#D4B483", "#9AADC2", "#C9A86C", "#B8A070"];

    private static readonly Dictionary<AppTheme, string[]> Palettes = new()
    {
        [AppTheme.Light] = ["#6B7A8C", "#1E7F5C", "#1F5FD6", "#9A5B00", "#5B6679", "#9A6B00", "#8A6B20"],
        [AppTheme.Gta6] = ["#9C7BBF", "#FFD166", "#FF5CAA", "#35E0E0", "#C9A8DC", "#FFB020", "#FF8A5C"]
    };

    public static IHighlightingDefinition? GetDefinition() => GetDefinition(ThemeService.Current);

    public static IHighlightingDefinition? GetDefinition(AppTheme theme)
    {
        if (Cache.TryGetValue(theme, out var cached))
            return cached;

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;

        using var text = new StreamReader(stream);
        var xshd = text.ReadToEnd();
        if (Palettes.TryGetValue(theme, out var palette))
        {
            for (var i = 0; i < DarkColors.Length; i++)
                xshd = xshd.Replace(DarkColors[i], palette[i], StringComparison.OrdinalIgnoreCase);
        }

        using var reader = XmlReader.Create(new StringReader(xshd));
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        if (theme == AppTheme.Dark)
            HighlightingManager.Instance.RegisterHighlighting(DefinitionName, [".sql"], definition);

        Cache[theme] = definition;
        return definition;
    }
}
