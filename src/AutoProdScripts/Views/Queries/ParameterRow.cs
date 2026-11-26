using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Views.Queries;

/// <summary>
/// Ввод одного параметра перед запуском: название и тип, поле с подсказкой формата, NULL
/// и замечание, если значение не подходит.
/// </summary>
internal sealed class ParameterRow
{
    private readonly string _name;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _technicalName = new() { Visibility = Visibility.Collapsed };
    private readonly ContentControl _kindText = ParameterKinds.Label();
    private readonly ComboBox _kind = ParameterKinds.Combo(SqlParameterKind.NVarChar);
    private readonly TextBox _value = new() { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
    // Лежит поверх поля, поэтому не должна перехватывать мышь.
    private readonly TextBlock _placeholder = new()
    {
        IsHitTestVisible = false,
        Margin = new Thickness(10, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly ToggleButton _isNull = new() { Content = "NULL", ToolTip = "Передать NULL вместо значения" };
    private readonly TextBlock _remark = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    /// <param name="kindChanged">null — тип здесь не выбирается (у цепочки он задан в настройке).</param>
    /// <param name="submit">Enter в поле значения.</param>
    public ParameterRow(string name, Action? kindChanged, Action submit)
    {
        _name = name;
        _title.Text = name;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        _technicalName.Text = name;
        _technicalName.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        _kindText.VerticalAlignment = VerticalAlignment.Center;
        _kindText.Margin = new Thickness(8, 0, 0, 0);
        _kindText.SetResourceReference(Control.ForegroundProperty, "FgBrush");
        _kind.Width = 195;
        _kind.Margin = new Thickness(8, 0, 0, 0);
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        _isNull.SetResourceReference(FrameworkElement.StyleProperty, "ChipToggle");

        _value.TextChanged += (_, _) => Check();
        _value.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            submit();
        };
        _isNull.Checked += (_, _) => Check();
        _isNull.Unchecked += (_, _) => Check();
        _kind.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            Check();
            kindChanged?.Invoke();
        };

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_title);
        titles.Children.Add(_technicalName);

        var header = new DockPanel();
        FrameworkElement type = kindChanged is not null ? _kind : _kindText;
        DockPanel.SetDock(type, Dock.Right);
        header.Children.Add(type);
        header.Children.Add(titles);

        var field = new Grid();
        field.Children.Add(_value);
        field.Children.Add(_placeholder);

        var input = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(_isNull, Dock.Right);
        input.Children.Add(_isNull);
        input.Children.Add(field);

        var panel = new StackPanel { Tag = name, Margin = new Thickness(0, 6, 0, 8) };
        panel.Children.Add(header);
        panel.Children.Add(input);
        panel.Children.Add(_remark);
        Element = panel;
        Check();
    }

    public FrameworkElement Element { get; }

    public SqlParameterKind Kind => ParameterKinds.At(_kind.SelectedIndex);

    /// <summary>Введённое значение; null — выбран NULL.</summary>
    public string? Text => _isNull.IsChecked == true ? null : _value.Text;

    /// <summary>Подпись и тип из библиотеки; введённое значение не трогает.</summary>
    public void Describe(ScriptParameter parameter)
    {
        var labeled = parameter.Label.Length > 0;
        _title.Text = labeled ? parameter.Label : _name;
        _title.ToolTip = labeled ? $"{parameter.Label} ({_name})" : _name;
        _technicalName.Visibility = labeled ? Visibility.Visible : Visibility.Collapsed;
        _kind.SelectedIndex = ParameterKinds.IndexOf(parameter.Kind);
        Check();
    }

    public void SetValue(string value) => _value.Text = value;

    /// <summary>Значение по умолчанию подставляется только в пустое поле.</summary>
    public void SetDefault(string? value)
    {
        if (_value.Text.Length == 0 && value is not null)
            _value.Text = value;
    }

    public void Clear()
    {
        _isNull.IsChecked = false;
        _value.Clear();
    }

    public SqlParameterValue Value() => new(_name, Kind, Text);

    private void Check()
    {
        var isNull = _isNull.IsChecked == true;
        var kind = Kind;
        _kindText.Content = ParameterKinds.Options[ParameterKinds.IndexOf(kind)];
        _value.IsEnabled = !isNull;
        _placeholder.Text = isNull ? "будет передан NULL" : Hint(kind);
        _placeholder.Visibility = isNull || _value.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // При NULL введённый текст не показываем поверх подсказки.
        _value.Opacity = isNull ? 0.35 : 1;
        if (isNull && _value.Text.Length > 0)
            _placeholder.Visibility = Visibility.Collapsed;

        var (remark, brush) = Remark(kind, isNull);
        _remark.Text = remark;
        _remark.Visibility = remark.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _remark.Margin = new Thickness(0, 3, 0, 0);
        _remark.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (brush == "DangerBrush" && remark.Length > 0)
            _value.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
        else
            _value.ClearValue(Control.BorderBrushProperty);
    }

    private (string Text, string Brush) Remark(SqlParameterKind kind, bool isNull)
    {
        var text = _value.Text;
        if (isNull || text.Length == 0)
            return (string.Empty, "FgMutedBrush");

        if (kind is SqlParameterKind.NVarChar or SqlParameterKind.VarChar)
        {
            // Скопированный из письма ИИН с пробелом в конце просто «не находится».
            return text != text.Trim()
                ? ("Есть пробелы по краям.", "WarnBrush")
                : (string.Empty, "FgMutedBrush");
        }

        try
        {
            new SqlParameterValue(_name, kind, text).ToSqlParameter();
            return (string.Empty, "FgMutedBrush");
        }
        catch (FormatException)
        {
            return ($"Ожидается: {Hint(kind)}.", "DangerBrush");
        }
    }

    private static string Hint(SqlParameterKind kind) => kind switch
    {
        SqlParameterKind.Int or SqlParameterKind.BigInt => "целое число",
        SqlParameterKind.Decimal => "число, например 12,5",
        SqlParameterKind.Bit => "0 или 1",
        SqlParameterKind.Date => "дд.мм.гггг",
        SqlParameterKind.DateTime => "дд.мм.гггг чч:мм",
        SqlParameterKind.Guid => "GUID: 8-4-4-4-12 знаков",
        _ => "значение без кавычек"
    };
}
