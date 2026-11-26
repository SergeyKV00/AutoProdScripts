using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Connections;

namespace AutoProdScripts.Views.Queries;

public partial class ConnectionsEditor : UserControl
{
    private DbConnectionProfile _current = new();
    private string _baseline = string.Empty;
    private bool _isNew = true;
    private bool _loading;
    private CancellationTokenSource? _test;

    public ConnectionsEditor()
    {
        InitializeComponent();
        ProviderBox.ItemsSource = new[] { "Microsoft SQL Server" };
        ProviderBox.SelectedIndex = 0;
        AuthBox.ItemsSource = new[] { "Windows-аутентификация", "SQL-логин" };
        LevelBox.ItemsSource = new[] { "К конкретной базе", "К кластеру (без базы)" };
        DataObject.AddPastingHandler(PasteBox, OnPaste);
        Unloaded += (_, _) => _test?.Cancel();

        ReloadList(null);
        ShowSelectedOrNew();
        if (App.Connections.LoadError is { } error)
            StatusLine.Text = error;
    }

    private Window? Owner => Window.GetWindow(this);

    private bool _single;

    /// <summary>Для «на основе текущего»: чей пароль взять, если новый не введён.</summary>
    private Guid? _copyPasswordFrom;

    public void CopyPasswordFrom(Guid id) => _copyPasswordFrom = id;

    /// <summary>Подключение сохранено (в режиме одного подключения — можно закрывать окно).</summary>
    public event Action<DbConnectionProfile>? Saved;

    /// <summary>
    /// Режим мастера: без списка слева, редактируется одно подключение.
    /// </summary>
    /// <param name="isNew">true — сохранить как новое (в том числе «на основе текущего»).</param>
    public void ShowSingle(DbConnectionProfile profile, bool isNew, string? password = null)
    {
        _single = true;
        ListPanel.Visibility = Visibility.Collapsed;
        ListColumn.Width = new GridLength(0);
        GapColumn.Width = new GridLength(0);
        Show(profile, isNew);
        if (password is not null)
            PasswordInput.Password = password;
        _baseline = Snapshot();
    }

    private bool HasUnsavedChanges => Snapshot() != _baseline;

    /// <summary>false — пользователь решил остаться и дописать подключение.</summary>
    public bool ConfirmDiscard()
    {
        if (!HasUnsavedChanges)
            return true;

        var answer = MessageBox.Show(Owner, "Изменения подключения не сохранены. Отбросить их?", "Подключения",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return false;

        Show(_current, _isNew);
        return true;
    }

    private void ReloadList(Guid? select)
    {
        _loading = true;
        List.Items.Clear();
        foreach (var profile in App.Connections.GetAll())
        {
            var item = new ListBoxItem { Content = ConnectionUi.Label(profile), Tag = profile };
            List.Items.Add(item);
            if (profile.Id == select)
                List.SelectedItem = item;
        }

        if (List.SelectedItem is null && select is null && List.Items.Count > 0)
            List.SelectedIndex = 0;
        _loading = false;
    }

    private void ShowSelectedOrNew()
    {
        if (List.SelectedItem is ListBoxItem { Tag: DbConnectionProfile profile })
            Show(profile, isNew: false);
        else
            Show(new DbConnectionProfile(), isNew: true);
    }

    private void Show(DbConnectionProfile profile, bool isNew)
    {
        _test?.Cancel();
        _current = profile;
        _isNew = isNew;
        Fill(profile);
        PasswordInput.Clear();
        PasteBox.Clear();
        StatusLine.Text = string.Empty;
        UpdatePasswordHint();
        DuplicateButton.IsEnabled = !isNew;
        DeleteButton.IsEnabled = !isNew;
        _baseline = Snapshot();
    }

    private void Fill(DbConnectionProfile profile)
    {
        NameBox.Text = profile.Name;
        GroupBox.ItemsSource = App.Connections.Groups();
        GroupBox.Text = ConnectionStore.NormalizeGroup(profile.Group);
        ServerBox.Text = profile.Server;
        PortBox.Text = profile.Port?.ToString() ?? string.Empty;
        DatabaseBox.Text = profile.Database;
        // У нового пустого подключения предлагаем базу; «кластер» — только когда сервер уже есть, а базы нет.
        LevelBox.SelectedIndex = profile.IsClusterLevel && profile.Server.Length > 0 ? ClusterLevel : DatabaseLevel;
        UpdateLevel();
        AuthBox.SelectedIndex = (int)profile.AuthMode;
        UserBox.Text = profile.UserName;
        EncryptBox.IsChecked = profile.Encrypt;
        TrustBox.IsChecked = profile.TrustServerCertificate;
        ReadOnlyIntentBox.IsChecked = profile.ReadOnlyIntent;
        ConnectTimeoutBox.Text = profile.ConnectTimeoutSeconds.ToString();
        CommandTimeoutBox.Text = profile.CommandTimeoutSeconds.ToString();
        ExtraBox.Text = profile.ExtraParameters;
    }

    private const int DatabaseLevel = 0;
    private const int ClusterLevel = 1;

    private void Level_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        UpdateLevel();
    }

