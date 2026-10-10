using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Models;
using AutoProdScripts.Services.AzureDevOps;
using AutoProdScripts.Services.Discovery;
using AutoProdScripts.Services.Git;
using AutoProdScripts.Themes;
using AutoProdScripts.Views;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AutoProdScripts;

public partial class MainWindow : Window
{
    private readonly ProjectDiscoveryService _discovery = new();
    private readonly List<OpenDocument> _documents = new();
    private GitCliService? _git;
    private OpenDocument? _activeDocument;
    private string? _currentFilePath;
    private bool _dirty;
    private bool _suppressEditorEvents;
    private string? _currentBranch;
    private string? _selectedFolderRel;
    private string? _contextFolderFullPath;

    public MainWindow()
    {
        InitializeComponent();
        App.Log.Entries.CollectionChanged += (_, _) => RenderLog();
        RenderLog();
        SqlEditor.SyntaxHighlighting = SqlHighlightingLoader.GetDefinition()
                                       ?? HighlightingManager.Instance.GetDefinition("TSQL")
                                       ?? HighlightingManager.Instance.GetDefinition("SQL");
        SqlEditor.TextChanged += SqlEditor_TextChanged;
        Loaded += async (_, _) => await OnLoadedAsync();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void SqlEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || _activeDocument is null || _activeDocument.IsDirty)
            return;

