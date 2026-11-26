using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AutoProdScripts.Core.Connections;

namespace AutoProdScripts.Views.Queries;

internal static class EnvironmentUi
{
    public static string Title(DbEnvironment environment) => environment switch
    {
        DbEnvironment.Test => "ТЕСТ",
        DbEnvironment.Dev => "РАЗРАБОТКА",
        _ => "БОЙ"
    };

    public static string BrushKey(DbEnvironment environment) => environment switch
    {
        DbEnvironment.Test => "WarnBrush",
        DbEnvironment.Dev => "SuccessBrush",
        _ => "DangerBrush"
    };

    /// <summary>Строка списка: цветная метка среды и имя подключения.</summary>
    public static FrameworkElement Label(DbConnectionProfile profile)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var mark = new Run("● ");
        mark.SetResourceReference(TextElement.ForegroundProperty, BrushKey(profile.Environment));
        text.Inlines.Add(mark);
        text.Inlines.Add(new Run(profile.Name));
        text.ToolTip = $"{Title(profile.Environment)} · {profile.Server} · {profile.Database}";
        return text;
    }
}
