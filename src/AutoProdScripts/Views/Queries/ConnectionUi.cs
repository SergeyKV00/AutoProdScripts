using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AutoProdScripts.Core.Connections;

namespace AutoProdScripts.Views.Queries;

/// <summary>Общие подписи подключений.</summary>
internal static class ConnectionUi
{
    /// <summary>Куда ведёт подключение: «кластер» или «база X».</summary>
    public static string Target(DbConnectionProfile profile) =>
        profile.IsClusterLevel ? "кластер" : $"база {profile.Database}";

    public static string Option(DbConnectionProfile profile) => $"{profile.Name}  ·  {Target(profile)}";

    /// <summary>Группа подключения для подписи, или пусто.</summary>
    public static string GroupOf(DbConnectionProfile profile) => ConnectionStore.NormalizeGroup(profile.Group);

    public const string ClusterWarning =
        "Не выбрана база: выберите её рядом с подключением, добавьте USE или пишите База.dbo.Таблица.";

    /// <summary>Строка списка: имя, группа и ниже — кластер это или база.</summary>
    public static FrameworkElement Label(DbConnectionProfile profile)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Run(profile.Name));
        var group = GroupOf(profile);
        if (group.Length > 0)
        {
            var groupRun = new Run("  · " + group) { FontSize = 11 };
            groupRun.SetResourceReference(TextElement.ForegroundProperty, "FgMutedBrush");
            text.Inlines.Add(groupRun);
        }

        text.Inlines.Add(new LineBreak());
        var kind = new Run(profile.IsClusterLevel ? "    КЛАСТЕР " : "    БАЗА ") { FontSize = 11, FontWeight = FontWeights.Bold };
        kind.SetResourceReference(TextElement.ForegroundProperty, profile.IsClusterLevel ? "WarnBrush" : "FgInfoBrush");
        text.Inlines.Add(kind);
        var where = new Run(profile.IsClusterLevel ? profile.Server : $"{profile.Database} · {profile.Server}") { FontSize = 11 };
        where.SetResourceReference(TextElement.ForegroundProperty, "FgMutedBrush");
        text.Inlines.Add(where);
        text.ToolTip = $"{profile.Server} · {Target(profile)}";
        return text;
    }
}