        _activeDocument.IsDirty = true;
        _dirty = true;
        UpdateTabChrome(_activeDocument);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }

    private async Task OnLoadedAsync()
    {
        if (!App.Settings.Current.IsConfigured ||
            string.IsNullOrWhiteSpace(App.Settings.Current.ProjectPath) ||
            !Directory.Exists(App.Settings.Current.ProjectPath))
        {
            if (!ShowSettingsDialog())
            {
                Close();
                return;
            }
        }

        await InitializeWorkspaceAsync();
    }

    private bool ShowSettingsDialog()
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultSettings is null)
            return false;

        App.Settings.Save(dlg.ResultSettings);
        if (!string.IsNullOrWhiteSpace(dlg.ResultPat))
            App.Settings.SavePat(dlg.ResultPat);

        App.Log.Info("Настройки сохранены.");
        return true;
    }

    private async Task InitializeWorkspaceAsync()
    {
        var path = App.Settings.Current.ProjectPath;
        _git = new GitCliService(path);
        Title = $"AutoProdScripts — {path}";

        if (!_git.IsGitAvailable(out var gitMsg))
        {
            SetStatus(gitMsg);
            App.Log.Error(gitMsg);
        }
        else
        {
            App.Log.Info(gitMsg);
            if (!_git.IsRepository(out var repoMsg))
            {
                SetStatus(repoMsg);
                App.Log.Warn(repoMsg);
            }
        }

        RefreshTree();
        await RefreshBranchUiAsync();
        await RefreshChangesAsync();
        SetStatus("готов");
    }

    private void RefreshTree()
    {
        ProjectTree.Items.Clear();
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        var nodes = _discovery.BuildTree(root);
        foreach (var node in nodes)
            ProjectTree.Items.Add(CreateTreeItem(node));
    }

    private static readonly Geometry FolderIconData = Freeze(Geometry.Parse(
        "M1.2,4.5 H5.8 L7.1,3 H12.8 V11.7 H1.2 Z"));

    private static readonly Geometry FileIconData = Freeze(Geometry.Parse(
        "M3.2,1.3 H8.1 L11.8,5 V13.5 H3.2 Z M8.1,1.5 V5 H11.6"));

    private static readonly Brush FolderIconBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD7, 0xBA, 0x7D)));
    private static readonly Brush FileIconBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xD4)));

    private static TreeViewItem CreateTreeItem(FileSystemNode node)
    {
        var item = new TreeViewItem
        {
            Header = CreateNodeHeader(node),
            Tag = node,
            IsExpanded = false
        };
        if (node.IsDirectory)
        {
            foreach (var child in node.Children)
                item.Items.Add(CreateTreeItem(child));
        }

        return item;
    }

    private static FrameworkElement CreateNodeHeader(FileSystemNode node)
    {
        var icon = node.IsDirectory ? CreateFolderIcon() : CreateFileIcon();
        var label = new TextBlock
        {
            Text = node.Name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);
        return row;
    }

    private static System.Windows.Shapes.Path CreateFolderIcon() => new()
    {
        Width = 14,
        Height = 12,
        Stretch = Stretch.Fill,
        Fill = FolderIconBrush,
        Data = FolderIconData,
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = true
    };

    private static System.Windows.Shapes.Path CreateFileIcon() => new()
    {
        Width = 13,
        Height = 14,
        Stretch = Stretch.Uniform,
        Stroke = FileIconBrush,
        StrokeThickness = 1.15,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Fill = Brushes.Transparent,
        Data = FileIconData,
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = true
    };

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private async Task RefreshBranchUiAsync()
    {
        if (_git is null)
            return;

        _currentBranch = await _git.GetCurrentBranchAsync();
        BranchStatusText.Text = $"ветка: {_currentBranch ?? "—"}";
        SideBranchText.Text = $"Ветка: {_currentBranch ?? "—"}";
        SideBaseText.Text = $"база (последняя): {App.Settings.Current.LastBaseBranch}";
    }

    private async Task RefreshChangesAsync()
    {
        ChangesTree.Items.Clear();
        if (_git is null)
        {
            NoChangesText.Visibility = Visibility.Visible;
            ChangesTree.Visibility = Visibility.Collapsed;
            return;
        }

        var entries = await _git.GetStatusEntriesAsync();
        var hasChanges = entries.Count > 0;
        NoChangesText.Visibility = hasChanges ? Visibility.Collapsed : Visibility.Visible;
        ChangesTree.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        foreach (var node in BuildChangeTree(entries))
            ChangesTree.Items.Add(CreateChangeItem(node));
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void RenderLog()
    {
        var text = string.Join(Environment.NewLine, App.Log.Entries);
        if (LogBox.Text == text)
            return;

        LogBox.Text = text;
        LogBox.ScrollToHome();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null) return;
        SetStatus("sync…");
        var remote = App.Settings.Current.RemoteName;
        var result = await _git.SyncCurrentBranchAsync(remote);
        if (result.Success)
        {
            var detail = OneLine(result.CombinedOutput);
            App.Log.Info(string.IsNullOrWhiteSpace(detail) ? $"sync ok ({remote})" : $"sync ok ({remote}): {detail}");
            SetStatus("sync ok");
            RefreshTree();
        }
        else
        {
            App.Log.Error($"sync failed: {result.CombinedOutput}");
            SetStatus("ошибка sync");
            MessageBox.Show(this, result.CombinedOutput, "Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await RefreshBranchUiAsync();
        await RefreshChangesAsync();
    }

    private static string OneLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length > 240 ? line[..240] + "…" : line;
    }

    private async void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null) return;

        var branches = await _git.ListBranchesAsync();
        var names = branches.Select(b => b.Name).ToList();
        var dlg = new NewBranchDialog(names, App.Settings.Current.LastBaseBranch) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedBaseBranch is null || dlg.NewBranchName is null)
            return;

        SetStatus("создание ветки…");
        var result = await _git.CreateBranchAsync(
            dlg.NewBranchName,
            dlg.SelectedBaseBranch,
            App.Settings.Current.RemoteName);
        RefreshTree();
        if (!result.Success)
        {
            App.Log.Error($"branch failed: {result.CombinedOutput}");
            SetStatus("ошибка ветки");
            MessageBox.Show(this, result.CombinedOutput, "Новая ветка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var settings = App.Settings.Current;
        settings.LastBaseBranch = dlg.SelectedBaseBranch;
        App.Settings.Save(settings);

        App.Log.Info($"ветка создана: {dlg.NewBranchName} от {dlg.SelectedBaseBranch}");
        SetStatus("ветка создана");
        await RefreshBranchUiAsync();
    }

    private void NewScript_Click(object sender, RoutedEventArgs e)
    {
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        var folders = _discovery.DiscoverWorkFolders(root);
        var dlg = new NewScriptDialog(root, folders, _selectedFolderRel) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.CreatedFilePath is null)
            return;

        App.Log.Info($"файл создан: {dlg.CreatedFilePath}");
        RefreshTree();
        OpenFile(dlg.CreatedFilePath);
        _ = RefreshChangesAsync();
    }

    private async void Status_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null) return;
        var result = await _git.StatusPorcelainAsync();
        await RefreshChangesAsync();
        MessageBox.Show(this,
            string.IsNullOrWhiteSpace(result.CombinedOutput) ? "(чисто)" : result.CombinedOutput,
            "git status", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ProjectTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectTree.SelectedItem is not TreeViewItem { Tag: FileSystemNode node })
            return;

        if (node.IsDirectory)
        {
            var root = App.Settings.Current.ProjectPath;
            _selectedFolderRel = Path.GetRelativePath(root, node.FullPath).Replace('\\', '/');
            if (_selectedFolderRel == ".")
                _selectedFolderRel = null;
            return;
        }

        OpenFile(node.FullPath);
    }

    private void ProjectTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;

        e.Handled = true;
        DeleteSelectedTreeFile();
    }

    private void ProjectTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null)
            return;

        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            _contextFolderFullPath = App.Settings.Current.ProjectPath;
        }
        else
        {
            item.IsSelected = true;
            item.Focus();
            if (item.Tag is not FileSystemNode node)
                _contextFolderFullPath = App.Settings.Current.ProjectPath;
            else
                _contextFolderFullPath = node.IsDirectory
                    ? node.FullPath
                    : Path.GetDirectoryName(node.FullPath);
        }

        e.Handled = true;
        ShowAddScriptDialog();
    }

    private void ShowAddScriptDialog()
    {
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        if (string.IsNullOrWhiteSpace(_contextFolderFullPath) || !Directory.Exists(_contextFolderFullPath))
            _contextFolderFullPath = root;

        var relative = Path.GetRelativePath(root, _contextFolderFullPath).Replace('\\', '/');
        var caption = relative == "." ? "Папка: корень проекта" : $"Папка: {relative}";
        var dlg = new AddScriptDialog(caption) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ChosenFileName))
            return;

        CreateScriptInContextFolder(dlg.ChosenFileName);
    }

    private void DeleteSelectedTreeFile()
    {
        if (ProjectTree.SelectedItem is not TreeViewItem { Tag: FileSystemNode node })
            return;

        if (node.IsDirectory)
        {
            SetStatus("удаление папки не поддерживается");
            return;
        }

        var answer = MessageBox.Show(this, $"Удалить файл «{node.Name}»?", "Удаление",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            if (File.Exists(node.FullPath))
                File.Delete(node.FullPath);
        }
        catch (Exception ex)
        {
            App.Log.Error($"не удалось удалить файл: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var open = _documents.FirstOrDefault(d =>
            string.Equals(d.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase));
        if (open is not null)
            CloseDocument(open, promptIfDirty: false);

        App.Log.Info($"файл удалён: {node.FullPath}");
        RefreshTree();
        _ = RefreshChangesAsync();
        SetStatus($"удалён: {node.Name}");
    }

    private void CreateScriptInContextFolder(string fileName)
    {
        var root = App.Settings.Current.ProjectPath;
        var folder = _contextFolderFullPath;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!string.Equals(folder, root, StringComparison.OrdinalIgnoreCase) &&
            !folder.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        fileName = fileName.Trim();
        if (!fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            fileName += ".sql";

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            MessageBox.Show(this, "Имя файла содержит недопустимые символы.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fullPath = Path.Combine(folder, fileName);
        if (File.Exists(fullPath))
        {
            MessageBox.Show(this, "Файл уже существует. Выберите другое имя.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(fullPath, string.Empty);
        }
        catch (Exception ex)
        {
            App.Log.Error($"не удалось создать файл: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Новый скрипт", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        _selectedFolderRel = relative == "." ? null : relative;

        App.Log.Info($"файл создан: {fullPath}");
        RefreshTree();
        RevealInTree(fullPath);
        OpenFile(fullPath);
        _ = RefreshChangesAsync();
        SetStatus($"создан: {fileName}");
    }

    private void RevealInTree(string fullPath)
    {
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root))
            return;

        var relative = Path.GetRelativePath(root, fullPath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ItemCollection items = ProjectTree.Items;
        TreeViewItem? current = null;
        foreach (var part in parts)
        {
            current = items.OfType<TreeViewItem>().FirstOrDefault(item =>
                item.Tag is FileSystemNode node &&
                string.Equals(node.Name, part, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return;

            current.IsExpanded = true;
            items = current.Items;
        }

        if (current is null)
            return;

        current.IsSelected = true;
        current.BringIntoView();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static readonly Brush AddedChangeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7D, 0xCE, 0xA0)));
    private static readonly Brush ModifiedChangeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD7, 0xBA, 0x7D)));
    private static readonly Brush DeletedChangeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x7A)));
    private static readonly Brush RenamedChangeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xD4)));

    private static List<ChangeNode> BuildChangeTree(IReadOnlyList<GitStatusEntry> entries)
    {
        var roots = new List<ChangeNode>();
        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var parts = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var list = roots;
            var accumulated = string.Empty;
            for (var i = 0; i < parts.Length; i++)
            {
                var isFile = i == parts.Length - 1;
                accumulated = accumulated.Length == 0 ? parts[i] : accumulated + "/" + parts[i];
                var existing = list.FirstOrDefault(node =>
                    node.IsDirectory == !isFile &&
                    string.Equals(node.Name, parts[i], StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new ChangeNode
                    {
                        Name = parts[i],
                        RelativePath = accumulated,
                        IsDirectory = !isFile,
                        Entry = isFile ? entry : null
                    };
                    list.Add(existing);
                }
                else if (isFile)
                {
                    existing.Entry = entry;
                }

                list = existing.Children;
            }
        }

        SortChangeNodes(roots);
        return roots;
    }

    private static void SortChangeNodes(List<ChangeNode> nodes)
    {
        nodes.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var node in nodes)
            SortChangeNodes(node.Children);
    }

    private TreeViewItem CreateChangeItem(ChangeNode node)
    {
        var item = new TreeViewItem
        {
            Header = CreateChangeHeader(node),
            Tag = node,
            IsExpanded = true
        };
        foreach (var child in node.Children)
            item.Items.Add(CreateChangeItem(child));
        return item;
    }

    private static FrameworkElement CreateChangeHeader(ChangeNode node)
    {
        var icon = node.IsDirectory ? CreateFolderIcon() : CreateFileIcon();
        var label = new TextBlock
        {
            Text = node.Name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);
        if (!node.IsDirectory && node.Entry is not null)
        {
            var kind = GitStatusFormat.KindOf(node.Entry.Status);
            row.Children.Add(new TextBlock
            {
                Text = GitStatusFormat.Label(kind),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushForKind(kind)
            });
        }

        return row;
    }

    private static Brush BrushForKind(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added => AddedChangeBrush,
        GitChangeKind.Deleted => DeletedChangeBrush,
        GitChangeKind.Renamed => RenamedChangeBrush,
        _ => ModifiedChangeBrush
    };

    private void ChangesTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChangesTree.SelectedItem is not TreeViewItem { Tag: ChangeNode { IsDirectory: false, Entry: { } entry } })
            return;

        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root))
            return;

        var fullPath = Path.GetFullPath(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(fullPath))
            OpenFile(fullPath);
    }

    private void ChangesTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is null)
            return;

        item.IsSelected = true;
        item.Focus();
    }

    private void ChangesTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ChangesTree.SelectedItem is not TreeViewItem { Tag: ChangeNode })
            e.Handled = true;
    }

    private async void RevertChange_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null)
            return;
        if (ChangesTree.SelectedItem is not TreeViewItem { Tag: ChangeNode node })
            return;

        var files = node.EnumerateFiles().ToList();
        if (files.Count == 0)
            return;

        var prompt = node.IsDirectory
            ? $"Отменить изменения в папке «{node.Name}» ({files.Count})?"
            : $"Отменить изменения в «{node.Name}»?";
        var answer = MessageBox.Show(this, prompt, "Отмена изменений",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var result = await RevertEntriesAsync(files);
        if (!result.Success)
        {
            App.Log.Error($"не удалось отменить изменения: {result.CombinedOutput}");
            MessageBox.Show(this, result.CombinedOutput, "Отмена изменений",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        App.Log.Info(node.IsDirectory
            ? $"отменены изменения в папке {node.RelativePath}"
            : $"отменены изменения: {node.RelativePath}");
        ReloadOpenDocumentsFromDisk();
        RefreshTree();
        await RefreshChangesAsync();
        SetStatus("изменения отменены");
    }

    private async Task<GitResult> RevertEntriesAsync(IReadOnlyList<GitStatusEntry> entries)
    {
        if (_git is null)
            return new GitResult(false, string.Empty, "Git недоступен.", -1);

        var root = Path.GetFullPath(App.Settings.Current.ProjectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var restore = new List<string>();
        var unstage = new List<string>();
        var delete = new List<string>();

        foreach (var entry in entries)
        {
            if (GitStatusFormat.DiscardAsNewFile(entry.Status))
            {
                if (entry.Status.Length > 0 && entry.Status[0] == 'A')
                    unstage.Add(entry.Path);
                delete.Add(entry.Path);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.OldPath))
            {
                restore.Add(entry.OldPath);
                if (entry.Status.Length > 0 && entry.Status[0] is not (' ' or '?'))
                    unstage.Add(entry.Path);
                delete.Add(entry.Path);
                continue;
            }

            restore.Add(entry.Path);
        }

        if (unstage.Count > 0)
        {
            var reset = await _git.UnstageAsync(unstage);
            if (!reset.Success)
                return reset;
        }

        if (restore.Count > 0)
        {
            var restored = await _git.RestoreToHeadAsync(restore);
            if (!restored.Success)
                return restored;
        }

        foreach (var relative in delete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInsideRoot(root, full) || !File.Exists(full))
                continue;

            File.Delete(full);
            RemoveEmptyParents(root, Path.GetDirectoryName(full));
        }

        return new GitResult(true, string.Empty, string.Empty, 0);
    }

    private void ReloadOpenDocumentsFromDisk()
    {
        foreach (var doc in _documents.ToList())
        {
            if (!File.Exists(doc.FullPath))
            {
                CloseDocument(doc, promptIfDirty: false);
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(doc.FullPath);
            }
            catch
            {
                continue;
            }

            if (doc.Document.Text != text)
            {
                _suppressEditorEvents = true;
                try
                {
                    doc.Document.Text = text;
                    doc.CaretOffset = 0;
                    if (doc == _activeDocument)
                        SqlEditor.CaretOffset = 0;
                }
                finally
                {
                    _suppressEditorEvents = false;
                }
            }

            doc.IsDirty = false;
            if (doc == _activeDocument)
                _dirty = false;
            UpdateTabChrome(doc);
        }
    }

    private static bool IsInsideRoot(string root, string fullPath)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveEmptyParents(string root, string? directory)
    {
        while (!string.IsNullOrWhiteSpace(directory) &&
               IsInsideRoot(root, directory) &&
               !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                return;

            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            App.Log.Error($"не удалось открыть файл: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var existing = _documents.FirstOrDefault(d =>
            string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateDocument(existing);
            return;
        }

        if (!File.Exists(path))
        {
            App.Log.Error($"файл не найден: {path}");
            RefreshTree();
            MessageBox.Show(this,
                "Файл не найден на диске. Дерево проекта обновлено.\nЕсли скрипты должны были прийти с сервера, выполните Sync — он подтягивает файлы в текущую ветку.",
                "Файл не найден",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        TextDocument document;
        try
        {
            document = new TextDocument(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            App.Log.Error($"не удалось открыть файл: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var opened = new OpenDocument { FullPath = path, Document = document };
        _documents.Add(opened);
        CreateTab(opened);
        UpdateEmptyTabsHint();
        ActivateDocument(opened);
    }

    private void ActivateDocument(OpenDocument doc)
    {
        if (_activeDocument != doc)
        {
            _suppressEditorEvents = true;
            try
            {
                if (_activeDocument is not null)
                    _activeDocument.CaretOffset = SqlEditor.CaretOffset;

                _activeDocument = doc;
                _currentFilePath = doc.FullPath;
                SqlEditor.Document = doc.Document;
                SqlEditor.CaretOffset = Math.Clamp(doc.CaretOffset, 0, doc.Document.TextLength);
            }
            finally
            {
                _suppressEditorEvents = false;
            }

            _dirty = doc.IsDirty;
            SqlEditor.Focus();
            SetStatus($"открыт: {Path.GetFileName(doc.FullPath)}");
        }

        RefreshTabChrome();
        doc.Tab?.BringIntoView();
    }

    private void CreateTab(OpenDocument doc)
    {
        var icon = CreateFileIcon();
        icon.Width = 12;
        icon.Height = 13;
        icon.Margin = new Thickness(0, 0, 6, 0);

        var dirty = new TextBlock
        {
            Text = "●",
            FontSize = 9,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("FgInfoBrush"),
            Visibility = Visibility.Collapsed
        };

        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var close = new TextBlock
        {
            Text = "×",
            FontSize = 14,
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("FgMutedBrush"),
            Cursor = Cursors.Hand,
            ToolTip = "Закрыть"
        };
        close.MouseEnter += (_, _) => close.Foreground = BrushOf("FgBrush");
        close.MouseLeave += (_, _) => close.Foreground = BrushOf("FgMutedBrush");
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CloseDocument(doc);
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(dirty);
        row.Children.Add(icon);
        row.Children.Add(title);
        row.Children.Add(close);

        var tab = new Border
        {
            Child = row,
            Height = 32,
            Padding = new Thickness(10, 0, 6, 0),
            Cursor = Cursors.Hand,
            ToolTip = doc.FullPath,
            BorderThickness = new Thickness(0, 0, 1, 2),
            SnapsToDevicePixels = true
        };
        tab.MouseLeftButtonDown += (_, _) => ActivateDocument(doc);
        tab.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle)
                return;
            e.Handled = true;
            CloseDocument(doc);
        };

        doc.Tab = tab;
        doc.TitleText = title;
        doc.DirtyMark = dirty;
        FileTabStrip.Children.Add(tab);
        UpdateTabChrome(doc);
    }

    private void CloseDocument(OpenDocument doc, bool promptIfDirty = true)
    {
        if (promptIfDirty && doc.IsDirty)
        {
            var answer = MessageBox.Show(this,
                $"Сохранить изменения в «{Path.GetFileName(doc.FullPath)}»?",
                "Файл изменён",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
                return;
            if (answer == MessageBoxResult.Yes && !TrySaveDocument(doc, showStatus: true))
                return;
        }

        var index = _documents.IndexOf(doc);
        _documents.Remove(doc);
        if (doc.Tab is not null)
            FileTabStrip.Children.Remove(doc.Tab);

        if (_activeDocument == doc)
        {
            _activeDocument = null;
            _currentFilePath = null;
            _dirty = false;
            var next = index > 0 ? _documents[index - 1] : _documents.FirstOrDefault();
            if (next is null)
            {
                _suppressEditorEvents = true;
                SqlEditor.Document = new TextDocument();
                _suppressEditorEvents = false;
                SetStatus("готов");
            }
            else
            {
                ActivateDocument(next);
            }
        }

        UpdateEmptyTabsHint();
        RefreshTabChrome();
    }

    private void UpdateEmptyTabsHint() =>
        EmptyTabsText.Visibility = _documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshTabChrome()
    {
        foreach (var doc in _documents)
            UpdateTabChrome(doc);
    }

    private void UpdateTabChrome(OpenDocument doc)
    {
        if (doc.Tab is null || doc.TitleText is null || doc.DirtyMark is null)
            return;

        doc.TitleText.Text = TabLabel(doc);
        doc.DirtyMark.Visibility = doc.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        var active = doc == _activeDocument;
        doc.Tab.Background = BrushOf(active ? "BgBrush" : "PanelAltBrush");
        doc.Tab.BorderBrush = active ? BrushOf("StatusBrush") : BrushOf("BorderBrush");
        doc.TitleText.Foreground = BrushOf(active ? "FgBrush" : "FgMutedBrush");
    }

    private string TabLabel(OpenDocument doc)
    {
        var name = Path.GetFileName(doc.FullPath);
        var duplicate = _documents.Any(other => other != doc &&
            string.Equals(Path.GetFileName(other.FullPath), name, StringComparison.OrdinalIgnoreCase));
        if (!duplicate)
            return name;

        var parent = Path.GetFileName(Path.GetDirectoryName(doc.FullPath) ?? string.Empty);
        return string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
    }

    private static Brush BrushOf(string key) => (Brush)Application.Current.FindResource(key);

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrentFile();

    private void SaveCurrentFile()
    {
        if (_activeDocument is null)
        {
            MessageBox.Show(this, "Нет открытого файла.", "Сохранение",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TrySaveDocument(_activeDocument, showStatus: true);
    }

    private bool TrySaveDocument(OpenDocument doc, bool showStatus)
    {
        try
        {
            File.WriteAllText(doc.FullPath, doc.Document.Text);
            doc.IsDirty = false;
            if (doc == _activeDocument)
                _dirty = false;
            UpdateTabChrome(doc);
            App.Log.Info($"сохранён: {doc.FullPath}");
            if (showStatus)
                SetStatus("сохранён");
            _ = RefreshChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            App.Log.Error($"ошибка сохранения: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool SaveAllDirtyDocuments()
    {
        foreach (var doc in _documents)
        {
            if (!doc.IsDirty)
                continue;
            if (!TrySaveDocument(doc, showStatus: false))
                return false;
        }

        _dirty = false;
        SetStatus("сохранён");
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var dirty = _documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            base.OnClosing(e);
            return;
        }

        var prompt = dirty.Count == 1
            ? $"Сохранить изменения в «{Path.GetFileName(dirty[0].FullPath)}»?"
            : $"Сохранить изменения в файлах ({dirty.Count})?";
        var answer = MessageBox.Show(this, prompt, "Файл изменён",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.Yes && !SaveAllDirtyDocuments())
            e.Cancel = true;

        if (!e.Cancel)
            base.OnClosing(e);
    }

    private async void CreatePr_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null)
            return;

        var settings = App.Settings.Current;
        var pat = App.Settings.LoadPat();
        if (string.IsNullOrWhiteSpace(pat))
        {
            MessageBox.Show(this, "PAT не задан. Откройте Настройки.", "Create PR",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.AzureDevOpsProject) ||
            string.IsNullOrWhiteSpace(settings.AzureDevOpsRepo))
        {
            MessageBox.Show(this, "Укажите Project и Repo в Настройках.", "Create PR",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var branch = await _git.GetCurrentBranchAsync();
        if (string.IsNullOrWhiteSpace(branch))
        {
            MessageBox.Show(this, "Не удалось определить текущую ветку.", "Create PR",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var baseBranch = settings.LastBaseBranch;
        if (branch.Equals(baseBranch, StringComparison.OrdinalIgnoreCase) ||
            branch is "master" or "main")
        {
            var confirm = MessageBox.Show(this,
                $"Вы на ветке «{branch}». Создать PR от неё в «{baseBranch}» всё равно?",
                "Create PR", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var changes = await _git.GetStatusEntriesAsync();
        var dirtyCount = _documents.Count(d => d.IsDirty);
        if (changes.Count > 0 || dirtyCount > 0)
        {
            var message = $"{branch}: обновление SQL-скриптов";
            var pending = Math.Max(changes.Count, dirtyCount);
            var confirmCommit = MessageBox.Show(this,
                $"Есть незакоммиченные изменения ({pending}).\nСообщение commit:\n{message}\n\nЗакоммитить и продолжить?",
                "Create PR", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (confirmCommit == MessageBoxResult.Cancel)
                return;
            if (confirmCommit == MessageBoxResult.Yes)
            {
                if (!SaveAllDirtyDocuments())
                    return;

                SetStatus("commit…");
                var add = await _git.AddAsync(new[] { "." });
                if (!add.Success)
                {
                    ShowPrError($"git add: {add.CombinedOutput}");
                    return;
                }

                var commit = await _git.CommitAsync(message);
                if (!commit.Success)
                {
                    ShowPrError($"git commit: {commit.CombinedOutput}");
                    return;
                }

                App.Log.Info("commit ok");
            }
        }

        SetStatus("push…");
        var push = await _git.PushAsync(settings.RemoteName, branch, setUpstream: true);
        if (!push.Success)
        {
            App.Log.Error($"push failed: {push.CombinedOutput}");
            ShowPrError($"git push: {push.CombinedOutput}");
            return;
        }

        App.Log.Info("push ok");
        SetStatus("проверка PR…");

        using var client = new AzureDevOpsClient(
            settings.AzureDevOpsOrgUrl,
            settings.AzureDevOpsProject,
            settings.AzureDevOpsRepo,
            pat);

        var (prChecked, activePr) = await client.FindActivePullRequestAsync(branch);
        if (prChecked && activePr is not null)
        {
            App.Log.Info(activePr.Message + (activePr.Url is null ? string.Empty : $" {activePr.Url}"));
            SetStatus("PR уже активен");
            await RefreshChangesAsync();
            var existingDlg = new PullRequestResultDialog(
                activePr.PullRequestId is null
                    ? "Активный Pull Request"
                    : $"Pull Request #{activePr.PullRequestId} уже активен",
                activePr.Message,
                activePr.Url,
                success: true)
            { Owner = this };
            existingDlg.ShowDialog();
            return;
        }

        if (!prChecked)
            App.Log.Warn(activePr is null
                ? "не удалось проверить активные PR, создаём новый"
                : $"не удалось проверить активные PR: {activePr.Message}. Создаём новый");

        SetStatus("создание PR…");

        var title = $"{branch}";
        if (!string.IsNullOrWhiteSpace(_currentFilePath))
            title = $"{branch}: {Path.GetFileName(_currentFilePath)}";

        var pr = await client.CreatePullRequestAsync(branch, baseBranch, title, title);
        if (!pr.Success)
        {
            App.Log.Error(pr.Message);
            SetStatus("ошибка PR");
            var failDlg = new PullRequestResultDialog(
                "Не удалось создать Pull Request",
                pr.Message,
                null,
                success: false)
            { Owner = this };
            failDlg.ShowDialog();
            return;
        }

        App.Log.Info(pr.Message + (pr.Url is null ? string.Empty : $" {pr.Url}"));
        SetStatus("PR создан");
        await RefreshChangesAsync();

        var okDlg = new PullRequestResultDialog(
            $"Pull Request создан{(pr.PullRequestId is null ? string.Empty : $" #{pr.PullRequestId}")}",
            $"Status: Active\n{pr.Message}",
            pr.Url,
            success: true)
        { Owner = this };
        okDlg.ShowDialog();
    }

    private void ShowPrError(string message)
    {
        SetStatus("ошибка PR");
        MessageBox.Show(this, message, "Create PR", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowSettingsDialog())
            return;
        _ = InitializeWorkspaceAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class OpenDocument
    {
        public required string FullPath { get; init; }
        public required TextDocument Document { get; init; }
        public bool IsDirty { get; set; }
        public int CaretOffset { get; set; }
        public Border? Tab { get; set; }
        public TextBlock? TitleText { get; set; }
        public TextBlock? DirtyMark { get; set; }
    }

    private sealed class ChangeNode
    {
        public required string Name { get; init; }
        public required string RelativePath { get; init; }
        public required bool IsDirectory { get; init; }
        public GitStatusEntry? Entry { get; set; }
        public List<ChangeNode> Children { get; } = new();

        public IEnumerable<GitStatusEntry> EnumerateFiles()
        {
            if (Entry is not null)
                yield return Entry;
            foreach (var child in Children)
            {
                foreach (var file in child.EnumerateFiles())
                    yield return file;
            }
        }
    }
}
