using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AutoProdScripts.Models;
using AutoProdScripts.Services.Mapping;
using AutoProdScripts.Themes;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AutoProdScripts.Views;

public partial class QuickAddDialog : Window
{
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x7D, 0xCE, 0xA0));
    private static readonly Brush WarnBrush = Freeze(Color.FromRgb(0xD7, 0xBA, 0x7D));
    private static readonly Brush ErrorBrush = Freeze(Color.FromRgb(0xE0, 0x7A, 0x7A));

    private readonly UseFolderMapping _mapping;
    private readonly string _projectRoot;
    private readonly IReadOnlyList<string> _folders;
    private readonly List<QuickScriptRow> _rows = new();
    private readonly DispatcherTimer _parseTimer;
    private readonly Dictionary<Guid, TreeViewItem> _nodes = new();
    private int _tempIndex;
    private bool _syncing;
    private bool _editing;

    public bool CreatePullRequest { get; private set; }
    public IReadOnlyList<QuickScriptPlan> Plans { get; private set; } = Array.Empty<QuickScriptPlan>();

    public QuickAddDialog(UseFolderMapping mapping, string? mappingError, string projectRoot, IReadOnlyList<string> folders)
    {
        InitializeComponent();
        SqlBox.SyntaxHighlighting = SqlHighlightingLoader.GetDefinition()
                                    ?? HighlightingManager.Instance.GetDefinition("TSQL")
                                    ?? HighlightingManager.Instance.GetDefinition("SQL");
        _mapping = mapping;
        _projectRoot = projectRoot;
        _folders = folders;
        _parseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _parseTimer.Tick += (_, _) =>
        {
            _parseTimer.Stop();
            ApplySql();
        };
        MappingStatus.Text = mappingError is null
            ? $"Mapping: {mapping.FilePath} · правил: {mapping.Mappings.Count}"
            : $"Ошибка mapping: {mappingError}. Папку можно выбрать вручную.";
        if (mappingError is not null)
            MappingStatus.Foreground = ErrorBrush;
        AddRow();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddRow();

    private void AddRow()
    {
        var row = new QuickScriptRow { TempName = NextTempName(), FileName = string.Empty };
        row.FileName = row.TempName;
        Resolve(row);
        _rows.Add(row);
        ScriptList.Items.Add(row);
        ScriptList.SelectedItem = row;
        RebuildTree();
        SqlBox.Focus();
    }

    private string NextTempName()
    {
        while (true)
        {
            var name = $"temp_{++_tempIndex}.sql";
            if (_rows.All(row => !row.TempName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ScriptList.SelectedItem is not QuickScriptRow row)
            return;
        var index = _rows.IndexOf(row);
        _rows.Remove(row);
        ScriptList.Items.Remove(row);
        if (_rows.Count == 0)
            AddRow();
        else
            ScriptList.SelectedIndex = Math.Min(index, _rows.Count - 1);
        RebuildTree();
    }

    private void ScriptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        ShowSelected();
        RevealSelected();
    }

    private void ShowSelected()
    {
        _editing = true;
        try
        {
            if (ScriptList.SelectedItem is not QuickScriptRow row)
            {
                SqlBox.Text = string.Empty;
                SqlBox.IsEnabled = false;
                return;
            }

            SqlBox.IsEnabled = true;
            SqlBox.Text = row.Sql;
        }
        finally
        {
            _editing = false;
        }
    }

    private void SqlBox_TextChanged(object sender, EventArgs e)
    {
        if (_editing)
            return;
        _parseTimer.Stop();
        _parseTimer.Start();
    }

    private void ApplySql()
    {
        if (ScriptList.SelectedItem is not QuickScriptRow row)
            return;
        row.Sql = SqlBox.Text;
        Resolve(row);
        RefreshCaptions();
        RebuildTree();
    }

    private void Resolve(QuickScriptRow row)
    {
        row.UseName = UseStatementParser.TryExtract(row.Sql);
        row.AutoFolder = null;
        if (string.IsNullOrWhiteSpace(row.FileName) || !IsSafeFileName(row.FileName))
        {
            row.Status = QuickScriptStatus.BadName;
            return;
        }

        if (string.IsNullOrWhiteSpace(row.UseName))
        {
            row.Status = row.ManualFolder is null ? QuickScriptStatus.NoUse : QuickScriptStatus.Manual;
        }
        else if (_mapping.TryGetFolder(row.UseName, out var folder))
        {
            row.AutoFolder = folder;
            row.Status = row.ManualFolder is null ? QuickScriptStatus.Ok : QuickScriptStatus.Manual;
        }
        else if (row.ManualFolder is not null)
        {
            row.Status = QuickScriptStatus.Manual;
        }
        else if (_mapping.OnMissingMapping == "createFolder")
        {
            row.AutoFolder = row.UseName;
            row.Status = QuickScriptStatus.Ok;
        }
        else
        {
            row.Status = _mapping.OnMissingMapping == "skip"
                ? QuickScriptStatus.Skip
                : QuickScriptStatus.NoMapping;
        }

        if (row.Status is QuickScriptStatus.Ok or QuickScriptStatus.Manual && !FolderExists(row.EffectiveFolder))
            row.Status = QuickScriptStatus.MissingFolder;

        ApplyFileName(row);
    }

    private void ApplyFileName(QuickScriptRow row)
    {
        var placed = row.EffectiveFolder is not null &&
                     row.Status is QuickScriptStatus.Ok or QuickScriptStatus.Manual or QuickScriptStatus.MissingFolder;
        if (!placed)
        {
            row.StampedFolder = null;
            if (!row.NameLocked)
                row.FileName = row.TempName;
            return;
        }

        if (row.NameLocked)
            return;
        if (row.StampedFolder is not null &&
            row.StampedFolder.Equals(row.EffectiveFolder, StringComparison.OrdinalIgnoreCase) &&
            IsTimestampName(row.FileName))
            return;

        row.FileName = UniqueTimestamp(row);
        row.StampedFolder = row.EffectiveFolder;
    }

    private string UniqueTimestamp(QuickScriptRow row)
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmm");
        var name = stamp + ".sql";
        var index = 2;
        while (_rows.Any(other => !ReferenceEquals(other, row) &&
                                  string.Equals(other.EffectiveFolder, row.EffectiveFolder, StringComparison.OrdinalIgnoreCase) &&
                                  other.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{stamp}_{index++}.sql";
        return name;
    }

    private static bool IsTimestampName(string name)
    {
        var file = Path.GetFileNameWithoutExtension(name);
        var split = file.IndexOf('_');
        var stamp = split < 0 ? file : file[..split];
        return stamp.Length == 12 && stamp.All(char.IsDigit);
    }

    private bool FolderExists(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(_projectRoot))
            return false;
        try
        {
            var full = Path.GetFullPath(Path.Combine(_projectRoot, folder.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(_projectRoot);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full);
        }
        catch
        {
            return false;
        }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRow() is not QuickScriptRow row)
            return;
        PickFolder(row);
    }

    private QuickScriptRow? CurrentRow()
    {
        if (ScriptList.SelectedItem is QuickScriptRow selected)
            return selected;
        if (TargetTree.SelectedItem is TreeViewItem { Tag: Guid id })
            return _rows.FirstOrDefault(item => item.Id == id);
        return null;
    }

    private void PickFolder(QuickScriptRow row)
    {
        var dialog = new FolderPickDialog(_folders, row.EffectiveFolder) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedFolder))
            return;
        row.ManualFolder = dialog.SelectedFolder;
        Resolve(row);
        ShowSelected();
        RefreshCaptions();
        RebuildTree();
    }

    private void Rename_Click(QuickScriptRow row)
    {
        var name = PromptFileName(row.FileName);
        if (name is null)
            return;
        if (!IsSafeFileName(name))
        {
            MessageBox.Show(this, "Имя файла не подходит.", "Быстрое добавление",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        row.FileName = EnsureSql(name);
        row.NameLocked = true;
        RefreshCaptions();
        RebuildTree();
    }

    private string? PromptFileName(string current)
    {
        var box = new TextBox { Text = current, Margin = new Thickness(12, 12, 12, 0), MinWidth = 280 };
        var withoutExt = Path.GetFileNameWithoutExtension(current);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.Select(0, Math.Min(withoutExt.Length, box.Text.Length));
        };
        var ok = new Button { Content = "Ок", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", Width = 90, IsCancel = true, Style = (Style)FindResource("SecondaryButton") };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(box);
        var window = new Window
        {
            Title = "Имя файла",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = this,
            Background = Background,
            Foreground = Foreground,
            ShowInTaskbar = false
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text.Trim();
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? result : null;
    }

    private void ResetFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ScriptList.SelectedItem is not QuickScriptRow row)
            return;
        row.ManualFolder = null;
        Resolve(row);
        ShowSelected();
        RefreshCaptions();
        RebuildTree();
    }

    private void TargetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncing || TargetTree.SelectedItem is not TreeViewItem { Tag: Guid id })
            return;
        var row = _rows.FirstOrDefault(item => item.Id == id);
        if (row is null || ReferenceEquals(ScriptList.SelectedItem, row))
            return;
        _syncing = true;
        ScriptList.SelectedItem = row;
        _syncing = false;
        ShowSelected();
    }

    private void RebuildTree()
    {
        var selected = (ScriptList.SelectedItem as QuickScriptRow)?.Id;
        _nodes.Clear();
        TargetTree.Items.Clear();
        var roots = new List<TargetNode>();
        foreach (var row in _rows)
        {
            var folder = row.EffectiveFolder;
            var parts = string.IsNullOrWhiteSpace(folder)
                ? new[] { "без папки" }
                : folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var list = roots;
            TargetNode? current = null;
            foreach (var part in parts)
            {
                current = list.FirstOrDefault(node => node.IsFolder && node.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                {
                    current = new TargetNode { Name = part, IsFolder = true };
                    list.Add(current);
                }

                list = current.Children;
            }

            list.Add(new TargetNode
            {
                Name = string.IsNullOrWhiteSpace(row.FileName) ? "(без имени)" : row.FileName,
                IsFolder = false,
                Row = row
            });
        }

        foreach (var node in roots)
            TargetTree.Items.Add(CreateItem(node));
        if (selected is Guid id && _nodes.TryGetValue(id, out var item))
        {
            var focused = Keyboard.FocusedElement;
            _syncing = true;
            item.IsSelected = true;
            item.BringIntoView();
            _syncing = false;
            if (focused is IInputElement input)
                input.Focus();
        }
    }

    private TreeViewItem CreateItem(TargetNode node)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = node.IsFolder ? "📁" : "📄",
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = node.Name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (node.Row is QuickScriptRow script)
        {
            header.Children.Add(new TextBlock
            {
                Text = script.StatusText,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushFor(script.Status)
            });
            var rename = new Button
            {
                Content = new System.Windows.Shapes.Path
                {
                    Width = 13,
                    Height = 13,
                    Stretch = Stretch.Uniform,
                    Fill = (Brush)FindResource("FgBrush"),
                    Data = Geometry.Parse("M3,17.25 L3,21 L6.75,21 L17.81,9.94 L14.06,6.19 Z M20.71,7.04 C21.1,6.65 21.1,6.02 20.71,5.63 L18.37,3.29 C17.98,2.9 17.35,2.9 16.96,3.29 L15.13,5.12 L18.88,8.87 Z")
                },
                Width = 26,
                Height = 26,
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)FindResource("IconButton"),
                ToolTip = "Переименовать файл"
            };
            rename.Click += (_, e) =>
            {
                e.Handled = true;
                Rename_Click(script);
            };
            header.Children.Add(rename);
        }

        var item = new TreeViewItem
        {
            Header = header,
            IsExpanded = true,
            Tag = node.Row?.Id
        };
        if (node.Row is not null)
            _nodes[node.Row.Id] = item;
        foreach (var child in node.Children)
            item.Items.Add(CreateItem(child));
        return item;
    }

    private void RevealSelected()
    {
        if (ScriptList.SelectedItem is not QuickScriptRow row || !_nodes.TryGetValue(row.Id, out var item))
            return;
        _syncing = true;
        item.IsSelected = true;
        item.BringIntoView();
        _syncing = false;
    }

    private void RefreshCaptions() => ScriptList.Items.Refresh();

    private void Save_Click(object sender, RoutedEventArgs e) => Accept(createPr: false);

    private void AutoPr_Click(object sender, RoutedEventArgs e) => Accept(createPr: true);

    private void Accept(bool createPr)
    {
        ApplySql();
        foreach (var row in _rows)
            Resolve(row);
        RefreshCaptions();
        RebuildTree();

        var problems = _rows.Where(row => row.EffectiveFolder is null || row.Status is QuickScriptStatus.NoUse
            or QuickScriptStatus.BadName or QuickScriptStatus.Skip or QuickScriptStatus.NoMapping).ToList();
        if (_rows.Count == 0 || problems.Count > 0)
        {
            MessageBox.Show(this,
                "У каждого скрипта должны быть имя и папка. Если USE нет или для него нет mapping, выберите папку вручную.",
                "Быстрое добавление", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var duplicate = _rows
            .GroupBy(row => row.EffectiveFolder + "/" + row.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            MessageBox.Show(this, $"Повторяется файл: {duplicate.Key}", "Быстрое добавление",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Plans = _rows.Select(row => new QuickScriptPlan
        {
            FileName = EnsureSql(row.FileName),
            RelativeFolder = row.EffectiveFolder!,
            Sql = row.Sql ?? string.Empty
        }).ToList();
        CreatePullRequest = createPr;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OpenMapping_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_mapping.FilePath);
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        if (!File.Exists(_mapping.FilePath))
            UseFolderMappingFile.Save(_mapping);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private static bool IsSafeFileName(string name)
    {
        var file = name.Trim();
        if (string.IsNullOrWhiteSpace(file) || file.Equals(".sql", StringComparison.OrdinalIgnoreCase))
            return false;
        return file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !file.Contains('/') && !file.Contains('\\');
    }

    private static string EnsureSql(string name)
    {
        var file = name.Trim();
        return file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? file : file + ".sql";
    }

    private static Brush BrushFor(QuickScriptStatus status) => status switch
    {
        QuickScriptStatus.Ok or QuickScriptStatus.Manual => OkBrush,
        QuickScriptStatus.NoMapping or QuickScriptStatus.MissingFolder => WarnBrush,
        _ => ErrorBrush
    };

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class QuickScriptRow
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string TempName { get; set; } = "temp_1.sql";
        public string FileName { get; set; } = "temp_1.sql";
        public bool NameLocked { get; set; }
        public string? StampedFolder { get; set; }
        public string Sql { get; set; } = string.Empty;
        public string? UseName { get; set; }
        public string? AutoFolder { get; set; }
        public string? ManualFolder { get; set; }
        public QuickScriptStatus Status { get; set; }
        public string? EffectiveFolder => ManualFolder ?? AutoFolder;
        public string StatusText => Status switch
        {
            QuickScriptStatus.Ok => "новый файл",
            QuickScriptStatus.Manual => "папка вручную",
            QuickScriptStatus.MissingFolder => "нет папки",
            QuickScriptStatus.NoUse => "нет USE",
            QuickScriptStatus.NoMapping => "нет mapping",
            QuickScriptStatus.Skip => "пропуск",
            _ => "ошибка имени"
        };
        public string Caption =>
            string.IsNullOrWhiteSpace(EffectiveFolder) ? $"{FileName} — {StatusText}" : $"{FileName} — {EffectiveFolder}";
    }

    private enum QuickScriptStatus
    {
        Ok,
        Manual,
        MissingFolder,
        NoUse,
        NoMapping,
        Skip,
        BadName
    }

    private sealed class TargetNode
    {
        public string Name { get; init; } = string.Empty;
        public bool IsFolder { get; init; }
        public QuickScriptRow? Row { get; init; }
        public List<TargetNode> Children { get; } = new();
    }
}
