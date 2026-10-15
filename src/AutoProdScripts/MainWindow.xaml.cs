using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Models;
using AutoProdScripts.Services.AzureDevOps;
using AutoProdScripts.Services.Discovery;
using AutoProdScripts.Services.Git;
using AutoProdScripts.Services.Mapping;
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
    private bool _branchUiLock;
    private List<GitBranchRef> _branchRefs = new();
    private OpenDocument? _activeDocument;
    private string? _currentFilePath;
    private bool _dirty;
    private bool _testMode;
    private bool _suppressEditorEvents;
    private string? _currentBranch;
    private string? _selectedFolderRel;
    private string? _contextFolderFullPath;

    public MainWindow()
    {
        InitializeComponent();
        BranchPopup.CustomPopupPlacementCallback = PlaceBranchPopupAbove;
        App.Log.Entries.CollectionChanged += (_, _) => RenderLog();
        RenderLog();
        SqlEditor.SyntaxHighlighting = SqlHighlightingLoader.GetDefinition()
                                       ?? HighlightingManager.Instance.GetDefinition("TSQL")
                                       ?? HighlightingManager.Instance.GetDefinition("SQL");
        SqlEditor.TextChanged += SqlEditor_TextChanged;
        Loaded += async (_, _) => await OnLoadedAsync();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private static CustomPopupPlacement[] PlaceBranchPopupAbove(Size popupSize, Size targetSize, Point offset) =>
        new[]
        {
            new CustomPopupPlacement(new Point(0, -popupSize.Height - 4), PopupPrimaryAxis.None)
        };

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
            return;
        }

        if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _testMode = !_testMode;
            TestAutoPrHost.Visibility = _testMode ? Visibility.Visible : Visibility.Collapsed;
            var message = _testMode ? "тестовый режим включён" : "тестовый режим выключен";
            App.Log.Info(message);
            SetStatus(message);
            e.Handled = true;
        }
    }

    private void TestAutoPr_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings.Current;
        var text = TelegramPreviewRenderer.DemoText;
        var pr = new PullRequestResult(
            true,
            128,
            TelegramPreviewRenderer.DemoUrl,
            "Тестовый PR. Изменения в git не отправлялись, pull request не создавался.",
            text);
        ShowTelegramResult(
            "Тестовый Pull Request #128",
            "Режим проверки шаблона. Git и Azure DevOps не вызывались.",
            pr,
            text,
            settings);
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

    private bool ShowSettingsDialog(bool openMicrosoftLogin = false, bool openAutoPrTab = false)
    {
        var dlg = new SettingsWindow(openMicrosoftLogin, openAutoPrTab) { Owner = this };
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
        ReloadUseMapping(showError: false);
        await RefreshBranchUiAsync();
        await RefreshChangesAsync();
        RefreshAuthStatus();
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
    private static readonly Brush StatusOkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)));
    private static readonly Brush StatusBusyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0xB6, 0xFF)));
    private static readonly Brush StatusWarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xC4, 0x51)));
    private static readonly Brush StatusErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Geometry UndoIconData = Freeze(Geometry.Parse(
        "M12.5,8 C9.85,8 7.45,8.99 5.6,10.6 L2,7 V16 H11 L7.38,12.38 C8.77,11.22 10.54,10.5 12.5,10.5 C16.04,10.5 19.05,12.81 20.1,16 L22.47,15.22 C21.08,11.03 17.15,8 12.5,8 Z"));
    private string? _renderedLogKey;

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
        SideBaseText.Text = $"база (последняя): {App.Settings.Current.LastBaseBranch}";
        await FillBranchBoxAsync();
    }

    private async Task FillBranchBoxAsync()
    {
        if (_git is null)
            return;

        _branchRefs = (await _git.ListBranchRefsAsync()).ToList();
        BranchButtonText.Text = string.IsNullOrWhiteSpace(_currentBranch) ? "—" : _currentBranch;
        BuildBranchTree();
        if (BranchPopup.IsOpen)
            ApplyBranchFilter();
    }

    private void SwitchBranch_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null)
            return;

        BranchPopup.IsOpen = true;
    }

    private void BranchPopup_Opened(object sender, EventArgs e)
    {
        _branchUiLock = true;
        BranchSearchBox.Text = string.Empty;
        _branchUiLock = false;
        ApplyBranchFilter();
        BranchSearchBox.Focus();
        BranchSearchBox.SelectAll();
    }

    private void BranchSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_branchUiLock)
            return;
        ApplyBranchFilter();
    }

    private void BuildBranchTree()
    {
        var local = _branchRefs
            .Where(branch => branch.Remote is null)
            .OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remotes = _branchRefs
            .Where(branch => branch.Remote is not null)
            .GroupBy(branch => branch.Remote!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        _branchUiLock = true;
        try
        {
            BranchTree.Items.Clear();
            AddBranchFolder("Стянутые", local);
            foreach (var group in remotes)
            {
                AddBranchFolder(
                    "remote/" + group.Key,
                    group.OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase).ToList());
            }

            BranchTree.Visibility = BranchTree.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ApplyBranchFilter();
        }
        finally
        {
            _branchUiLock = false;
        }
    }

    private void AddBranchFolder(string title, IReadOnlyList<GitBranchRef> branches)
    {
        if (branches.Count == 0)
            return;

        var folder = CreateBranchFolderItem(title, info: true);
        foreach (var branch in branches)
            InsertBranch(folder, branch);
        BranchTree.Items.Add(folder);
    }

    private void InsertBranch(TreeViewItem root, GitBranchRef branch)
    {
        var parts = branch.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var parent = root;
        for (var i = 0; i < parts.Length - 1; i++)
            parent = FindOrAddBranchFolder(parent, parts[i]);

        var leafName = parts[^1];
        parent.Items.Add(new TreeViewItem
        {
            Header = new TextBlock
            {
                Text = branch.IsCurrent ? leafName + "  · текущая" : leafName,
                Foreground = (Brush)FindResource(branch.IsCurrent ? "FgInfoBrush" : "FgBrush")
            },
            Tag = branch
        });
    }

    private TreeViewItem FindOrAddBranchFolder(TreeViewItem parent, string name)
    {
        foreach (TreeViewItem child in parent.Items)
        {
            if (child.Tag is null && ItemTitle(child).Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;
        }

        var folder = CreateBranchFolderItem(name, info: false);
        parent.Items.Add(folder);
        return folder;
    }

    private TreeViewItem CreateBranchFolderItem(string title, bool info) => new()
    {
        IsExpanded = false,
        Header = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(info ? "FgInfoBrush" : "FgBrush")
        }
    };

    private static string ItemTitle(TreeViewItem item) =>
        item.Header is TextBlock text ? text.Text : string.Empty;

    private void ApplyBranchFilter()
    {
        var query = (BranchSearchBox.Text ?? string.Empty).Trim();
        if (BranchTree.Items.Count == 0)
        {
            BranchEmptyText.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrEmpty(query))
        {
            foreach (TreeViewItem item in BranchTree.Items)
                ShowBranchItem(item);
            foreach (TreeViewItem item in BranchTree.Items)
                ExpandToCurrent(item);
            BranchEmptyText.Visibility = Visibility.Collapsed;
            BranchTree.Visibility = Visibility.Visible;
            return;
        }

        var any = false;
        foreach (TreeViewItem item in BranchTree.Items)
            any |= FilterBranchItem(item, query);
        BranchEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        BranchTree.Visibility = Visibility.Visible;
    }

    private bool FilterBranchItem(TreeViewItem item, string query)
    {
        if (item.Tag is GitBranchRef branch)
        {
            var path = branch.Remote is null ? branch.Name : branch.Remote + "/" + branch.Name;
            var match = path.Contains(query, StringComparison.OrdinalIgnoreCase);
            item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            return match;
        }

        var titleMatch = ItemTitle(item).Contains(query, StringComparison.OrdinalIgnoreCase);
        var childMatch = false;
        foreach (TreeViewItem child in item.Items)
            childMatch |= FilterBranchItem(child, query);

        if (titleMatch && !childMatch)
        {
            ShowBranchItem(item);
            item.IsExpanded = true;
            return true;
        }

        item.Visibility = childMatch ? Visibility.Visible : Visibility.Collapsed;
        item.IsExpanded = childMatch;
        return childMatch;
    }

    private static void ShowBranchItem(TreeViewItem item)
    {
        item.Visibility = Visibility.Visible;
        item.IsExpanded = false;
        foreach (TreeViewItem child in item.Items)
            ShowBranchItem(child);
    }

    private static bool ExpandToCurrent(TreeViewItem item)
    {
        if (item.Tag is GitBranchRef branch)
            return branch.IsCurrent;

        var found = false;
        foreach (TreeViewItem child in item.Items)
            found |= ExpandToCurrent(child);
        item.IsExpanded = found;
        return found;
    }

    private async void BranchTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_branchUiLock || BranchTree.SelectedItem is not TreeViewItem item || item.Tag is not GitBranchRef branch)
            return;

        BranchPopup.IsOpen = false;
        if (branch.Name.Equals(_currentBranch, StringComparison.OrdinalIgnoreCase))
            return;

        await SwitchToBranchAsync(branch);
    }

    private async Task SwitchToBranchAsync(GitBranchRef branch)
    {
        if (_git is null)
            return;

        if (_documents.Any(d => d.IsDirty) && !SaveAllDirtyDocuments())
        {
            await RefreshBranchUiAsync();
            return;
        }

        BranchButton.IsEnabled = false;
        try
        {
            SetStatus("переключение ветки…");
            var remote = branch.Remote ?? App.Settings.Current.RemoteName;
            var result = await _git.CheckoutBranchAsync(branch.Name, remote);
            if (!result.Success)
            {
                App.Log.Error($"checkout failed: {result.CombinedOutput}");
                SetStatus("ошибка ветки");
                MessageBox.Show(this, result.CombinedOutput, "Переключить ветку",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshBranchUiAsync();
                return;
            }

            App.Log.Info($"ветка: {branch.Name}");
            SetStatus("ветка переключена");
            RefreshTree();
            ReloadOpenDocumentsFromDisk();
            await RefreshBranchUiAsync();
            await RefreshChangesAsync();
        }
        finally
        {
            BranchButton.IsEnabled = true;
        }
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

    private void SetStatus(string text)
    {
        var shown = string.IsNullOrWhiteSpace(text) ? "Готов" : char.ToUpper(text[0]) + text[1..];
        StatusText.Text = shown;
        var brush = StatusBrushFor(text);
        StatusDot.Fill = brush;
        StatusText.Foreground = brush;
    }

    private static Brush StatusBrushFor(string text)
    {
        if (text.Contains("ошиб", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("не удалось", StringComparison.OrdinalIgnoreCase))
            return StatusErrorBrush;
        if (text.Contains('…') || text.Contains("...", StringComparison.Ordinal))
            return StatusBusyBrush;
        if (text.Contains("не найден", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("не настроен", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("не поддерж", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("отменено", StringComparison.OrdinalIgnoreCase))
            return StatusWarnBrush;
        return StatusOkBrush;
    }

    private void RenderLog()
    {
        var key = App.Log.Entries.Count == 0
            ? string.Empty
            : App.Log.Entries.Count + "|" + App.Log.Entries[0];
        if (key == _renderedLogKey)
            return;

        _renderedLogKey = key;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(8, 4, 8, 4),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = LogBox.Background
        };
        foreach (var line in App.Log.Entries)
            document.Blocks.Add(CreateLogParagraph(line));

        LogBox.Document = document;
        LogBox.ScrollToHome();
    }

    private static Paragraph CreateLogParagraph(string line)
    {
        var time = line;
        var level = "INFO";
        var message = string.Empty;
        var open = line.IndexOf('[');
        var close = open >= 0 ? line.IndexOf(']', open + 1) : -1;
        if (open > 0 && close > open)
        {
            time = line[..open].Trim();
            level = line[(open + 1)..close];
            message = line[(close + 1)..].TrimStart();
        }

        var levelBrush = level.ToUpperInvariant() switch
        {
            "ERROR" => StatusErrorBrush,
            "WARN" => StatusWarnBrush,
            _ => StatusBusyBrush
        };
        var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
        paragraph.Inlines.Add(new Run(time + "   ")
        {
            Foreground = (Brush)Application.Current.FindResource("FgMutedBrush")
        });
        paragraph.Inlines.Add(new Run(level.ToUpperInvariant())
        {
            Foreground = levelBrush,
            FontWeight = FontWeights.Bold
        });
        paragraph.Inlines.Add(new Run("    " + message)
        {
            Foreground = (Brush)Application.Current.FindResource("FgBrush")
        });
        return paragraph;
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

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null) return;
        SetStatus("pull…");
        var remote = App.Settings.Current.RemoteName;
        var result = await _git.PullCurrentBranchAsync(remote);
        if (result.Success)
        {
            var detail = OneLine(result.CombinedOutput);
            App.Log.Info(string.IsNullOrWhiteSpace(detail) ? $"pull ok ({remote})" : $"pull ok ({remote}): {detail}");
            SetStatus("pull ok");
            RefreshTree();
        }
        else
        {
            App.Log.Error($"pull failed: {result.CombinedOutput}");
            SetStatus("ошибка pull");
            MessageBox.Show(this, result.CombinedOutput, "Pull", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var folders = DiscoverScriptFolders(root);
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
        DeleteSelectedTreeNode();
    }

    private void DeleteFromTree_Click(object sender, RoutedEventArgs e) => DeleteSelectedTreeNode();

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

    }

    private void CreateFileFromTree_Click(object sender, RoutedEventArgs e) => ShowAddScriptDialog();

    private void CreateFolderFromTree_Click(object sender, RoutedEventArgs e)
    {
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        if (string.IsNullOrWhiteSpace(_contextFolderFullPath) || !Directory.Exists(_contextFolderFullPath))
            _contextFolderFullPath = root;

        var relative = Path.GetRelativePath(root, _contextFolderFullPath).Replace('\\', '/');
        var caption = relative == "." ? "Папка: корень проекта" : $"Папка: {relative}";
        var dlg = new NewFolderDialog(caption) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName))
            return;

        var name = dlg.FolderName.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) ||
            name is "." or "..")
        {
            MessageBox.Show(this, "Имя папки содержит недопустимые символы.", "Новая папка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fullPath = Path.Combine(_contextFolderFullPath, name);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            MessageBox.Show(this, "Такая папка или файл уже есть.", "Новая папка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            App.Log.Error($"не удалось создать папку: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Новая папка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        App.Log.Info($"папка создана: {fullPath}");
        RefreshTree();
        RevealInTree(fullPath);
        SetStatus($"создана папка: {name}");
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

    private void DeleteSelectedTreeNode()
    {
        if (ProjectTree.SelectedItem is not TreeViewItem { Tag: FileSystemNode node })
            return;

        var root = App.Settings.Current.ProjectPath;
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(Path.GetFullPath(node.FullPath), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            return;

        var prompt = node.IsDirectory
            ? $"Удалить папку «{node.Name}» и всё, что внутри?"
            : $"Удалить файл «{node.Name}»?";
        var answer = MessageBox.Show(this, prompt, "Удаление",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var toClose = _documents.Where(doc => IsInside(node.FullPath, doc.FullPath)).ToList();
        if (!CloseDocumentsForDelete(toClose))
            return;

        try
        {
            if (node.IsDirectory)
            {
                if (Directory.Exists(node.FullPath))
                    Directory.Delete(node.FullPath, recursive: true);
            }
            else if (File.Exists(node.FullPath))
            {
                File.Delete(node.FullPath);
            }
        }
        catch (Exception ex)
        {
            App.Log.Error($"не удалось удалить: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        App.Log.Info(node.IsDirectory ? $"папка удалена: {node.FullPath}" : $"файл удалён: {node.FullPath}");
        RefreshTree();
        _ = RefreshChangesAsync();
        SetStatus(node.IsDirectory ? $"удалена папка: {node.Name}" : $"удалён: {node.Name}");
    }

    private bool CloseDocumentsForDelete(IReadOnlyList<OpenDocument> docs)
    {
        foreach (var doc in docs)
        {
            if (!_documents.Contains(doc))
                continue;
            CloseDocument(doc);
            if (_documents.Contains(doc))
                return false;
        }

        return true;
    }

    private static bool IsInside(string folder, string path)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
            IsExpanded = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach (var child in node.Children)
            item.Items.Add(CreateChangeItem(child));
        return item;
    }

    private FrameworkElement CreateChangeHeader(ChangeNode node)
    {
        var icon = node.IsDirectory ? CreateFolderIcon() : CreateFileIcon();
        var label = new TextBlock
        {
            Text = node.Name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        row.Children.Add(icon);
        row.Children.Add(label);
        if (!node.IsDirectory && node.Entry is not null)
        {
            var kind = GitStatusFormat.KindOf(node.Entry.Status);
            var kindText = new TextBlock
            {
                Text = GitStatusFormat.Label(kind),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushForKind(kind)
            };
            Grid.SetColumn(kindText, 2);
            row.Children.Add(kindText);
        }

        var revert = CreateRevertButton(node);
        Grid.SetColumn(revert, 3);
        row.Children.Add(revert);
        return row;
    }

    private Button CreateRevertButton(ChangeNode node)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = UndoIconData,
            Fill = StatusBusyBrush,
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 14,
            IsHitTestVisible = false
        };
        var button = new Button
        {
            Content = icon,
            Width = 22,
            Height = 22,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Отменить изменения",
            Style = (Style)FindResource("IconButton")
        };
        button.Click += async (_, args) =>
        {
            args.Handled = true;
            await RevertNodeAsync(node);
        };
        return button;
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

    private async Task RevertNodeAsync(ChangeNode node)
    {
        if (_git is null)
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

        var closeAll = new MenuItem { Header = "Закрыть все" };
        var closeLeft = new MenuItem { Header = "Закрыть слева" };
        var closeRight = new MenuItem { Header = "Закрыть справа" };
        closeAll.Click += (_, _) => CloseDocuments(_documents.ToList());
        closeLeft.Click += (_, _) =>
        {
            var index = _documents.IndexOf(doc);
            if (index > 0)
                CloseDocuments(_documents.Take(index).ToList());
        };
        closeRight.Click += (_, _) =>
        {
            var index = _documents.IndexOf(doc);
            if (index >= 0 && index < _documents.Count - 1)
                CloseDocuments(_documents.Skip(index + 1).ToList());
        };
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Закрыть", CommandParameter = doc });
        ((MenuItem)menu.Items[0]).Click += (_, _) => CloseDocument(doc);
        menu.Items.Add(new Separator());
        menu.Items.Add(closeLeft);
        menu.Items.Add(closeRight);
        menu.Items.Add(closeAll);
        menu.Opened += (_, _) =>
        {
            var index = _documents.IndexOf(doc);
            closeLeft.IsEnabled = index > 0;
            closeRight.IsEnabled = index >= 0 && index < _documents.Count - 1;
            closeAll.IsEnabled = _documents.Count > 0;
        };
        tab.ContextMenu = menu;

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

    private void CloseDocuments(IReadOnlyList<OpenDocument> docs)
    {
        foreach (var doc in docs)
        {
            if (!_documents.Contains(doc))
                continue;
            var count = _documents.Count;
            CloseDocument(doc);
            if (_documents.Count == count)
                return;
        }
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

    private async void AutoPr_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null)
            return;

        AutoPrButton.IsEnabled = false;
        try
        {
            var settings = App.Settings.Current;
            if (!AutoPrTemplate.IsReady(settings))
            {
                if (!ShowSettingsDialog(openAutoPrTab: true))
                    return;

                await InitializeWorkspaceAsync();
                settings = App.Settings.Current;
                if (!AutoPrTemplate.IsReady(settings))
                {
                    SetStatus("шаблон Auto PR не настроен");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(settings.AzureDevOpsProject) ||
                string.IsNullOrWhiteSpace(settings.AzureDevOpsRepo))
            {
                MessageBox.Show(this, "Укажите Project и Repo в Настройках.", "Auto Pull Request",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_documents.Any(d => d.IsDirty) && !SaveAllDirtyDocuments())
                return;

            var changes = await _git.GetStatusEntriesAsync();
            var currentBranch = await _git.GetCurrentBranchAsync();
            if (string.IsNullOrWhiteSpace(currentBranch))
            {
                MessageBox.Show(this, "Не удалось определить текущую ветку.", "Auto Pull Request",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetStatus("авторизация…");
            var (session, authError) = await AzureDevOpsSession.OpenAsync(settings);
            if (session is null)
            {
                SetStatus("ошибка авторизации");
                MessageBox.Show(this, authError ?? "Не удалось авторизоваться в Azure DevOps.", "Auto Pull Request",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using var client = session.CreateClient(
                settings.AzureDevOpsOrgUrl,
                settings.AzureDevOpsProject,
                settings.AzureDevOpsRepo);
            SetStatus("проверка PR…");
            var (prChecked, activePr) = await client.FindActivePullRequestAsync(currentBranch);
            var hasActivePr = prChecked && activePr is not null && !string.IsNullOrWhiteSpace(activePr.Url);
            if (hasActivePr && changes.Count == 0)
            {
                App.Log.Info(activePr!.Message + " " + activePr.Url);
                SetStatus("активный PR");
                var text = string.IsNullOrWhiteSpace(activePr.Title) ? "скрипты" : activePr.Title;
                var id = activePr.PullRequestId is null ? string.Empty : $" #{activePr.PullRequestId}";
                ShowTelegramResult("Активный PR создан" + id, activePr.Message, activePr, text, settings);
                return;
            }

            if (!prChecked)
                App.Log.Warn("не удалось проверить активные PR");

            var choice = new AutoPrBranchChoiceDialog(currentBranch, changes.Count, hasActivePr) { Owner = this };
            if (choice.ShowDialog() != true)
                return;

            if (choice.Choice == AutoPrBranchChoice.Current)
            {
                if (currentBranch.Equals(settings.AutoBaseBranch, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this,
                        "Текущая ветка совпадает с базовой. Создайте PR из новой ветки.",
                        "Auto Pull Request",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var title = changes.Count > 0
                    ? AutoPrTemplate.SuggestTitle(changes.Select(c => c.Path))
                    : currentBranch;
                var commitMessage = changes.Count > 0 ? $"{currentBranch}: {title}" : null;
                if (!await CommitAndPushAsync(settings, currentBranch, commitMessage))
                    return;

                var prTitle = changes.Count > 0 ? $"{currentBranch}: {title}" : currentBranch;
                await PublishAutoPullRequestAsync(
                    client, settings, currentBranch, settings.AutoBaseBranch, prTitle, title);
                return;
            }

            await CreateAutoPrOnNewBranchAsync(client, settings, changes);
        }
        finally
        {
            AutoPrButton.IsEnabled = true;
        }
    }

    private async Task CreateAutoPrOnNewBranchAsync(
        AzureDevOpsClient client,
        AppSettings settings,
        IReadOnlyList<GitStatusEntry> changes)
    {
        var title = changes.Count > 0
            ? AutoPrTemplate.SuggestTitle(changes.Select(c => c.Path))
            : "скрипты";
        var branch = AutoPrTemplate.ExpandBranchName(
            settings.AutoBranchNameTemplate,
            settings.AutoBaseBranch,
            DateTime.Now);
        if (string.IsNullOrWhiteSpace(branch)
            || branch.Equals(settings.AutoBaseBranch, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Шаблон имени ветки дал пустое или совпадающее с базой имя.", "Auto Pull Request",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus("сохранение изменений…");
        var stash = await _git!.StashPushAsync();
        if (!stash.Success)
        {
            ShowPrError($"git stash: {stash.CombinedOutput}");
            return;
        }

        var hadStash = !stash.CombinedOutput.Contains("No local changes", StringComparison.OrdinalIgnoreCase);
        SetStatus("создание ветки…");
        var created = await _git.CreateBranchAsync(branch, settings.AutoBaseBranch, settings.RemoteName);
        RefreshTree();
        await RefreshBranchUiAsync();
        if (!created.Success)
        {
            if (hadStash)
                await _git.StashPopAsync();
            RefreshTree();
            await RefreshChangesAsync();
            ShowPrError($"ветка: {created.CombinedOutput}");
            return;
        }

        if (hadStash)
        {
            var pop = await _git.StashPopAsync();
            if (!pop.Success)
            {
                RefreshTree();
                await RefreshChangesAsync();
                ShowPrError($"Ветка создана, но изменения не применились: {pop.CombinedOutput}");
                return;
            }
        }

        var commitMessage = changes.Count > 0 ? $"{branch}: {title}" : null;
        if (!await CommitAndPushAsync(settings, branch, commitMessage))
            return;

        var prTitle = changes.Count > 0 ? $"{branch}: {title}" : branch;
        await PublishAutoPullRequestAsync(client, settings, branch, settings.AutoBaseBranch, prTitle, title);
    }

    private async Task<bool> CommitAndPushAsync(AppSettings settings, string branch, string? commitMessage)
    {
        if (!string.IsNullOrWhiteSpace(commitMessage))
        {
            SetStatus("commit…");
            var add = await _git!.AddAsync(new[] { "." });
            if (!add.Success)
            {
                ShowPrError($"git add: {add.CombinedOutput}");
                return false;
            }

            var commit = await _git.CommitAsync(commitMessage);
            if (!commit.Success)
            {
                ShowPrError($"git commit: {commit.CombinedOutput}");
                return false;
            }

            App.Log.Info($"commit ok: {commitMessage}");
        }

        SetStatus("push…");
        var push = await _git!.PushAsync(settings.RemoteName, branch, setUpstream: true);
        if (!push.Success)
        {
            App.Log.Error($"push failed: {push.CombinedOutput}");
            ShowPrError($"git push: {push.CombinedOutput}");
            return false;
        }

        App.Log.Info("push ok");
        return true;
    }

    private async Task PublishAutoPullRequestAsync(
        AzureDevOpsClient client,
        AppSettings settings,
        string sourceBranch,
        string targetBranch,
        string prTitle,
        string text)
    {
        SetStatus("создание PR…");
        var reviewers = (settings.AutoReviewers ?? new List<SavedReviewer>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => r.Copy())
            .ToList();
        var (prChecked, activePr) = await client.FindActivePullRequestAsync(sourceBranch);
        PullRequestResult pr;
        var existed = false;
        if (prChecked && activePr is not null && !string.IsNullOrWhiteSpace(activePr.Url))
        {
            pr = activePr;
            existed = true;
            App.Log.Info(activePr.Message + " " + activePr.Url);
            SetStatus("PR уже активен");
        }
        else
        {
            if (!prChecked)
                App.Log.Warn("не удалось проверить активные PR, создаём новый");

            App.Log.Info(FormatReviewers(reviewers));
            pr = await client.CreatePullRequestAsync(sourceBranch, targetBranch, prTitle, prTitle, reviewers);
            if (!pr.Success)
            {
                App.Log.Error(pr.Message);
                SetStatus("ошибка PR");
                new PullRequestResultDialog("Не удалось создать Pull Request", pr.Message, null, success: false)
                { Owner = this }.ShowDialog();
                return;
            }

            App.Log.Info(pr.Message + (pr.Url is null ? string.Empty : $" {pr.Url}"));
            SetStatus("PR создан");
        }

        await RefreshChangesAsync();
        var id = pr.PullRequestId is null ? string.Empty : $" #{pr.PullRequestId}";
        var heading = existed ? "Активный PR создан" + id : "Pull Request" + id;
        ShowTelegramResult(
            heading,
            $"Status: Active\n{pr.Message}\n{FormatReviewers(reviewers)}",
            pr,
            string.IsNullOrWhiteSpace(pr.Title) ? text : pr.Title,
            settings);
    }

    private void ShowTelegramResult(string heading, string status, PullRequestResult pr, string text, AppSettings settings)
    {
        var caption = AutoPrTemplate.LinkCaptionOrDefault(settings.TelegramLinkCaption);
        new PullRequestResultDialog(
            heading,
            status,
            pr.Url,
            success: true,
            telegram: new TelegramCompose
            {
                Template = settings.TelegramMessageTemplate ?? string.Empty,
                Text = text,
                LinkCaption = caption,
                LinkUrl = pr.Url ?? string.Empty,
                Tags = settings.TelegramTags ?? string.Empty,
                Custom = settings.TelegramCustomTemplates,
                TimeTexts = settings.TelegramTimeTexts,
                TimeZoneId = settings.TelegramTimeZoneId
            })
        { Owner = this }.ShowDialog();
    }

    private async void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || _git is null)
            return;

        var mapping = ReloadUseMapping(showError: true);
        var folders = DiscoverScriptFolders(root);
        var dialog = new QuickAddDialog(mapping, _mappingError, root, folders)
        { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (!await EnsureWorkBranchAsync())
            return;

        var wrote = TryWriteQuickScripts(root, dialog.Plans, out var firstPath);
        if (firstPath is not null)
        {
            RefreshTree();
            _ = RefreshChangesAsync();
            OpenFile(firstPath);
        }

        if (!wrote)
            return;

        App.Log.Info($"быстрое добавление: файлов {dialog.Plans.Count}");
        SetStatus("скрипты сохранены");
        if (dialog.CreatePullRequest)
            AutoPr_Click(AutoPrButton, new RoutedEventArgs());
    }

    private string? _mappingError;

    private UseFolderMapping ReloadUseMapping(bool showError)
    {
        var root = App.Settings.Current.ProjectPath;
        var mapping = UseFolderMappingFile.Load(out _mappingError);
        if (_mappingError is not null)
        {
            App.Log.Error("mapping: " + _mappingError);
            if (showError)
                MessageBox.Show(this, _mappingError, "Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (showError)
        {
            var found = File.Exists(mapping.FilePath) ? mapping.Mappings.Count.ToString() : "файл ещё не создан";
            App.Log.Info($"mapping обновлён: {found}");
            SetStatus("mapping обновлён");
        }

        return mapping;
    }

    private void ReloadMapping_Click(object sender, RoutedEventArgs e) => ReloadUseMapping(showError: true);

    private void OpenMappingFolder_Click(object sender, RoutedEventArgs e)
    {
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;
        var mapping = ReloadUseMapping(showError: false);
        var dir = Path.GetDirectoryName(mapping.FilePath);
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        if (!File.Exists(mapping.FilePath))
            UseFolderMappingFile.Save(mapping);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void AnalyzeUse_Click(object sender, RoutedEventArgs e)
    {
        var root = App.Settings.Current.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        SetStatus("анализ USE…");
        var report = UseFolderAnalyzer.Analyze(root, UseFolderAnalyzer.ScriptRootFolder);
        var mapping = ReloadUseMapping(showError: false);
        var dialog = new UseMappingReportDialog(report, mapping) { Owner = this };
        dialog.ShowDialog();
        if (dialog.MappingChanged)
            ReloadUseMapping(showError: false);
        SetStatus("готов");
    }

    private IReadOnlyList<string> DiscoverScriptFolders(string root)
    {
        var scope = Path.Combine(root, UseFolderAnalyzer.ScriptRootFolder);
        if (!Directory.Exists(scope))
            return _discovery.DiscoverWorkFolders(root, maxDepth: 8);

        var folders = new List<string> { UseFolderAnalyzer.ScriptRootFolder };
        foreach (var folder in _discovery.DiscoverWorkFolders(scope, maxDepth: 8))
        {
            var relative = folder.Replace('\\', '/').Trim('/');
            if (relative.Length == 0 || relative == ".")
                continue;
            folders.Add(UseFolderAnalyzer.ScriptRootFolder + "/" + relative);
        }

        return folders;
    }

    private async Task<bool> EnsureWorkBranchAsync()
    {
        if (_git is null)
            return false;

        var settings = App.Settings.Current;
        var current = await _git.GetCurrentBranchAsync();
        var bases = new[] { settings.LastBaseBranch, settings.AutoBaseBranch, "master", "main" }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (!string.IsNullOrWhiteSpace(current) &&
            !bases.Any(name => name.Equals(current, StringComparison.OrdinalIgnoreCase)))
            return true;

        var baseBranch = !string.IsNullOrWhiteSpace(settings.AutoBaseBranch)
            ? settings.AutoBaseBranch
            : settings.LastBaseBranch;
        if (string.IsNullOrWhiteSpace(baseBranch))
            baseBranch = string.IsNullOrWhiteSpace(current) ? "master" : current;
        var template = string.IsNullOrWhiteSpace(settings.AutoBranchNameTemplate)
            ? AutoPrTemplate.DefaultBranchTemplate
            : settings.AutoBranchNameTemplate;
        var name = AutoPrTemplate.ExpandBranchName(template, baseBranch, DateTime.Now);
        var answer = MessageBox.Show(this,
            $"Сейчас ветка «{current ?? baseBranch}». Записать скрипты в новую ветку «{name}»?",
            "Быстрое добавление", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return false;

        SetStatus("создание ветки…");
        var created = await _git.CreateBranchAsync(name, baseBranch, settings.RemoteName);
        RefreshTree();
        await RefreshBranchUiAsync();
        if (!created.Success)
        {
            App.Log.Error($"ветка: {created.CombinedOutput}");
            MessageBox.Show(this, created.CombinedOutput, "Быстрое добавление",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        App.Log.Info($"ветка создана: {name} от {baseBranch}");
        return true;
    }

    private bool TryWriteQuickScripts(string root, IReadOnlyList<QuickScriptPlan> plans, out string? firstPath)
    {
        firstPath = null;
        var missing = plans
            .Select(plan => Path.GetFullPath(Path.Combine(root, plan.RelativeFolder.Replace('/', Path.DirectorySeparatorChar))))
            .Where(dir => !Directory.Exists(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
        {
            var list = string.Join(Environment.NewLine, missing.Select(dir => Path.GetRelativePath(root, dir)));
            var create = MessageBox.Show(this,
                "Этих папок ещё нет. Создать их?\n\n" + list,
                "Быстрое добавление", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (create != MessageBoxResult.Yes)
                return false;
        }

        var rootFull = Path.GetFullPath(root);
        foreach (var plan in plans)
        {
            string path;
            try
            {
                var dir = Path.GetFullPath(Path.Combine(rootFull, plan.RelativeFolder.Replace('/', Path.DirectorySeparatorChar)));
                if (!dir.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                    !dir.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, $"Папка вне проекта: {plan.RelativeFolder}", "Быстрое добавление",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, plan.FileName);
                if (File.Exists(path))
                {
                    var choice = MessageBox.Show(this,
                        $"Файл уже есть:\n{Path.GetRelativePath(rootFull, path)}\n\nДа — перезаписать.\nНет — сохранить под новым именем.\nОтмена — прервать.",
                        "Быстрое добавление", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (choice == MessageBoxResult.Cancel)
                        return false;
                    if (choice == MessageBoxResult.No)
                        path = NextFreePath(path);
                }

                File.WriteAllText(path, plan.Sql ?? string.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Быстрое добавление",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            firstPath ??= path;
        }

        return true;
    }

    private static string NextFreePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMddHHmm}{ext}");
    }

    private async void CreatePr_Click(object sender, RoutedEventArgs e) => await RunCreatePrAsync();

    private async Task RunCreatePrAsync()
    {
        if (_git is null)
            return;

        var settings = App.Settings.Current;
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

        SetStatus("авторизация…");
        var (session, authError) = await AzureDevOpsSession.OpenAsync(settings);
        if (session is null)
        {
            SetStatus("ошибка авторизации");
            MessageBox.Show(this, authError ?? "Не удалось авторизоваться в Azure DevOps.", "Create PR",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus("ревьюверы…");
        var reviewers = ChooseReviewers(settings, session);
        if (reviewers is null)
        {
            SetStatus("создание PR отменено");
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

        using var client = session.CreateClient(
            settings.AzureDevOpsOrgUrl,
            settings.AzureDevOpsProject,
            settings.AzureDevOpsRepo);

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

        var reviewerLine = FormatReviewers(reviewers);
        App.Log.Info(reviewerLine);
        var pr = await client.CreatePullRequestAsync(branch, baseBranch, title, title, reviewers);
        if (!pr.Success)
        {
            App.Log.Error(pr.Message);
            SetStatus("ошибка PR");
            var failDlg = new PullRequestResultDialog(
                "Не удалось создать Pull Request",
                pr.Message + Environment.NewLine + reviewerLine,
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
            $"Status: Active\n{pr.Message}\n{reviewerLine}",
            pr.Url,
            success: true)
        { Owner = this };
        okDlg.ShowDialog();
    }

    private IReadOnlyList<SavedReviewer>? ChooseReviewers(AppSettings settings, AzureDevOpsSession session)
    {
        var dialog = new PullRequestReviewersDialog(
            settings.AzureDevOpsOrgUrl,
            settings.AzureDevOpsProject,
            settings.AzureDevOpsRepo,
            session.Authorization,
            settings.SavedReviewers)
        { Owner = this };

        if (dialog.ShowDialog() != true)
            return null;

        var selected = dialog.SelectedReviewers.Select(r => r.Copy()).ToList();
        settings.SavedReviewers = selected.Select(r => r.Copy()).ToList();
        App.Settings.Save(settings);
        return selected;
    }

    private static string FormatReviewers(IReadOnlyList<SavedReviewer> reviewers)
    {
        if (reviewers.Count == 0)
            return "Ревьюверы не назначены.";

        var parts = reviewers.Select(r =>
        {
            var name = string.IsNullOrWhiteSpace(r.DisplayName) ? r.UniqueName : r.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
                name = r.Id;
            return r.IsRequired ? $"{name} (обязательный)" : name;
        });
        return "Ревьюверы: " + string.Join(", ", parts);
    }

    private void ShowPrError(string message)
    {
        SetStatus("ошибка PR");
        MessageBox.Show(this, message, "Create PR", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RefreshAuthStatus()
    {
        var settings = App.Settings.Current;
        if (settings.AuthMode == AzureAuthMode.MicrosoftAccount)
        {
            var name = MicrosoftAccountAuthService.GetSignedInUsername();
            AuthStatusText.Text = string.IsNullOrWhiteSpace(name) ? "вход: Microsoft" : $"вход: {name}";
            return;
        }

        AuthStatusText.Text = App.Settings.HasPat() ? "вход: PAT" : "вход: PAT не задан";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowSettingsDialog())
            return;
        _ = InitializeWorkspaceAsync();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowSettingsDialog(openMicrosoftLogin: true))
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
