using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Views.Queries;

public partial class LibraryPanel : UserControl
{
    private const int RecentLimit = 20;
    private const int ViewAll = 0;
    private const int ViewFavorites = 1;
    private const int ViewRecent = 2;

    private readonly HashSet<string> _collapsed = new(StringComparer.CurrentCultureIgnoreCase);
    private bool _loading = true;
    private Guid? _selected;

    public LibraryPanel()
    {
        InitializeComponent();
        ViewBox.ItemsSource = new[] { "Все скрипты", "Избранное", "Недавно запущенные" };
        ViewBox.SelectedIndex = ViewAll;
    }

    public event Action<Guid>? OpenRequested;
    public event Action<Guid>? PropertiesRequested;
    public event Action<Guid>? DeleteRequested;

    public void Initialize()
    {
        App.Library.Changed += Reload;
        App.Connections.Changed += Reload;
        Reload();
        if (App.Library.LoadErrors.Count > 0)
        {
            ErrorText.Text = string.Join(Environment.NewLine, App.Library.LoadErrors);
            ErrorText.ToolTip = ErrorText.Text;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    public void Select(Guid? id)
    {
        _selected = id;
        Rebuild();
    }

    private void Reload()
    {
        _loading = true;
        var scripts = App.Library.GetAll();
        FillFilter(ConnectionFilter, "Все подключения",
            Entries(scripts).Select(e => e.ConnectionName).Where(n => n.Length > 0));
        FillFilter(TagFilter, "Все теги", scripts.SelectMany(s => s.Tags));
        _loading = false;
        Rebuild();
    }

    private static void FillFilter(ComboBox box, string all, IEnumerable<string> values)
    {
        var selected = (box.SelectedItem as ComboBoxItem)?.Tag as string;
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = all });
        box.SelectedIndex = 0;
        foreach (var value in values.Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .Order(StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ComboBoxItem { Content = value, Tag = value };
            box.Items.Add(item);
            if (string.Equals(value, selected, StringComparison.CurrentCultureIgnoreCase))
                box.SelectedItem = item;
        }
    }

    private static IEnumerable<LibraryEntry> Entries(IEnumerable<LibraryScript> scripts)
    {
        var connections = App.Connections.GetAll();
        return scripts.Select(script =>
        {
            var profile = ScriptConnection.Resolve(script, connections);
            return new LibraryEntry(script, profile?.Name ?? script.ConnectionName, profile?.Database ?? string.Empty);
        }).ToList();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!_loading)
            Rebuild();
    }

    private void Rebuild()
    {
        if (_loading)
            return;

        var library = App.Library;
        var scripts = library.GetAll();
        var found = LibrarySearch.Find(
            Entries(scripts),
            SearchBox.Text,
            (ConnectionFilter.SelectedItem as ComboBoxItem)?.Tag as string,
            (TagFilter.SelectedItem as ComboBoxItem)?.Tag as string);

        var view = ViewBox.SelectedIndex;
        if (view == ViewFavorites)
            found = found.Where(e => library.IsFavorite(e.Script.Id)).ToList();
        else if (view == ViewRecent)
            found = found.Where(e => library.LastRun(e.Script.Id) is not null)
                .OrderByDescending(e => library.LastRun(e.Script.Id))
                .Take(RecentLimit)
                .ToList();

        Tree.Items.Clear();
        // Группы дерева — только при обычном просмотре; результаты поиска идут по релевантности.
        var grouped = view == ViewAll && string.IsNullOrWhiteSpace(SearchBox.Text);
        var groups = new Dictionary<string, TreeViewItem>(StringComparer.CurrentCultureIgnoreCase);
        if (grouped)
        {
            foreach (var path in found.Select(e => e.Script.Group).Where(g => g.Length > 0)
                         .Distinct(StringComparer.CurrentCultureIgnoreCase)
                         .Order(StringComparer.CurrentCultureIgnoreCase))
                GroupNode(path, groups);
        }

        foreach (var entry in found)
        {
            var target = grouped && entry.Script.Group.Length > 0
                ? GroupNode(entry.Script.Group, groups).Items
                : Tree.Items;
            target.Add(ScriptNode(entry, showGroup: !grouped));
        }

        EmptyText.Text = scripts.Count == 0
            ? "Пока пусто. Напишите запрос и нажмите «Сохранить»."
            : found.Count == 0 ? "Ничего не найдено." : string.Empty;
    }

