using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Views.Queries;

/// <summary>Что взять из файла коллеги и какими подключениями это заменить.</summary>
internal sealed class ImportDialog : Window
{
    private readonly LibraryPackage _package;
    private readonly Dictionary<string, ComboBox> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ComboBox> _actions = new();

    public ImportDialog(LibraryPackage package, string fileName)
    {
        _package = package;
        Title = "Импорт: " + fileName;
        Width = 760;
        Height = 640;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "FgBrush");

        var body = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        AddConnections(body);
        AddItems(body);

        var cancel = new Button { Content = "Отмена", IsCancel = true, Width = 110 };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        var import = new Button { Content = "Импортировать", IsDefault = true, Width = 150, Margin = new Thickness(8, 0, 0, 0) };
        import.Click += (_, _) =>
        {
            Result = Import();
            DialogResult = true;
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(import);

        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = panel;
    }

    public ImportResult? Result { get; private set; }

    private ImportResult Import()
    {
        var actions = _actions.ToDictionary(a => a.Key, a => (ImportAction)((ComboBoxItem)a.Value.SelectedItem).Tag);
        return LibraryExchange.Import(_package, App.Library, actions, name =>
            _connections.TryGetValue(name, out var box) && ((ComboBoxItem)box.SelectedItem).Tag is MappedConnection chosen
                ? chosen
                : new MappedConnection(null, name));
    }

    private void AddConnections(StackPanel body)
    {
        var names = _package.ConnectionNames();
        if (names.Count == 0)
            return;

        body.Children.Add(Header("Подключения"));
        body.Children.Add(Muted("Выберите свои подключения для имён из файла."));
        var mine = App.Connections.GetAll();
        foreach (var name in names)
        {
            var box = new ComboBox();
            box.Items.Add(new ComboBoxItem { Content = "Не заменять (выбрать позже)" });
            box.SelectedIndex = 0;
            foreach (var profile in mine)
            {
                var item = new ComboBoxItem
                {
                    Content = ConnectionUi.Option(profile),
                    Tag = new MappedConnection(profile.Id, profile.Name)
                };
                box.Items.Add(item);
                if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
                    box.SelectedItem = item;
            }

            _connections[name] = box;
            body.Children.Add(Row(Text(name), box));
        }
    }

    private void AddItems(StackPanel body)
    {
        body.Children.Add(Header("Что импортировать"));
        var candidates = LibraryExchange.Preview(_package, App.Library);
        if (candidates.Count == 0)
            body.Children.Add(Muted("В файле нет ни скриптов, ни цепочек."));

        foreach (var candidate in candidates)
        {
            var item = candidate.Item;
            var title = new StackPanel();
            var path = item.Group.Length > 0 ? $"{item.Group} / {item.Name}" : item.Name;
            title.Children.Add(Text((item is LibraryChain ? "🔗 " : string.Empty) + path));
            title.Children.Add(Muted(candidate.State switch
            {
                ImportState.New => "новое",
                ImportState.Identical => "уже есть, не отличается",
                _ => "уже есть, отличается от вашего"
            }));
            if (candidate.Problem is not null)
            {
                var problem = Text("Внимание: " + candidate.Problem);
                problem.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
                title.Children.Add(problem);
            }

            var box = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
            // Первое в списке — по умолчанию: ничего своего молча не перезаписываем.
            (ImportAction Action, string Title)[] options = candidate.State switch
            {
                ImportState.New => [(ImportAction.Add, "Добавить"), (ImportAction.Skip, "Пропустить")],
                ImportState.Identical => [(ImportAction.Skip, "Пропустить"), (ImportAction.Copy, "Сделать копию")],
                _ =>
                [
                    (ImportAction.Copy, "Сделать копию"), (ImportAction.Update, "Обновить моё"),
                    (ImportAction.Skip, "Пропустить")
                ]
            };
            foreach (var (action, text) in options)
                box.Items.Add(new ComboBoxItem { Content = text, Tag = action });
            box.SelectedIndex = 0;
            _actions[item.Id] = box;
            body.Children.Add(Row(title, box));
        }
    }

    private static Grid Row(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        left.VerticalAlignment = VerticalAlignment.Center;
        left.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        return block;
    }

    private static TextBlock Muted(string text)
    {
        var block = Text(text);
        block.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        return block;
    }

    private static TextBlock Header(string text)
    {
        var block = Text(text);
        block.FontSize = 15;
        block.FontWeight = FontWeights.SemiBold;
        block.Margin = new Thickness(0, 8, 0, 6);
        return block;
    }
}
