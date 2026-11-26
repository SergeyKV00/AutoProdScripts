using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Sql;

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
    public event Action? NewChainRequested;

    private void NewChain_Click(object sender, RoutedEventArgs e) => NewChainRequested?.Invoke();

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
        var entries = Entries();
        FillFilter(ConnectionFilter, "Все подключения", entries.SelectMany(e => e.Connections));
        FillFilter(TagFilter, "Все теги", entries.SelectMany(e => e.Item.Tags));
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

    private static List<LibraryEntry> Entries()
    {
        var connections = App.Connections.GetAll();
        var scripts = App.Library.GetAll();
        var entries = scripts.Select(script =>
        {
            var profile = ScriptConnection.Resolve(script, connections);
            var regional = script.RegionDatabase.Length > 0;
            return new LibraryEntry(
                script,
                Named(regional ? RegionTargets.Title : profile?.Name ?? script.ConnectionName),
                Named(regional ? script.RegionDatabase : profile?.Database),
                script.Sql,
                string.Join('\n', script.Parameters.Select(p => p.Label)));
        }).ToList();

        var byId = scripts.ToDictionary(s => s.Id);
        entries.AddRange(App.Library.GetChains().Select(chain =>
        {
            var steps = chain.Steps
                .Select(s => (Step: s, Script: s.Inline ?? byId.GetValueOrDefault(s.ScriptId)))
                .Where(x => x.Script is not null)
                .Select(x => (x.Step, Script: x.Script!))
                .ToList();
            var profiles = steps.Select(x =>
                ScriptConnection.Resolve(x.Step.ConnectionId, x.Step.ConnectionName, connections)
                ?? ScriptConnection.Resolve(x.Script, connections)).OfType<DbConnectionProfile>().ToList();
            var templates = steps.Select(x => x.Step.EffectiveRegionDatabase(x.Script)).Where(t => t.Length > 0).Distinct().ToList();
            return new LibraryEntry(
                chain,
                profiles.Select(p => p.Name).Concat(templates.Count > 0 ? [RegionTargets.Title] : []).Distinct().ToList(),
                profiles.Select(p => p.Database).Concat(templates).Distinct().ToList(),
                string.Join('\n', steps.Select(x => x.Script.Sql)),
                string.Join('\n', steps.Select(x => x.Script.Name).Concat(chain.Parameters.Select(p => p.Label))));
        }));
        return entries;
    }

    private static string[] Named(string? value) => string.IsNullOrEmpty(value) ? [] : [value];

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
        var all = Entries();
        var found = LibrarySearch.Find(
            all,
            SearchBox.Text,
            (ConnectionFilter.SelectedItem as ComboBoxItem)?.Tag as string,
            (TagFilter.SelectedItem as ComboBoxItem)?.Tag as string);

        var view = ViewBox.SelectedIndex;
        if (view == ViewFavorites)
            found = found.Where(e => library.IsFavorite(e.Item.Id)).ToList();
        else if (view == ViewRecent)
            found = found.Where(e => library.LastRun(e.Item.Id) is not null)
                .OrderByDescending(e => library.LastRun(e.Item.Id))
                .Take(RecentLimit)
                .ToList();

        Tree.Items.Clear();
        // Группы дерева — только при обычном просмотре; результаты поиска идут по релевантности.
        var grouped = view == ViewAll && string.IsNullOrWhiteSpace(SearchBox.Text);
        var groups = new Dictionary<string, TreeViewItem>(StringComparer.CurrentCultureIgnoreCase);
        if (grouped)
        {
            foreach (var path in found.Select(e => e.Item.Group).Where(g => g.Length > 0)
                         .Distinct(StringComparer.CurrentCultureIgnoreCase)
                         .Order(StringComparer.CurrentCultureIgnoreCase))
                GroupNode(path, groups);
        }

        foreach (var entry in found)
        {
            var target = grouped && entry.Item.Group.Length > 0
                ? GroupNode(entry.Item.Group, groups).Items
                : Tree.Items;
            target.Add(ItemNode(entry, showGroup: !grouped));
        }

        EmptyText.Text = all.Count == 0
            ? "Пока пусто."
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
            IsExpanded = !_collapsed.Contains(path),
            Tag = path
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

    private TreeViewItem ItemNode(LibraryEntry entry, bool showGroup)
    {
        var item = entry.Item;
        var chain = item is LibraryChain ? "🔗 " : string.Empty;
        var star = App.Library.IsFavorite(item.Id) ? "★ " : string.Empty;
        var group = showGroup && item.Group.Length > 0 ? $"  · {item.Group}" : string.Empty;
        var tooltip = string.Join(Environment.NewLine, new[]
        {
            item.Description,
            entry.Connections.Count > 0 ? "Подключения: " + string.Join(", ", entry.Connections) : string.Empty,
            item.Tags.Count > 0 ? "Теги: " + string.Join(", ", item.Tags) : string.Empty
        }.Where(line => line.Length > 0));

        var row = Row(star + chain + item.Name + group);
        // Обработчик на самой строке: не нужно искать запись по элементу под курсором.
        var id = item.Id;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // После обработки щелчка: открытие перестраивает дерево и может показать вопрос.
            Dispatcher.BeginInvoke(() => OpenRequested?.Invoke(id));
        };

        return new TreeViewItem
        {
            Header = row,
            Tag = id,
            ToolTip = tooltip.Length > 0 ? tooltip : null,
            IsSelected = id == _selected
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
        // У группы — только экспорт; на пустом месте меню нет.
        var id = CurrentId;
        if (id is null && CurrentGroup is null)
        {
            e.Handled = true;
            return;
        }

        foreach (var item in new FrameworkElement[]
                     { OpenMenu, PropertiesMenu, FavoriteItem, DuplicateMenu, ExportMenu, DeleteSeparator, DeleteMenu })
            item.Visibility = id is null ? Visibility.Collapsed : Visibility.Visible;
        ExportGroupMenu.Visibility = id is null ? Visibility.Visible : Visibility.Collapsed;
        if (id is { } selected)
            FavoriteItem.Header = App.Library.IsFavorite(selected) ? "Убрать из избранного" : "В избранное";
    }

    private void Open_Click(object sender, RoutedEventArgs e) => WithSelected(id => OpenRequested?.Invoke(id));

    private void Properties_Click(object sender, RoutedEventArgs e) => WithSelected(id => PropertiesRequested?.Invoke(id));

    private void Delete_Click(object sender, RoutedEventArgs e) => WithSelected(id => DeleteRequested?.Invoke(id));

    private void Favorite_Click(object sender, RoutedEventArgs e) =>
        WithSelected(id => App.Library.SetFavorite(id, !App.Library.IsFavorite(id)));

    private void Duplicate_Click(object sender, RoutedEventArgs e) => WithSelected(id =>
    {
        if (App.Library.FindChain(id) is not null)
            App.Library.DuplicateChain(id);
        else
            App.Library.Duplicate(id);
    });

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
        Resolve(script.ConnectionId, script.ConnectionName, connections);

    /// <summary>По Id, а для перенесённых с другого ПК — по имени.</summary>
    public static DbConnectionProfile? Resolve(Guid? id, string name, IReadOnlyList<DbConnectionProfile> connections) =>
        connections.FirstOrDefault(c => c.Id == id)
        ?? connections.FirstOrDefault(c => name.Length > 0
                                           && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
