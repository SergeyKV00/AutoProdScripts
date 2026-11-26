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

    /// <summary>Куда ведёт подключение: «кластер» или «база X».</summary>
    public static string Target(DbConnectionProfile profile) =>
        profile.IsClusterLevel ? "кластер" : $"база {profile.Database}";

    public static string Option(DbConnectionProfile profile) => $"{profile.Name}  ·  {Target(profile)}";

    public const string ClusterWarning =
        "Не указана база: добавьте USE или пишите База.dbo.Таблица.";

    /// <summary>Строка списка: цветная метка среды, имя и ниже — кластер это или база.</summary>
    public static FrameworkElement Label(DbConnectionProfile profile)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var mark = new Run("● ");
        mark.SetResourceReference(TextElement.ForegroundProperty, BrushKey(profile.Environment));
        text.Inlines.Add(mark);
        text.Inlines.Add(new Run(profile.Name));
        text.Inlines.Add(new LineBreak());
        var kind = new Run(profile.IsClusterLevel ? "    КЛАСТЕР " : "    БАЗА ") { FontSize = 11, FontWeight = FontWeights.Bold };
        kind.SetResourceReference(TextElement.ForegroundProperty, profile.IsClusterLevel ? "WarnBrush" : "FgInfoBrush");
        text.Inlines.Add(kind);
        var where = new Run(profile.IsClusterLevel ? profile.Server : $"{profile.Database} · {profile.Server}") { FontSize = 11 };
        where.SetResourceReference(TextElement.ForegroundProperty, "FgMutedBrush");
        text.Inlines.Add(where);
        text.ToolTip = $"{Title(profile.Environment)} · {profile.Server} · {Target(profile)}";
        return text;
    }
}
