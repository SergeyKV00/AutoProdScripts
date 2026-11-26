using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Chains;
using AutoProdScripts.Core.Execution;

namespace AutoProdScripts.Views.Queries;

/// <summary>Предыдущий шаг вернул несколько значений — пользователь выбирает одно.</summary>
internal sealed class ChoiceDialog : Window
{
    private const int Shown = 1000;
    private readonly ListBox _list = new();

    public ChoiceDialog(ChoiceRequest request)
    {
        Title = "Выберите значение";
        Width = 460;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "FgBrush");

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = $"Шаг {request.SourceStepIndex + 1} вернул несколько значений «{request.Column}». "
                   + $"Какое передать в {request.Parameter} на шаге {request.StepIndex + 1}?"
                   + (request.Values.Count > Shown ? $" Показаны первые {Shown}." : string.Empty)
        };
        foreach (var value in request.Values.Take(Shown))
            _list.Items.Add(ResultFormatter.Format(value));
        _list.MouseDoubleClick += (_, _) => Accept();

        var stop = new Button { Content = "Остановить", IsCancel = true, Width = 110 };
        stop.SetResourceReference(StyleProperty, "SecondaryButton");
        var choose = new Button { Content = "Выбрать", IsDefault = true, Width = 110, Margin = new Thickness(8, 0, 0, 0) };
        choose.Click += (_, _) => Accept();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(stop);
        buttons.Children.Add(choose);

        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);
        panel.Children.Add(_list);
        Content = panel;
    }

    public int? Selected { get; private set; }

    private void Accept()
    {
        // Без выбора не подставляем первое значение молча.
        if (_list.SelectedIndex < 0)
            return;
        Selected = _list.SelectedIndex;
        DialogResult = true;
    }
}
