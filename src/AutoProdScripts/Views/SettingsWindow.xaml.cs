using System.IO;
using System.Windows;
using System.Windows.Media;
using AutoProdScripts.Models;
using AutoProdScripts.Services.AzureDevOps;
using AutoProdScripts.Services.Discovery;
using AutoProdScripts.Services.Git;

namespace AutoProdScripts.Views;

public partial class SettingsWindow : Window
{
    private readonly bool _openMicrosoftLogin;
    private readonly bool _openAutoPrTab;
    private bool _microsoftAuth;
    private bool _autoTemplateTouched;
    private List<SavedReviewer> _autoReviewers = new();
    private readonly List<CustomTemplateRow> _customTemplates = new();

    public AppSettings? ResultSettings { get; private set; }
    public string? ResultPat { get; private set; }

    public SettingsWindow(bool openMicrosoftLogin = false, bool openAutoPrTab = false)
    {
        _openMicrosoftLogin = openMicrosoftLogin;
        _openAutoPrTab = openAutoPrTab;
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
        _autoReviewers = (s.AutoReviewers ?? new List<SavedReviewer>()).Select(r => r.Copy()).ToList();
        AutoBaseBranchBox.Text = string.IsNullOrWhiteSpace(s.AutoBaseBranch)
            ? (string.IsNullOrWhiteSpace(s.LastBaseBranch) ? "master" : s.LastBaseBranch)
            : s.AutoBaseBranch;
        AutoBranchTemplateBox.Text = string.IsNullOrWhiteSpace(s.AutoBranchNameTemplate)
            ? AutoPrTemplate.DefaultBranchTemplate
            : s.AutoBranchNameTemplate;
        TelegramTemplateBox.Text = string.IsNullOrWhiteSpace(s.TelegramMessageTemplate)
            ? AutoPrTemplate.DefaultTelegramTemplate
            : s.TelegramMessageTemplate;
        TelegramTagsBox.Text = s.TelegramTags ?? string.Empty;
        foreach (var part in s.TelegramCustomTemplates ?? new List<TelegramCustomTemplate>())
        {
            if (!string.IsNullOrWhiteSpace(part.Name))
                AddCustomTemplateRow(part.Name.Trim(), part.Value);
        }
        UpdateAutoReviewersLabel();
        UpdateAutoPreview();
        _microsoftAuth = _openMicrosoftLogin || s.AuthMode == AzureAuthMode.MicrosoftAccount;
        ApplyAuthModePanels();
        RefreshMicrosoftAccountLabel();
        if (_openAutoPrTab)
            SettingsTabs.SelectedItem = AutoPrTab;
        else if (_microsoftAuth)
            MicrosoftSignInButton.Focus();

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
        var autoBaseBefore = AutoBaseBranchBox.Text;
        BaseBranchBox.Items.Clear();
        AutoBaseBranchBox.Items.Clear();
        var path = ProjectPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        var git = new GitCliService(path);
        if (!git.IsGitAvailable(out _) || !git.IsRepository(out _))
            return;

        var branches = await git.ListBranchesAsync();
        foreach (var b in branches)
        {
            BaseBranchBox.Items.Add(b.Name);
            AutoBaseBranchBox.Items.Add(b.Name);
        }

        var last = App.Settings.Current.LastBaseBranch;
        if (!string.IsNullOrWhiteSpace(last))
            BaseBranchBox.Text = last;
        else if (branches.Any(x => x.Name.Equals("master", StringComparison.OrdinalIgnoreCase)))
            BaseBranchBox.Text = "master";
        else if (branches.Count > 0)
            BaseBranchBox.Text = branches[0].Name;

        var autoBase = string.IsNullOrWhiteSpace(autoBaseBefore)
            ? BaseBranchBox.Text
            : autoBaseBefore;
        if (!string.IsNullOrWhiteSpace(autoBase))
            AutoBaseBranchBox.Text = autoBase;
        UpdateAutoPreview();
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

        using var client = new AzureDevOpsClient(
            OrgUrlBox.Text,
            ProjectBox.Text,
            RepoBox.Text,
            AzureDevOpsAuthorization.FromPat(pat));

        var (ok, message) = await client.ValidateConnectionAsync();
        PatHint.Text = message;
        MessageBox.Show(this, message, "Проверка PAT", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private void SelectPatAuth_Click(object sender, RoutedEventArgs e)
    {
        _microsoftAuth = false;
        ApplyAuthModePanels();
    }

    private void SelectMicrosoftAuth_Click(object sender, RoutedEventArgs e)
    {
        _microsoftAuth = true;
        ApplyAuthModePanels();
        MicrosoftSignInButton.Focus();
    }

    private void ApplyAuthModePanels()
    {
        if (PatPanel is null || MicrosoftPanel is null || PatAuthButton is null || MicrosoftAuthButton is null)
            return;

        PatPanel.Visibility = _microsoftAuth ? Visibility.Collapsed : Visibility.Visible;
        MicrosoftPanel.Visibility = _microsoftAuth ? Visibility.Visible : Visibility.Collapsed;
        PatAuthButton.Background = _microsoftAuth
            ? (Brush)FindResource("InputBrush")
            : (Brush)FindResource("AccentBrush");
        MicrosoftAuthButton.Background = _microsoftAuth
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("InputBrush");
    }

    private void RefreshMicrosoftAccountLabel()
    {
        if (MicrosoftAccountText is null)
            return;

        var name = MicrosoftAccountAuthService.GetSignedInUsername();
        MicrosoftAccountText.Text = string.IsNullOrWhiteSpace(name) ? "Вход не выполнен" : name;
    }

    private async void MicrosoftSignIn_Click(object sender, RoutedEventArgs e)
    {
        MicrosoftSignInButton.IsEnabled = false;
        MicrosoftSignOutButton.IsEnabled = false;
        try
        {
            MicrosoftStatusText.Text = "В браузере открыт вход Microsoft. Это окно можно не закрывать.";
            var (ok, message) = await MicrosoftAccountAuthService.SignInAsync(
                OrgUrlBox.Text,
                ProjectBox.Text,
                RepoBox.Text);
            MicrosoftStatusText.Text = message;
            MessageBox.Show(this, message, "Учётная запись Microsoft", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            MicrosoftSignInButton.IsEnabled = true;
            MicrosoftSignOutButton.IsEnabled = true;
            RefreshMicrosoftAccountLabel();
        }
    }

    private void MicrosoftSignOut_Click(object sender, RoutedEventArgs e)
    {
        MicrosoftAccountAuthService.SignOut();
        MicrosoftStatusText.Text = "Выход выполнен. При следующем входе можно выбрать другую учётную запись.";
        RefreshMicrosoftAccountLabel();
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

        var authMode = _microsoftAuth
            ? AzureAuthMode.MicrosoftAccount
            : AzureAuthMode.Pat;
        if (authMode == AzureAuthMode.Pat)
        {
            if (string.IsNullOrWhiteSpace(PatBox.Password) && !App.Settings.HasPat())
            {
                MessageBox.Show(this, "Укажите PAT Azure DevOps.", "Настройка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (!MicrosoftAccountAuthService.HasSignedInAccount())
        {
            MessageBox.Show(this, "Войдите в учётную запись Microsoft.", "Настройка",
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
            IsConfigured = true,
            AuthMode = authMode,
            SavedReviewers = (App.Settings.Current.SavedReviewers ?? new List<SavedReviewer>())
                .Select(r => r.Copy())
                .ToList(),
            AutoBaseBranch = AutoBaseBranchBox.Text.Trim(),
            AutoBranchNameTemplate = AutoBranchTemplateBox.Text.Trim(),
            TelegramMessageTemplate = TelegramTemplateBox.Text,
            TelegramTags = TelegramTagsBox.Text.Trim(),
            TelegramCustomTemplates = _customTemplates
                .Select(row => new TelegramCustomTemplate { Name = row.Name, Value = row.ValueBox.Text })
                .ToList(),
            AutoReviewers = _autoReviewers.Select(r => r.Copy()).ToList(),
            AutoPrTemplateConfigured = App.Settings.Current.AutoPrTemplateConfigured
                || (_autoTemplateTouched
                    && !string.IsNullOrWhiteSpace(AutoBaseBranchBox.Text)
                    && !string.IsNullOrWhiteSpace(AutoBranchTemplateBox.Text))
        };
        ResultPat = string.IsNullOrWhiteSpace(PatBox.Password) ? null : PatBox.Password;
        DialogResult = true;
        Close();
    }

    private void SettingsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SettingsTabs))
            return;
        if (ReferenceEquals(SettingsTabs.SelectedItem, AutoPrTab))
            _autoTemplateTouched = true;
    }

    private void AutoTemplate_Changed(object sender, RoutedEventArgs e) => UpdateAutoPreview();

    private void UpdateAutoPreview()
    {
        if (BranchPreviewText is null || TelegramPreviewBox is null)
            return;

        var baseBranch = string.IsNullOrWhiteSpace(AutoBaseBranchBox.Text) ? "master" : AutoBaseBranchBox.Text.Trim();
        var branch = AutoPrTemplate.ExpandBranchName(AutoBranchTemplateBox.Text, baseBranch, DateTime.Now);
        BranchPreviewText.Text = string.IsNullOrWhiteSpace(branch)
            ? "Пример ветки: —"
            : $"Пример ветки: {branch}";
        TelegramPreviewBox.Text = TelegramMessage.Build(
            TelegramTemplateBox.Text,
            "Скрипты по заявкам",
            "Скрипты по заявкам",
            TelegramTagsBox.Text,
            _customTemplates.Select(row => new TelegramCustomTemplate { Name = row.Name, Value = row.ValueBox.Text }));
    }

    private void InsertTelegramToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string key } || string.IsNullOrWhiteSpace(key))
            return;
        InsertIntoBox(TelegramTemplateBox, "{" + key + "}");
        _autoTemplateTouched = true;
    }

    private void AddCustomTemplate_Click(object sender, RoutedEventArgs e)
    {
        var name = (CustomTemplateNameBox.Text ?? string.Empty).Trim();
        if (!IsCustomTemplateName(name))
        {
            MessageBox.Show(this, "Имя шаблона: буква, затем буквы, цифры или _. Нельзя использовать text, link и tags.",
                "Свой шаблон", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_customTemplates.Any(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Такой шаблон уже есть.", "Свой шаблон",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddCustomTemplateRow(name, string.Empty);
        InsertIntoBox(TelegramTemplateBox, "{" + name + "}");
        CustomTemplateNameBox.Text = string.Empty;
        _autoTemplateTouched = true;
        UpdateAutoPreview();
    }

    private void AddCustomTemplateRow(string name, string value)
    {
        var valueBox = new System.Windows.Controls.TextBox
        {
            Text = value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0)
        };
        valueBox.TextChanged += (_, _) => UpdateAutoPreview();

        var insertLink = new System.Windows.Controls.Button
        {
            Content = "Вставить {link}",
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        insertLink.Click += (_, _) => InsertIntoBox(valueBox, "{link}");

        var remove = new System.Windows.Controls.Button
        {
            Content = "Удалить",
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        buttons.Children.Add(insertLink);
        buttons.Children.Add(remove);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "{" + name + "}",
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("FgInfoBrush")
        });
        panel.Children.Add(valueBox);
        panel.Children.Add(buttons);
        CustomTemplatesHost.Children.Add(panel);

        var row = new CustomTemplateRow(name, valueBox, panel);
        remove.Click += (_, _) =>
        {
            CustomTemplatesHost.Children.Remove(panel);
            _customTemplates.Remove(row);
            _autoTemplateTouched = true;
            UpdateAutoPreview();
        };
        _customTemplates.Add(row);
    }

    private static bool IsCustomTemplateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("link", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("tags", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && char.IsLetter(name[0]);
    }

    private static void InsertIntoBox(System.Windows.Controls.TextBox box, string token)
    {
        var index = Math.Clamp(box.CaretIndex, 0, box.Text.Length);
        box.Text = box.Text.Insert(index, token);
        box.CaretIndex = index + token.Length;
        box.Focus();
    }

    private sealed class CustomTemplateRow(string name, System.Windows.Controls.TextBox valueBox, FrameworkElement panel)
    {
        public string Name { get; } = name;
        public System.Windows.Controls.TextBox ValueBox { get; } = valueBox;
        public FrameworkElement Panel { get; } = panel;
    }

    private void UpdateAutoReviewersLabel()
    {
        if (_autoReviewers.Count == 0)
        {
            AutoReviewersText.Text = "не выбраны";
            return;
        }

        AutoReviewersText.Text = string.Join(", ", _autoReviewers.Select(r =>
        {
            var name = string.IsNullOrWhiteSpace(r.DisplayName) ? r.UniqueName : r.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
                name = r.Id;
            return r.IsRequired ? $"{name} (обязательный)" : name;
        }));
    }

    private async void PickAutoReviewers_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProjectBox.Text) || string.IsNullOrWhiteSpace(RepoBox.Text))
        {
            MessageBox.Show(this, "Сначала укажите Project и Repo на вкладке «Подключение».", "Ревьюверы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AzureDevOpsAuthorization authorization;
        if (_microsoftAuth)
        {
            var (ok, message, token) = await MicrosoftAccountAuthService.AcquireTokenAsync();
            if (!ok || string.IsNullOrWhiteSpace(token))
            {
                MessageBox.Show(this, message ?? "Не удалось войти.", "Ревьюверы",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            authorization = AzureDevOpsAuthorization.FromAccessToken(token);
        }
        else
        {
            var pat = string.IsNullOrWhiteSpace(PatBox.Password) ? App.Settings.LoadPat() : PatBox.Password;
            if (string.IsNullOrWhiteSpace(pat))
            {
                MessageBox.Show(this, "Укажите PAT на вкладке «Подключение».", "Ревьюверы",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            authorization = AzureDevOpsAuthorization.FromPat(pat);
        }

        var dialog = new PullRequestReviewersDialog(
            OrgUrlBox.Text,
            ProjectBox.Text.Trim(),
            RepoBox.Text.Trim(),
            authorization,
            _autoReviewers)
        { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _autoReviewers = dialog.SelectedReviewers.Select(r => r.Copy()).ToList();
        _autoTemplateTouched = true;
        UpdateAutoReviewersLabel();
    }
}
