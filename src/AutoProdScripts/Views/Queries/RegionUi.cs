using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Regions;

namespace AutoProdScripts.Views.Queries;

/// <summary>Выбор регионов для запуска: кнопка и выпадающий список с галочками.</summary>
public sealed class RegionPicker : UserControl
{
    private readonly ToggleButton _button = new() { Width = 190 };
    private readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly StackPanel _list = new();
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _caption = new();

    public RegionPicker()
    {
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        _button.Content = _caption;
        _button.SetResourceReference(StyleProperty, "BranchPickerButton");
        _button.Checked += (_, _) => _popup.IsOpen = true;
        _button.Unchecked += (_, _) => _popup.IsOpen = false;
        _popup.Closed += (_, _) => _button.IsChecked = false;
        _popup.PlacementTarget = _button;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        tools.Children.Add(Tool("Все", () => SetAll(true)));
        tools.Children.Add(Tool("Снять все", () => SetAll(false)));

        var body = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        body.Children.Add(tools);
        body.Children.Add(new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        var border = new Border { Child = body, Padding = new Thickness(10), BorderThickness = new Thickness(1), MinWidth = 220 };
        border.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _popup.Child = border;

        var root = new Grid();
        root.Children.Add(_button);
        root.Children.Add(_popup);
        Content = root;
        UpdateCaption();
    }

    public event Action? SelectionChanged;

    /// <summary>Отмеченные коды в порядке справочника.</summary>
    public IReadOnlyList<string> Selected =>
        _list.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

    /// <summary>Отметить ровно эти регионы (у каждой вкладки свой выбор). Событие не вызывается.</summary>
    public void SetSelected(IEnumerable<string> codes)
    {
        _selected.Clear();
        _selected.UnionWith(codes);
        foreach (var box in _list.Children.OfType<CheckBox>())
            box.IsChecked = _selected.Contains((string)box.Tag);
        UpdateCaption();
    }

    public void Reload()
    {
        _list.Children.Clear();
        foreach (var region in App.Regions.GetAll())
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = region.Title },
                Tag = region.Code,
                IsChecked = _selected.Contains(region.Code),
                Margin = new Thickness(0, 0, 0, 6)
            };
            box.Click += (_, _) => Changed();
            _list.Children.Add(box);
        }

        if (_list.Children.Count == 0)
        {
            var empty = new TextBlock { Text = "Регионы не настроены: «Подключения…» → «Регионы».", TextWrapping = TextWrapping.Wrap, MaxWidth = 240 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
            _list.Children.Add(empty);
        }

        UpdateCaption();
    }

    private void SetAll(bool value)
    {
        foreach (var box in _list.Children.OfType<CheckBox>())
            box.IsChecked = value;
        Changed();
    }

    private void Changed()
    {
        _selected.Clear();
        _selected.UnionWith(Selected);
        UpdateCaption();
        SelectionChanged?.Invoke();
    }

    private void UpdateCaption()
    {
        var all = _list.Children.OfType<CheckBox>().Count();
        var chosen = Selected.Count;
        _caption.Text = chosen == 0 ? "Выберите регионы ▾" : $"Регионы: {chosen} из {all} ▾";
    }

    private static Button Tool(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        button.Click += (_, _) => click();
        return button;
    }
}

internal static class RegionTargets
{
    public const string Title = "По регионам";

    /// <summary>Подключение к базе региона на его кластере.</summary>
    public static (RegionTarget Target, DbConnectionProfile? Profile) Build(string code, string template)
    {
        RegionTarget Failed(string message) => new(code, string.Empty, string.Empty, 30, message);

        var region = App.Regions.GetAll().FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        if (region is null)
            return (Failed("Нет такого региона в справочнике."), null);
        var cluster = ScriptConnection.Resolve(region.ConnectionId, region.ConnectionName, App.Connections.GetAll());
        if (cluster is null)
            return (Failed($"Нет подключения «{region.ConnectionName}»."), null);
        var password = cluster.AuthMode == DbAuthMode.SqlLogin ? App.Connections.LoadPassword(cluster.Id) : null;
        if (cluster.AuthMode == DbAuthMode.SqlLogin && password is null)
            return (Failed($"Для подключения «{cluster.Name}» не сохранён пароль."), cluster);

        try
        {
            var profile = cluster.Clone();
            profile.Database = RegionDatabase.Resolve(template, region.Code);
            return (new RegionTarget(region.Code, ConnectionStringFactory.Build(profile, password), profile.Server,
                profile.CommandTimeoutSeconds), profile);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
        {
            return (Failed(ex.Message), cluster);
        }
    }
}