    private TreeViewItem GroupNode(string path, Dictionary<string, TreeViewItem> groups)
    {
        if (groups.TryGetValue(path, out var existing))
            return existing;

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? Tree.Items : GroupNode(path[..slash], groups).Items;
        var node = new TreeViewItem
        {
            Header = Row("📁 " + path[(slash + 1)..]),
            IsExpanded = !_collapsed.Contains(path)
        };
        node.Collapsed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, node))
                _collapsed.Add(path);
        };
        node.Expanded += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, node))
                _collapsed.Remove(path);
        };
        parent.Add(node);
        return groups[path] = node;
    }

    /// <summary>Строка дерева с подсветкой при наведении.</summary>
    private static Border Row(string text)
    {
        var row = new Border { Child = new TextBlock { Text = text } };
        row.SetResourceReference(StyleProperty, "LibraryRow");
        return row;
    }

    private TreeViewItem ScriptNode(LibraryEntry entry, bool showGroup)
    {
        var script = entry.Script;
        var star = App.Library.IsFavorite(script.Id) ? "★ " : string.Empty;
        var group = showGroup && script.Group.Length > 0 ? $"  · {script.Group}" : string.Empty;
        var tooltip = string.Join(Environment.NewLine, new[]
        {
            script.Description,
            entry.ConnectionName.Length > 0 ? "Подключение: " + entry.ConnectionName : string.Empty,
            script.Tags.Count > 0 ? "Теги: " + string.Join(", ", script.Tags) : string.Empty
        }.Where(line => line.Length > 0));

        var row = Row(star + script.Name + group);
        // Обработчик на самой строке: не нужно искать скрипт по элементу под курсором.
        var id = script.Id;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // После обработки щелчка: открытие перестраивает дерево и может показать вопрос.
            Dispatcher.BeginInvoke(() => OpenRequested?.Invoke(id));
        };

        return new TreeViewItem
        {
            Header = row,
            Tag = script.Id,
            ToolTip = tooltip.Length > 0 ? tooltip : null,
            IsSelected = script.Id == _selected
        };
    }

    private Guid? CurrentId => (Tree.SelectedItem as TreeViewItem)?.Tag as Guid?;

    private static TreeViewItem? ItemAt(object source)
    {
        var element = source as DependencyObject;
        while (element is not null and not TreeViewItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as TreeViewItem;
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || CurrentId is not { } id)
            return;
        e.Handled = true;
        OpenRequested?.Invoke(id);
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is { } item)
            item.IsSelected = true;
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Меню относится только к скриптам, не к группам и пустому месту.
        if (CurrentId is not { } id)
        {
            e.Handled = true;
            return;
        }

        FavoriteItem.Header = App.Library.IsFavorite(id) ? "Убрать из избранного" : "В избранное";
    }

    private void Open_Click(object sender, RoutedEventArgs e) => WithSelected(id => OpenRequested?.Invoke(id));

    private void Properties_Click(object sender, RoutedEventArgs e) => WithSelected(id => PropertiesRequested?.Invoke(id));

    private void Delete_Click(object sender, RoutedEventArgs e) => WithSelected(id => DeleteRequested?.Invoke(id));

    private void Favorite_Click(object sender, RoutedEventArgs e) =>
        WithSelected(id => App.Library.SetFavorite(id, !App.Library.IsFavorite(id)));

    private void Duplicate_Click(object sender, RoutedEventArgs e) => WithSelected(id => App.Library.Duplicate(id));

    private void WithSelected(Action<Guid> action)
    {
        if (CurrentId is not { } id)
            return;
        try
        {
            action(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Библиотека", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

internal static class ScriptConnection
{
    public static DbConnectionProfile? Resolve(LibraryScript script, IReadOnlyList<DbConnectionProfile> connections) =>
        connections.FirstOrDefault(c => c.Id == script.ConnectionId)
        ?? connections.FirstOrDefault(c => script.ConnectionName.Length > 0
                                           && string.Equals(c.Name, script.ConnectionName, StringComparison.OrdinalIgnoreCase));
}
