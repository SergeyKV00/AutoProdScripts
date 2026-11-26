using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoProdScripts.Core.Chains;
using AutoProdScripts.Core.Execution;

namespace AutoProdScripts.Views.Queries;

/// <summary>Предыдущий шаг вернул несколько строк — пользователь выбирает одну целиком.</summary>
internal sealed class ChoiceDialog : Window
{
    private const int Shown = 1000;
    private readonly DataGrid _grid;

    public ChoiceDialog(ChoiceRequest request)
    {
        Title = "Выберите строку";
        Width = 760;
        Height = 480;
        MinWidth = 460;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "FgBrush");

        var uses = request.Uses.Select(u => $"  шаг {u.StepIndex + 1}: {u.Parameter} ← колонка «{u.Column}»");
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = $"Шаг {request.SourceStepIndex + 1} вернул несколько строк. Из выбранной строки будут взяты:\n"
                   + string.Join('\n', uses)
                   + (request.Rows.Count > Shown ? $"\nПоказаны первые {Shown} строк." : string.Empty)
        };

        // Вся строка, а не только передаваемые колонки: по остальным её проще узнать.
        _grid = ResultsPanel.CreateGrid(new QueryResultSet(request.Columns, request.Rows.Take(Shown).ToList(), false));
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.MouseDoubleClick += (_, e) =>
        {
            // Двойной щелчок по заголовку или прокрутке ничего не выбирает.
            if (e.OriginalSource is FrameworkElement { DataContext: object?[] })
                Accept();
        };
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            Accept();
        };

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
        panel.Children.Add(_grid);
        Content = panel;
    }

    public int? Selected { get; private set; }

    private void Accept()
    {
        // Без выбора не подставляем первую строку молча.
        if (_grid.SelectedIndex < 0)
            return;
        Selected = _grid.SelectedIndex;
        DialogResult = true;
    }
}
