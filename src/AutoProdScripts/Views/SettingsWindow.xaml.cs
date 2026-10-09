using System.IO;
using System.Windows;
using AutoProdScripts.Models;
using AutoProdScripts.Services.Discovery;
using AutoProdScripts.Services.Git;

namespace AutoProdScripts.Views;

public partial class SettingsWindow : Window
{
    public AppSettings? ResultSettings { get; private set; }
    public string? ResultPat { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var s = App.Settings.Current;
        ProjectPathBox.Text = s.ProjectPath;
        OrgUrlBox.Text = string.IsNullOrWhiteSpace(s.AzureDevOpsOrgUrl)
            ? "https://cit-damu.visualstudio.com/"
            : s.AzureDevOpsOrgUrl;
        ProjectBox.Text = s.AzureDevOpsProject;
        RepoBox.Text = s.AzureDevOpsRepo;
        BaseBranchBox.Text = s.LastBaseBranch;

        var existingPat = App.Settings.LoadPat();
        if (!string.IsNullOrEmpty(existingPat))
            PatBox.Password = existingPat;

        await RefreshProjectHintsAsync();
        await RefreshBranchesAsync();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку репозитория ProductionScripts"
        };
        if (dlg.ShowDialog(this) == true)
        {
            ProjectPathBox.Text = dlg.FolderName;
            await RefreshProjectHintsAsync();
            await RefreshBranchesAsync();
            await TryFillFromRemoteAsync();
        }
    }

    private Task RefreshProjectHintsAsync()
    {
        var path = ProjectPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ProjectPathHint.Text = "Укажите существующую папку репозитория.";
            GitHint.Text = string.Empty;
            return Task.CompletedTask;
        }

        var git = new GitCliService(path);
        if (!git.IsGitAvailable(out var gitMsg))
        {
            GitHint.Text = gitMsg;
            ProjectPathHint.Text = "Папка выбрана, но git недоступен.";
            return Task.CompletedTask;
        }

        GitHint.Text = gitMsg;
        if (!git.IsRepository(out var repoMsg))
        {
            ProjectPathHint.Text = repoMsg;
            return Task.CompletedTask;
        }

        var discovery = new ProjectDiscoveryService();
        var folders = discovery.DiscoverWorkFolders(path);
        ProjectPathHint.Text = $"✓ Найден .git  ·  обнаружено папок: {folders.Count}";
        return Task.CompletedTask;
    }

    private async Task RefreshBranchesAsync()
    {
        BaseBranchBox.Items.Clear();
        var path = ProjectPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        var git = new GitCliService(path);
        if (!git.IsGitAvailable(out _) || !git.IsRepository(out _))
            return;

        var branches = await git.ListBranchesAsync();
        foreach (var b in branches)
            BaseBranchBox.Items.Add(b.Name);

        var last = App.Settings.Current.LastBaseBranch;
        if (!string.IsNullOrWhiteSpace(last))
            BaseBranchBox.Text = last;
        else if (branches.Any(x => x.Name.Equals("master", StringComparison.OrdinalIgnoreCase)))
            BaseBranchBox.Text = "master";
        else if (branches.Count > 0)
            BaseBranchBox.Text = branches[0].Name;
    }

    private async Task TryFillFromRemoteAsync()
    {
        var path = ProjectPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        var git = new GitCliService(path);
        var url = await git.GetRemoteUrlAsync("origin");
        if (string.IsNullOrWhiteSpace(url))
            return;

        // Try parse: https://dev.azure.com/{org}/{project}/_git/{repo}
        // or https://{org}.visualstudio.com/{project}/_git/{repo}
        try
        {
            var uri = new Uri(url.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase));
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uri.Host.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                OrgUrlBox.Text = $"{uri.Scheme}://{uri.Host}/";
                if (parts.Length >= 1 && string.IsNullOrWhiteSpace(ProjectBox.Text))
                    ProjectBox.Text = parts[0];
                if (parts.Length >= 3 && parts[1].Equals("_git", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(RepoBox.Text))
                    RepoBox.Text = parts[2];
            }
            else if (uri.Host.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
            {
                OrgUrlBox.Text = $"{uri.Scheme}://{uri.Host}/{parts[0]}/";
                if (string.IsNullOrWhiteSpace(ProjectBox.Text))
                    ProjectBox.Text = parts[1];
                if (string.IsNullOrWhiteSpace(RepoBox.Text))
                    RepoBox.Text = parts[^1];
            }
        }
        catch
        {
            // ignore parse errors
        }
    }

    private async void ValidatePat_Click(object sender, RoutedEventArgs e)
    {
        var pat = PatBox.Password;
        if (string.IsNullOrWhiteSpace(pat))
        {
            MessageBox.Show(this, "Введите PAT.", "Проверка PAT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var client = new Services.AzureDevOps.AzureDevOpsClient(
            OrgUrlBox.Text,
            ProjectBox.Text,
            RepoBox.Text,
            pat);

        var (ok, message) = await client.ValidatePatAsync();
        PatHint.Text = message;
        MessageBox.Show(this, message, "Проверка PAT", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = ProjectPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(this, "Укажите корректный путь к проекту.", "Настройка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(PatBox.Password) && !App.Settings.HasPat())
        {
            MessageBox.Show(this, "Укажите PAT Azure DevOps.", "Настройка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultSettings = new AppSettings
        {
            ProjectPath = path,
            AzureDevOpsOrgUrl = OrgUrlBox.Text.Trim(),
            AzureDevOpsProject = ProjectBox.Text.Trim(),
            AzureDevOpsRepo = RepoBox.Text.Trim(),
            LastBaseBranch = string.IsNullOrWhiteSpace(BaseBranchBox.Text) ? "master" : BaseBranchBox.Text.Trim(),
            RemoteName = "origin",
            IsConfigured = true
        };
        ResultPat = string.IsNullOrWhiteSpace(PatBox.Password) ? null : PatBox.Password;
        DialogResult = true;
        Close();
    }
}