    private void UpdateLevel()
    {
        var cluster = LevelBox.SelectedIndex == ClusterLevel;
        DatabasePanel.Visibility = cluster ? Visibility.Collapsed : Visibility.Visible;
        LevelHint.Text = "Базу задаёт скрипт: USE или База.dbo.Таблица.";
        LevelHint.Visibility = cluster ? Visibility.Visible : Visibility.Collapsed;
    }

    private DbConnectionProfile Collect()
    {
        var profile = _current.Clone();
        profile.Name = NameBox.Text.Trim();
        profile.Provider = DbProvider.SqlServer;
        profile.Group = ConnectionStore.NormalizeGroup(GroupBox.Text);
        profile.Server = ServerBox.Text.Trim();
        profile.Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : null;
        profile.Database = LevelBox.SelectedIndex == ClusterLevel ? string.Empty : DatabaseBox.Text.Trim();
        profile.AuthMode = (DbAuthMode)Math.Max(0, AuthBox.SelectedIndex);
        profile.UserName = UserBox.Text.Trim();
        profile.Encrypt = EncryptBox.IsChecked == true;
        profile.TrustServerCertificate = TrustBox.IsChecked == true;
        profile.ReadOnlyIntent = ReadOnlyIntentBox.IsChecked == true;
        profile.ConnectTimeoutSeconds = ParseSeconds(ConnectTimeoutBox.Text, 15);
        profile.CommandTimeoutSeconds = ParseSeconds(CommandTimeoutBox.Text, 60);
        profile.ExtraParameters = ExtraBox.Text.Trim();
        return profile;
    }

    private static int ParseSeconds(string text, int fallback) =>
        int.TryParse(text.Trim(), out var value) && value > 0 ? value : fallback;

    private string Snapshot() => JsonSerializer.Serialize(Collect()) + (PasswordInput.Password.Length > 0 ? "+pwd" : "");

    private void UpdatePasswordHint()
    {
        PasswordHint.Text = !_isNew && App.Connections.LoadPassword(_current.Id) is not null
            ? "Сохранён. Пусто — не менять."
            : "Хранится в Credential Manager.";
    }

