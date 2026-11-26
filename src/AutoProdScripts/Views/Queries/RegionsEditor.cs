using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Regions;

namespace AutoProdScripts.Views.Queries;

/// <summary>Справочник регионов, сгруппированный по кластерам.</summary>
public sealed class RegionsEditor : UserControl
{
    private readonly StackPanel _groups = new();
    private List<Region> _items = new();
    private readonly TextBox _paste = new()
    {
        AcceptsReturn = true,
        Height = 100,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private string _baseline = string.Empty;

    public RegionsEditor()
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "FgInfoBrush");

        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(_groups);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("Сохранить регионы", secondary: false, () => Save()));
        var add = Button("＋ Регион", secondary: true, () => Add(new Region()));
        add.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(add);
        panel.Children.Add(buttons);
        panel.Children.Add(_status);

        var title = new TextBlock { Text = "Добавить списком", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 4) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        panel.Children.Add(title);
        panel.Children.Add(Muted("«Имя кластера:», ниже коды. Или «код; кластер»."));
        panel.Children.Add(_paste);
        var parse = Button("Добавить из списка", secondary: true, ImportList);
        parse.HorizontalAlignment = HorizontalAlignment.Left;
        parse.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(parse);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };

        Reload();
        if (App.Regions.LoadError is { } error)
            _status.Text = error;
        Loaded += (_, _) =>
        {
            App.Connections.Changed -= ConnectionsChanged;
            App.Connections.Changed += ConnectionsChanged;
            ConnectionsChanged();
        };
        Unloaded += (_, _) => App.Connections.Changed -= ConnectionsChanged;
    }

    private bool HasUnsavedChanges => Snapshot() != _baseline;

    /// <returns>false — пользователь решил остаться и сохранить.</returns>
    public bool ConfirmDiscard()
    {
        if (!HasUnsavedChanges)
            return true;

        if (MessageBox.Show(Window.GetWindow(this), "Изменения регионов не сохранены. Отбросить их?", "Регионы",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        Reload();
        return true;
    }

    private void Reload()
    {
        _items = App.Regions.GetAll().ToList();
        _baseline = Snapshot();
        Render();
    }

    // Подключения могли переименовать или добавить: правки при этом не теряются.
    private void ConnectionsChanged() => Render();

    private void Add(Region region)
    {
        _items.Add(region);
        Render();
    }

    private void Render()
    {
        var connections = App.Connections.GetAll();
        // Ключ группы — подключение, а для отсутствующих на этом ПК — его имя.
        string Key(Region r) => ScriptConnection.Resolve(r.ConnectionId, r.ConnectionName, connections)?.Name ?? r.ConnectionName;

        // Пустые кластеры тоже показываем: в них удобно добавлять.
        var names = connections.Where(c => c.IsClusterLevel).Select(c => c.Name)
            .Concat(_items.Select(Key))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (_items.Any(r => Key(r).Length == 0))
            names.Add(string.Empty);

        _groups.Children.Clear();
        if (names.Count == 0)
            _groups.Children.Add(Muted("Сначала добавьте подключение к кластеру."));

        else
            _groups.Children.Add(ColumnTitles());

        foreach (var name in names)
        {
            var regions = _items.Where(r => string.Equals(Key(r), name, StringComparison.OrdinalIgnoreCase)).ToList();
            var profile = connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            _groups.Children.Add(Header(name, profile, regions.Count));
            foreach (var region in regions)
                _groups.Children.Add(RegionRow(region, connections));
        }
    }

    // Подписи колонок один раз над всеми группами: пустое поле названия иначе непонятно.
    private static Grid ColumnTitles()
    {
        var grid = Cells(Muted("Код"), Muted("Название"), Muted("Кластер"), new TextBlock());
        grid.Margin = new Thickness(14, 0, 0, 0);
        return grid;
    }

    private static Grid Cells(params FrameworkElement[] cells)
    {
        GridLength[] widths = [new(1, GridUnitType.Star), new(1.6, GridUnitType.Star), new(1.6, GridUnitType.Star), new(36)];
        var grid = new Grid();
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = widths[i] });
            cells[i].Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }

        return grid;
    }

    private FrameworkElement Header(string name, DbConnectionProfile? cluster, int count)
    {
        var title = name.Length == 0 ? "Без кластера"
            : cluster is null ? $"{name} (нет такого подключения)"
            : name;
        var text = new TextBlock
        {
            Text = $"{title}  ·  регионов: {count}",
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = cluster?.Server
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");

        var row = new DockPanel();
        if (name.Length > 0)
        {
            var add = Button("＋", secondary: true, () => Add(new Region { ConnectionId = cluster?.Id, ConnectionName = name }));
            add.ToolTip = "Добавить регион в этот кластер";
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);
        }

        row.Children.Add(text);
        var band = new Border
        {
            Child = row,
            Padding = new Thickness(10, 3, 4, 3),
            Margin = new Thickness(0, 10, 0, 4),
            BorderThickness = new Thickness(4, 0, 0, 0),
            MinHeight = 30
        };
        band.SetResourceReference(Border.BackgroundProperty, "GroupRowBrush");
        band.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        return band;
    }

    private FrameworkElement RegionRow(Region region, IReadOnlyList<DbConnectionProfile> connections)
    {
        var code = new TextBox { Text = region.Code, ToolTip = "Код, например ast" };
        code.TextChanged += (_, _) => region.Code = code.Text.Trim();
        var name = new TextBox { Text = region.Name, ToolTip = "Название, необязательно" };
        name.TextChanged += (_, _) => region.Name = name.Text.Trim();

        // Перенос в другой кластер.
        var cluster = new ComboBox { ToolTip = "Кластер" };
        var current = ScriptConnection.Resolve(region.ConnectionId, region.ConnectionName, connections);
        foreach (var profile in connections.OrderByDescending(c => c.IsClusterLevel))
        {
            var item = new ComboBoxItem { Content = EnvironmentUi.Option(profile), Tag = profile };
            cluster.Items.Add(item);
            if (profile.Id == current?.Id)
                cluster.SelectedItem = item;
        }

        cluster.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            if ((cluster.SelectedItem as ComboBoxItem)?.Tag is not DbConnectionProfile profile)
                return;
            (region.ConnectionId, region.ConnectionName) = (profile.Id, profile.Name);
            // Строка переезжает в другую группу; перестраиваем после закрытия списка.
            Dispatcher.BeginInvoke(Render);
        };

        var remove = Button("✕", secondary: true, () =>
        {
            _items.Remove(region);
            Render();
        });

        var grid = Cells(code, name, cluster, remove);
        grid.Margin = new Thickness(14, 2, 0, 2);
        return grid;
    }

    // Только что добавленная и не заполненная строка не сохраняется.
    private List<Region> Collect() => _items.Where(r => r.Code.Length > 0 || r.Name.Length > 0).Select(r => r.Clone()).ToList();

    private string Snapshot() => JsonSerializer.Serialize(Collect());

    public bool Save()
    {
        try
        {
            App.Regions.Save(Collect());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
            return false;
        }

        Reload();
        _status.Text = $"Сохранено регионов: {_items.Count}.";
        return true;
    }

    private void ImportList()
    {
        var parsed = RegionStore.ParseList(_paste.Text);
        var connections = App.Connections.GetAll();
        var unknown = new List<string>();
        foreach (var region in parsed.Regions)
        {
            if (ScriptConnection.Resolve(null, region.ConnectionName, connections) is { } profile)
                (region.ConnectionId, region.ConnectionName) = (profile.Id, profile.Name);
            else
                unknown.Add(region.ConnectionName);

            // Регион с тем же кодом обновляется; название не стирается, если в списке его нет.
            var old = _items.FirstOrDefault(r => string.Equals(r.Code, region.Code, StringComparison.OrdinalIgnoreCase));
            if (region.Name.Length == 0 && old is not null)
                region.Name = old.Name;
            if (old is not null)
                _items.Remove(old);
            _items.Add(region);
        }

        Render();
        if (parsed.Errors.Count == 0)
            _paste.Clear();

        var messages = new List<string> { $"Добавлено из списка: {parsed.Regions.Count}. Проверьте и сохраните." };
        if (unknown.Count > 0)
            messages.Add("Нет таких подключений: " + string.Join(", ", unknown.Distinct()) + ".");
        messages.AddRange(parsed.Errors);
        _status.Text = string.Join(Environment.NewLine, messages);
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        return block;
    }

    private static Button Button(string text, bool secondary, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0) };
        if (secondary)
            button.SetResourceReference(StyleProperty, "SecondaryButton");
        button.Click += (_, _) => click();
        return button;
    }
}
