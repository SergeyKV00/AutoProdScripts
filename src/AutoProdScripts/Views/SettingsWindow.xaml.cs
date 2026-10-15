using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
    private readonly List<string> _recipients = new();
    private readonly List<TimeTextRow> _timeTextRows = new();

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
        TelegramLinkCaptionBox.Text = AutoPrTemplate.LinkCaptionOrDefault(s.TelegramLinkCaption);
        LoadRecipients(s.TelegramTags);
        LoadTimeZones(s.TelegramTimeZoneId);
        LoadTimeTexts(s.TelegramTimeTexts);
        foreach (var part in s.TelegramCustomTemplates ?? new List<TelegramCustomTemplate>())
        {
            if (!string.IsNullOrWhiteSpace(part.Name))
                AddCustomTemplateRow(part.Name.Trim(), part.Value);
        }
        UpdateAutoReviewersLabel();
        RefreshTokenMenu();
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

        CommitRecipientInput();
        if (string.IsNullOrWhiteSpace(TelegramTemplateBox.Text) || HasBrokenToken(TelegramTemplateBox.Text))
        {
            MessageBox.Show(this, "Шаблон сообщения пуст или в нём не закрыта фигурная скобка.", "Настройка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SettingsTabs.SelectedItem = AutoPrTab;
            return;
        }

        if (string.IsNullOrWhiteSpace(TelegramLinkCaptionBox.Text))
        {
            MessageBox.Show(this, "Укажите текст ссылки на PR.", "Настройка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SettingsTabs.SelectedItem = AutoPrTab;
            TelegramLinkCaptionBox.Focus();
            return;
        }

        if (!TryCollectTimeTexts(out var timeTexts))
        {
            SettingsTabs.SelectedItem = AutoPrTab;
            return;
        }

        var previewLength = TelegramMessage.Build(
            TelegramTemplateBox.Text,
            TelegramPreviewRenderer.DemoText,
            TelegramLinkCaptionBox.Text,
            string.Join(' ', _recipients),
            CurrentCustomTemplates(),
            timeTexts,
            SelectedTimeZoneId()).Length;
        if (previewLength > AutoPrTemplate.TelegramMaxLength)
        {
            MessageBox.Show(this, $"Сообщение длиннее {AutoPrTemplate.TelegramMaxLength} символов — Telegram его не примет.",
                "Настройка", MessageBoxButton.OK, MessageBoxImage.Warning);
            SettingsTabs.SelectedItem = AutoPrTab;
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
            TelegramLinkCaption = TelegramLinkCaptionBox.Text.Trim(),
            TelegramTags = string.Join(' ', _recipients),
            TelegramTimeZoneId = SelectedTimeZoneId(),
            TelegramTimeTexts = timeTexts,
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
        if (BranchPreviewText is null || TelegramPreviewBox is null || TelegramLinkCaptionBox is null || TelegramCountText is null)
            return;

        var baseBranch = string.IsNullOrWhiteSpace(AutoBaseBranchBox.Text) ? "master" : AutoBaseBranchBox.Text.Trim();
        var branch = AutoPrTemplate.ExpandBranchName(AutoBranchTemplateBox.Text, baseBranch, DateTime.Now);
        BranchPreviewText.Text = string.IsNullOrWhiteSpace(branch)
            ? "Пример ветки: —"
            : $"Пример ветки: {branch}";

        var custom = CurrentCustomTemplates();
        var timeTexts = CurrentTimeTexts();
        var timeZoneId = SelectedTimeZoneId();
        var message = TelegramMessage.Build(
            TelegramTemplateBox.Text,
            TelegramPreviewRenderer.DemoText,
            TelegramLinkCaptionBox.Text,
            string.Join(' ', _recipients),
            custom,
            timeTexts,
            timeZoneId);
        var count = message.Length;
        TelegramCountText.Text = $"{count} / {AutoPrTemplate.TelegramMaxLength}";
        TelegramCountText.Foreground = (Brush)FindResource(count > AutoPrTemplate.TelegramMaxLength ? "FgBrush" : "FgMutedBrush");
        if (count > AutoPrTemplate.TelegramMaxLength)
            TelegramCountText.Foreground = Brushes.IndianRed;

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        TelegramPreviewRenderer.Render(
            paragraph,
            TelegramTemplateBox.Text,
            TelegramPreviewRenderer.DemoText,
            TelegramLinkCaptionBox.Text,
            TelegramPreviewRenderer.DemoUrl,
            string.Join(' ', _recipients),
            custom,
            (Brush)FindResource("FgInfoBrush"),
            openLink: null,
            timeTexts,
            timeZoneId);
        if (TelegramTimeTextNow is not null)
        {
            var zone = TelegramMessage.ResolveTimeZone(timeZoneId);
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, zone);
            var sample = TelegramMessage.ResolveTimeText(timeTexts, zone.Id);
            TelegramTimeTextNow.Text = string.IsNullOrEmpty(sample)
                ? $"Сейчас {local:HH:mm}: текст по времени не подставится."
                : $"Сейчас {local:HH:mm}: подставится «{sample}».";
        }
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("FgBrush")
        };
        document.Blocks.Add(paragraph);
        TelegramPreviewBox.Document = document;
    }

    private void OpenTokenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu })
            return;
        RefreshTokenMenu();
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void RefreshTokenMenu()
    {
        if (InsertTokenButton is null)
            return;
        InsertTokenButton.ContextMenu ??= new ContextMenu();
        var menu = InsertTokenButton.ContextMenu;
        menu.Items.Clear();
        AddTokenItem(menu, "link", "{link} — ссылка на PR");
        AddTokenItem(menu, "tags", "{tags} — получатели");
        AddTokenItem(menu, "timeText", "{timeText} — текст по времени суток");
        AddTokenItem(menu, "text", "{text} — описание скриптов, необязательно");
    }

    private void AddTokenItem(ContextMenu menu, string key, string header)
    {
        var item = new MenuItem { Header = header, Tag = key };
        item.Click += InsertTelegramToken_Click;
        menu.Items.Add(item);
    }

    private void InsertTelegramToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || string.IsNullOrWhiteSpace(key))
            return;
        InsertIntoBox(TelegramTemplateBox, "{" + key + "}");
        _autoTemplateTouched = true;
    }

    private void LoadRecipients(string? raw)
    {
        _recipients.Clear();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var part in raw.Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                RememberRecipient(part);
        }

        RenderRecipients();
    }

    private void AddRecipient_Click(object sender, RoutedEventArgs e) => CommitRecipientInput();

    private void RecipientInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        CommitRecipientInput();
    }

    private void CommitRecipientInput()
    {
        if (RecipientInputBox is null || string.IsNullOrWhiteSpace(RecipientInputBox.Text))
            return;
        foreach (var part in RecipientInputBox.Text.Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            RememberRecipient(part);
        RecipientInputBox.Text = string.Empty;
        RenderRecipients();
        _autoTemplateTouched = true;
        UpdateAutoPreview();
    }

    private void RememberRecipient(string raw)
    {
        var name = raw.Trim();
        if (name.Length == 0)
            return;
        if (!name.StartsWith('@'))
            name = "@" + name;
        if (_recipients.Any(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;
        _recipients.Add(name);
    }

    private void RenderRecipients()
    {
        if (RecipientHost is null)
            return;
        RecipientHost.Children.Clear();
        foreach (var name in _recipients.ToList())
        {
            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("FgBrush")
            };
            var remove = new Button
            {
                Content = "×",
                Style = (Style)FindResource("IconButton"),
                Width = 22,
                Height = 22,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Удалить"
            };
            var edit = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 22,
                Height = 22,
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "Изменить",
                Content = new System.Windows.Shapes.Path
                {
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform,
                    Fill = (Brush)FindResource("FgBrush"),
                    Data = Geometry.Parse("M3,17.25 L3,21 L6.75,21 L17.81,9.94 L14.06,6.19 Z M20.71,7.04 C21.1,6.65 21.1,6.02 20.71,5.63 L18.37,3.29 C17.98,2.9 17.35,2.9 16.96,3.29 L15.13,5.12 L18.88,8.87 Z")
                }
            };
            var chip = new Border
            {
                Background = (Brush)FindResource("InputBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(label);
            row.Children.Add(edit);
            row.Children.Add(remove);
            chip.Child = row;
            var captured = name;
            remove.Click += (_, _) =>
            {
                _recipients.Remove(captured);
                RenderRecipients();
                _autoTemplateTouched = true;
                UpdateAutoPreview();
            };
            edit.Click += (_, _) =>
            {
                _recipients.Remove(captured);
                RecipientInputBox.Text = captured;
                RecipientInputBox.Focus();
                RecipientInputBox.CaretIndex = RecipientInputBox.Text.Length;
                RenderRecipients();
                UpdateAutoPreview();
            };
            RecipientHost.Children.Add(chip);
        }
    }

    private static bool HasBrokenToken(string template)
    {
        var depth = 0;
        foreach (var ch in template)
        {
            if (ch == '{')
                depth++;
            else if (ch == '}')
                depth--;
            if (depth < 0)
                return true;
        }

        return depth != 0;
    }

    private IEnumerable<TelegramCustomTemplate> CurrentCustomTemplates() =>
        _customTemplates.Select(row => new TelegramCustomTemplate { Name = row.Name, Value = row.ValueBox.Text });

    private void LoadTimeZones(string? selectedId)
    {
        if (TelegramTimeZoneBox is null)
            return;
        var zones = TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(zone => zone.BaseUtcOffset)
            .ThenBy(zone => zone.DisplayName, StringComparer.CurrentCulture)
            .ToList();
        TelegramTimeZoneBox.ItemsSource = zones;
        TelegramTimeZoneBox.DisplayMemberPath = nameof(TimeZoneInfo.DisplayName);
        var id = string.IsNullOrWhiteSpace(selectedId) ? TimeZoneInfo.Local.Id : selectedId;
        TelegramTimeZoneBox.SelectedItem = zones.FirstOrDefault(zone => zone.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? zones.FirstOrDefault(zone => zone.Id == TimeZoneInfo.Local.Id);
    }

    private void LoadTimeTexts(IEnumerable<TelegramTimeTextRule>? rules)
    {
        _timeTextRows.Clear();
        TelegramTimeTextsHost?.Children.Clear();
        foreach (var rule in rules ?? Array.Empty<TelegramTimeTextRule>())
            AddTimeTextRow(rule.From, rule.To, rule.Text);
    }

    private void AddTimeText_Click(object sender, RoutedEventArgs e)
    {
        AddTimeTextRow(string.Empty, string.Empty, string.Empty);
        _autoTemplateTouched = true;
        _timeTextRows.LastOrDefault()?.From.Focus();
    }

    private void AddTimeTextRow(string from, string to, string text)
    {
        var fromBox = new TextBox { Text = from, Width = 72, Margin = new Thickness(0, 0, 6, 0), ToolTip = "09:00" };
        var toBox = new TextBox { Text = to, Width = 72, Margin = new Thickness(0, 0, 6, 0), ToolTip = "12:00" };
        var textBox = new TextBox { Text = text, MinWidth = 140, ToolTip = "Доброе утро" };
        fromBox.TextChanged += (_, _) => UpdateAutoPreview();
        toBox.TextChanged += (_, _) => UpdateAutoPreview();
        textBox.TextChanged += (_, _) => UpdateAutoPreview();

        var remove = new Button
        {
            Content = "Удалить",
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(fromBox, 0);
        Grid.SetColumn(toBox, 1);
        Grid.SetColumn(textBox, 2);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(fromBox);
        grid.Children.Add(toBox);
        grid.Children.Add(textBox);
        grid.Children.Add(remove);
        TelegramTimeTextsHost.Children.Add(grid);

        var row = new TimeTextRow(fromBox, toBox, textBox, grid);
        remove.Click += (_, _) =>
        {
            TelegramTimeTextsHost.Children.Remove(grid);
            _timeTextRows.Remove(row);
            _autoTemplateTouched = true;
            UpdateAutoPreview();
        };
        _timeTextRows.Add(row);
    }

    private string SelectedTimeZoneId() =>
        TelegramTimeZoneBox?.SelectedItem is TimeZoneInfo zone ? zone.Id : TimeZoneInfo.Local.Id;

    private List<TelegramTimeTextRule> CurrentTimeTexts() =>
        _timeTextRows
            .Select(row => new TelegramTimeTextRule
            {
                From = row.From.Text.Trim(),
                To = row.To.Text.Trim(),
                Text = row.Text.Text ?? string.Empty
            })
            .Where(rule => rule.From.Length > 0 || rule.To.Length > 0 || rule.Text.Length > 0)
            .ToList();

    private bool TryCollectTimeTexts(out List<TelegramTimeTextRule> rules)
    {
        rules = new List<TelegramTimeTextRule>();
        foreach (var rule in CurrentTimeTexts())
        {
            if (!TelegramMessage.TryParseClock(rule.From, out var from) || !TelegramMessage.TryParseClock(rule.To, out var to))
            {
                MessageBox.Show(this, "Укажите начало и конец периода в формате ЧЧ:мм.", "Настройка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            rules.Add(new TelegramTimeTextRule
            {
                From = from.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                To = to.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                Text = rule.Text
            });
        }

        return true;
    }

    private sealed class TimeTextRow(TextBox from, TextBox to, TextBox text, FrameworkElement panel)
    {
        public TextBox From { get; } = from;
        public TextBox To { get; } = to;
        public TextBox Text { get; } = text;
        public FrameworkElement Panel { get; } = panel;
    }

    private void AddCustomTemplate_Click(object sender, RoutedEventArgs e)
    {
        var name = (CustomTemplateNameBox.Text ?? string.Empty).Trim();
        if (!IsCustomTemplateName(name))
        {
            MessageBox.Show(this, "Имя шаблона: буква, затем буквы, цифры или _. Нельзя использовать text, link, tags и timeText.",
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
        RefreshTokenMenu();
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
            RefreshTokenMenu();
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
            name.Equals("tags", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("timeText", StringComparison.OrdinalIgnoreCase))
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
        if (AutoReviewersHost is not null)
            AutoReviewersHost.Children.Clear();

        if (_autoReviewers.Count == 0)
        {
            AutoReviewersText.Text = "Ревьюверы не выбраны";
            AutoReviewersText.Visibility = Visibility.Visible;
            return;
        }

        AutoReviewersText.Visibility = Visibility.Collapsed;
        if (AutoReviewersHost is null)
            return;

        foreach (var reviewer in _autoReviewers)
        {
            var name = string.IsNullOrWhiteSpace(reviewer.DisplayName) ? reviewer.UniqueName : reviewer.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
                name = reviewer.Id;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("FgBrush")
            });
            if (reviewer.IsRequired)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "обязательный",
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("FgInfoBrush")
                });
            }

            AutoReviewersHost.Children.Add(new Border
            {
                Background = (Brush)FindResource(reviewer.IsRequired ? "SelectionBrush" : "InputBrush"),
                BorderBrush = (Brush)FindResource(reviewer.IsRequired ? "AccentBrush" : "BorderBrush"),
                BorderThickness = new Thickness(reviewer.IsRequired ? 2 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 8),
                Child = row
            });
        }
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
