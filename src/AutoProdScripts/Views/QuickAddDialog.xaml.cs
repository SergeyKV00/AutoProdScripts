using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AutoProdScripts.Models;
using AutoProdScripts.Services.Mapping;

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
    private bool _syncing;
    private bool _editing;

    public bool CreatePullRequest { get; private set; }
    public IReadOnlyList<QuickScriptPlan> Plans { get; private set; } = Array.Empty<QuickScriptPlan>();

    public QuickAddDialog(UseFolderMapping mapping, string? mappingError, string projectRoot, IReadOnlyList<string> folders)
    {
        InitializeComponent();
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
            ? $"Mapping: {mapping.FilePath} · правил: {mapping.Mappings.Count} · если USE не найден: {mapping.OnMissingMapping}"
            : $"Ошибка mapping: {mappingError}. Папку можно выбрать вручную.";
        if (mappingError is not null)
            MappingStatus.Foreground = ErrorBrush;
        AddRow();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddRow();

    private void AddRow()
    {
        var row = new QuickScriptRow();
        Resolve(row);
        _rows.Add(row);
        ScriptList.Items.Add(row);
        ScriptList.SelectedItem = row;
        RebuildTree();
        FileNameBox.Focus();
        FileNameBox.Select(0, Math.Min(8, FileNameBox.Text.Length));
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
                FileNameBox.Text = string.Empty;
                UseBox.Text = string.Empty;
                SqlBox.Text = string.Empty;
                StatusText.Text = string.Empty;
                FileNameBox.IsEnabled = false;
                SqlBox.IsEnabled = false;
                return;
            }

            FileNameBox.IsEnabled = true;
            SqlBox.IsEnabled = true;
            FileNameBox.Text = row.FileName;
            SqlBox.Text = row.Sql;
            UseBox.Text = string.IsNullOrWhiteSpace(row.UseName) ? "—" : row.UseName;
            StatusText.Text = row.StatusText;
            StatusText.Foreground = BrushFor(row.Status);
        }
        finally
        {
            _editing = false;
        }
    }

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing || ScriptList.SelectedItem is not QuickScriptRow row)
            return;
        row.FileName = FileNameBox.Text;
        Resolve(row);
        RefreshCaptions();
        RebuildTree();
    }

    private void SqlBox_TextChanged(object sender, TextChangedEventArgs e)
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
        UseBox.Text = string.IsNullOrWhiteSpace(row.UseName) ? "—" : row.UseName;
        StatusText.Text = row.StatusText;
        StatusText.Foreground = BrushFor(row.Status);
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
            row.Status = QuickScriptStatus.NoUse;
            return;
        }

        if (_mapping.TryGetFolder(row.UseName, out var folder))
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
        if (ScriptList.SelectedItem is not QuickScriptRow row)
            return;
        var dialog = new FolderPickDialog(_folders, row.EffectiveFolder) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedFolder))
            return;
        row.ManualFolder = dialog.SelectedFolder;
        Resolve(row);
        ShowSelected();
        RefreshCaptions();
        RebuildTree();
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
            var parts = string.IsNullOrWhiteSpace(folder) || folder == "."
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
        var label = new TextBlock
        {
            Text = node.Name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = node.IsFolder ? "📁" : "📄",
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(label);
        if (!node.IsFolder && node.Row is not null)
        {
            row.Children.Add(new TextBlock
            {
                Text = node.Row.StatusText,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushFor(node.Row.Status)
            });
        }

        var item = new TreeViewItem
        {
            Header = row,
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

    private void CreatePr_Click(object sender, RoutedEventArgs e) => Accept(createPr: true);

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
                "У каждого скрипта должны быть имя, USE и папка. Для строк без mapping выберите папку вручную.",
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
        public string FileName { get; set; } = "FileName.sql";
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
        public string Caption => $"{FileName} — {StatusText}";
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
