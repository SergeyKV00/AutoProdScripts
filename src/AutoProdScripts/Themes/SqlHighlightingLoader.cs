using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace AutoProdScripts.Themes;

/// <summary>
/// Loads the muted dark-IDE SQL highlighting definition shipped as an embedded resource.
/// </summary>
public static class SqlHighlightingLoader
{
    public const string DefinitionName = "SQL-Muted";
    private const string ResourceName = "AutoProdScripts.Resources.SqlMutedDark.xshd";

    private static IHighlightingDefinition? _cached;

    public static IHighlightingDefinition? GetDefinition()
    {
        if (_cached is not null)
            return _cached;

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;

        using var reader = XmlReader.Create(stream);
        _cached = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(
            DefinitionName,
            [".sql"],
            _cached);
        return _cached;
    }
}
