using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AutoProdScripts.Core.Execution;

namespace AutoProdScripts.Views.Queries;

/// <summary>Тип параметра в списке: имя из SQL и очень краткое пояснение.</summary>
internal sealed record ParameterKindOption(SqlParameterKind Kind, string SqlName, string Description)
{
    // Набор «int» на клавиатуре выбирает тип в списке.
    public override string ToString() => SqlName;
}

internal static class ParameterKinds
{
    public static IReadOnlyList<ParameterKindOption> Options { get; } =
    [
        new(SqlParameterKind.NVarChar, "NVARCHAR", "юникод"),
        new(SqlParameterKind.VarChar, "VARCHAR", "строка"),
        new(SqlParameterKind.Int, "INT", "целое"),
        new(SqlParameterKind.BigInt, "BIGINT", "большое"),
        new(SqlParameterKind.Decimal, "DECIMAL", "дробное"),
        new(SqlParameterKind.Bit, "BIT", "0 / 1"),
        new(SqlParameterKind.Date, "DATE", "дата"),
        new(SqlParameterKind.DateTime, "DATETIME", "и время"),
        new(SqlParameterKind.Guid, "UNIQUEIDENTIFIER", "GUID")
    ];

    private static readonly DataTemplate Template = CreateTemplate();

    public static SqlParameterKind At(int index) => Options[Math.Max(0, index)].Kind;

    public static int IndexOf(SqlParameterKind kind)
    {
        for (var i = 0; i < Options.Count; i++)
        {
            if (Options[i].Kind == kind)
                return i;
        }

        return 0;
    }

    public static ComboBox Combo(SqlParameterKind kind) => new()
    {
        ItemsSource = Options,
        ItemTemplate = Template,
        SelectedIndex = IndexOf(kind),
        ToolTip = "Тип параметра"
    };

    /// <summary>Тип без возможности выбора, в том же виде, что и в списке.</summary>
    public static ContentControl Label() => new() { ContentTemplate = Template, Focusable = false };

    private static DataTemplate CreateTemplate()
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ParameterKindOption.SqlName)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgInfoBrush");

        var description = new FrameworkElementFactory(typeof(TextBlock));
        description.SetBinding(TextBlock.TextProperty, new Binding(nameof(ParameterKindOption.Description)));
        description.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        description.SetValue(UIElement.OpacityProperty, 0.75);

        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.AppendChild(name);
        row.AppendChild(description);
        return new DataTemplate { VisualTree = row };
    }
}