    private void Auth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        SqlLoginPanel.Visibility = AuthBox.SelectedIndex == (int)DbAuthMode.SqlLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_loading || List.SelectedItem is not ListBoxItem { Tag: DbConnectionProfile profile } item)
            return;
        if (!_isNew && profile.Id == _current.Id)
            return;

        if (!ConfirmDiscard())
        {
            _loading = true;
            List.SelectedItem = List.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => !_isNew && ((DbConnectionProfile)i.Tag).Id == _current.Id);
            _loading = false;
            return;
        }

        Show((DbConnectionProfile)item.Tag, isNew: false);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;

        _loading = true;
        List.SelectedItem = null;
        _loading = false;
        Show(new DbConnectionProfile(), isNew: true);
        PasteBox.Focus();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;

        Try(() =>
        {
            var copy = App.Connections.Duplicate(_current.Id);
            ReloadList(copy.Id);
            ShowSelectedOrNew();
            StatusLine.Text = "Создана копия.";
        });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Owner, $"Удалить подключение «{_current.Name}» и его пароль?", "Подключения",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        Try(() =>
        {
            App.Connections.Delete(_current.Id);
            ReloadList(null);
            ShowSelectedOrNew();
        });
    }

    private void SaveConnection_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        var profile = Collect();
        if (LevelBox.SelectedIndex == DatabaseLevel && profile.Database.Length == 0)
            throw new InvalidOperationException("Укажите базу или выберите «К кластеру (без базы)».");
        var password = PasswordInput.Password.Length > 0 ? PasswordInput.Password : null;
        if (PortBox.Text.Trim().Length > 0 && profile.Port is null)
            throw new InvalidOperationException("Порт — число от 1 до 65535.");
        // Копия «на основе текущего» без нового пароля берёт пароль исходного.
        if (password is null && _isNew && _single && _copyPasswordFrom is { } source)
            password = App.Connections.LoadPassword(source);
        App.Connections.Save(profile, password);
        if (_single)
        {
            _current = profile;
            _isNew = false;
            _baseline = Snapshot();
            Saved?.Invoke(profile);
            return;
        }

        ReloadList(profile.Id);
        ShowSelectedOrNew();
        StatusLine.Text = "Подключение сохранено.";
        Saved?.Invoke(profile);
    });

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusLine.Text = ex.Message;
        }
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text)
            return;

        // Обычная вставка кусочка текста при ручном наборе проходит как есть.
        if (!text.Contains('=') && !text.Contains(':'))
            return;

        e.CancelCommand();
        Dispatcher.BeginInvoke(() => Import(text));
    }

    private void PasteFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
                Import(Clipboard.GetText());
            else
                StatusLine.Text = "В буфере обмена нет текста.";
        }
        catch (ExternalException)
        {
            StatusLine.Text = "Буфер обмена занят другой программой, попробуйте ещё раз.";
        }
    }

    private void ParseTyped_Click(object sender, RoutedEventArgs e) => Import(PasteBox.Text);

    private void Import(string text)
    {
        var result = ConnectionStringParser.Parse(text);
        if (result.Connections.Count == 0)
        {
            StatusLine.Text = string.Join(Environment.NewLine, result.Errors);
            return;
        }

        PasteBox.Clear();
        if (result.Connections.Count == 1 && result.Errors.Count == 0)
        {
            FillFromParsed(result.Connections[0]);
            return;
        }

        if (!ConfirmDiscard())
            return;

        var dialog = new ImportConnectionsDialog(result) { Owner = Owner };
        if (dialog.ShowDialog() != true)
            return;

        Try(() =>
        {
            Guid? last = null;
            foreach (var parsed in dialog.Selected)
            {
                parsed.Profile.Name = App.Connections.MakeUniqueName(parsed.Profile.Name);
                App.Connections.Save(parsed.Profile, parsed.Password);
                last = parsed.Profile.Id;
            }

            ReloadList(last);
            ShowSelectedOrNew();
            StatusLine.Text = $"Добавлено подключений: {dialog.Selected.Count}. Разложите их по группам.";
        });
    }

    private void FillFromParsed(ParsedConnection parsed)
    {
        var profile = Collect();
        var source = parsed.Profile;
        profile.Server = source.Server;
        profile.Database = source.Database;
        profile.AuthMode = source.AuthMode;
        profile.UserName = source.UserName;
        profile.Encrypt = source.Encrypt;
        profile.TrustServerCertificate = source.TrustServerCertificate;
        profile.ReadOnlyIntent = source.ReadOnlyIntent;
        profile.ConnectTimeoutSeconds = source.ConnectTimeoutSeconds;
        profile.ExtraParameters = source.ExtraParameters;
        if (profile.Name.Length == 0)
            profile.Name = App.Connections.MakeUniqueName(source.Name, profile.Id);

        Fill(profile);
        PasswordInput.Password = parsed.Password ?? string.Empty;
        StatusLine.Text = (profile.IsClusterLevel
                              ? "В строке нет базы — это подключение к КЛАСТЕРУ."
                              : $"Это подключение к базе {profile.Database}.")
                          + " Проверьте поля и группу, затем сохраните.";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        _test?.Cancel();
        var source = _test = new CancellationTokenSource();
        TestButton.IsEnabled = false;
        StatusLine.Text = "Подключаюсь…";
        try
        {
            var profile = Collect();
            var password = PasswordInput.Password.Length > 0
                ? PasswordInput.Password
                : !_isNew ? App.Connections.LoadPassword(profile.Id)
                : _copyPasswordFrom is { } original ? App.Connections.LoadPassword(original) : null;
            var connectionString = ConnectionStringFactory.Build(profile, password);
            var result = await Task.Run(() => ConnectionTester.TestAsync(connectionString, source.Token), source.Token);
            if (source.IsCancellationRequested)
                return;

            StatusLine.Text = (profile.IsClusterLevel
                                  ? $"Подключение к кластеру работает: сервер {result.Server}, база по умолчанию {result.Database}."
                                  : $"Подключение работает: сервер {result.Server}, база {result.Database}.")
                              + (result.CanWrite
                                  ? " Внимание: у логина есть права на запись."
                                  : string.Empty);
        }
        catch (Exception ex)
        {
            if (!source.IsCancellationRequested)
                StatusLine.Text = "Не удалось подключиться: " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
