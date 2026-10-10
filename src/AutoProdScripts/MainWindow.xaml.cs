using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoProdScripts.Models;
using AutoProdScripts.Services.AzureDevOps;
using AutoProdScripts.Services.Discovery;
using AutoProdScripts.Services.Git;
using AutoProdScripts.Themes;
using AutoProdScripts.Views;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AutoProdScripts;

public partial class MainWindow : Window
{
    private readonly ProjectDiscoveryService _discovery = new();
    private GitCliService? _git;
    private string? _currentFilePath;
    private bool _dirty;
    private string? _currentBranch;
    private string? _selectedFolderRel;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = App.Log.Entries;
        SqlEditor.TextChanged += (_, _) => _dirty = true;
        Loaded += async (_, _) => await OnLoadedAsync();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
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

    private static TreeViewItem CreateTreeItem(FileSystemNode node)
    {
        var item = new TreeViewItem
        {
            Header = node.Name,
            Tag = node,
            IsExpanded = node.IsDirectory && node.Children.Count > 0 && node.Children.Count < 20
        };
        if (node.IsDirectory)
        {
            foreach (var child in node.Children)
                item.Items.Add(CreateTreeItem(child));
        }

        return item;
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
        ChangesList.Items.Clear();
        if (_git is null)
            return;

        var entries = await _git.GetStatusEntriesAsync();
        foreach (var e in entries)
            ChangesList.Items.Add($"{e.Status}  {e.Path}");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null) return;
        SetStatus("fetch…");
        var remote = App.Settings.Current.RemoteName;
        var result = await _git.FetchAsync(remote);
        if (result.Success)
        {
            App.Log.Info($"fetch ok ({remote})");
            SetStatus("fetch ok");
        }
        else
        {
            App.Log.Error($"fetch failed: {result.CombinedOutput}");
            SetStatus("ошибка fetch");
            MessageBox.Show(this, result.CombinedOutput, "Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await RefreshBranchUiAsync();
        await RefreshChangesAsync();
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
        var result = await _git.CreateBranchAsync(dlg.NewBranchName, dlg.SelectedBaseBranch);
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

    private void OpenFile(string path)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "Сохранить изменения текущего файла?", "Файл изменён",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
                return;
            if (answer == MessageBoxResult.Yes)
                SaveCurrentFile();
        }

        try
        {
            SqlEditor.SyntaxHighlighting = SqlHighlightingLoader.GetDefinition()
                                           ?? HighlightingManager.Instance.GetDefinition("TSQL")
                                           ?? HighlightingManager.Instance.GetDefinition("SQL");
            SqlEditor.Load(path);
            _currentFilePath = path;
            _dirty = false;
            EditorPathText.Text = path;
            SetStatus($"открыт: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            App.Log.Error($"не удалось открыть файл: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrentFile();

    private void SaveCurrentFile()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            MessageBox.Show(this, "Нет открытого файла.", "Сохранение",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SqlEditor.Save(_currentFilePath);
            _dirty = false;
            App.Log.Info($"сохранён: {_currentFilePath}");
            SetStatus("сохранён");
            _ = RefreshChangesAsync();
        }
        catch (Exception ex)
        {
            App.Log.Error($"ошибка сохранения: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

        // Commit pending changes if any
        var changes = await _git.GetStatusEntriesAsync();
        if (changes.Count > 0)
        {
            if (_dirty)
                SaveCurrentFile();

            var message = $"{branch}: обновление SQL-скриптов";
            var confirmCommit = MessageBox.Show(this,
                $"Есть незакоммиченные изменения ({changes.Count}).\nСообщение commit:\n{message}\n\nЗакоммитить и продолжить?",
                "Create PR", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (confirmCommit == MessageBoxResult.Cancel)
                return;
            if (confirmCommit == MessageBoxResult.Yes)
            {
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
        SetStatus("создание PR…");

        var title = $"{branch}";
        if (!string.IsNullOrWhiteSpace(_currentFilePath))
            title = $"{branch}: {Path.GetFileName(_currentFilePath)}";

        using var client = new AzureDevOpsClient(
            settings.AzureDevOpsOrgUrl,
            settings.AzureDevOpsProject,
            settings.AzureDevOpsRepo,
            pat);

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
}
