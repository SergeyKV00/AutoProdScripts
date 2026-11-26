using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Sql;
using AutoProdScripts.Themes;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;

namespace AutoProdScripts.Views.Queries;

public partial class QueriesView : UserControl
{
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _initialized;

    public QueriesView()
    {
        InitializeComponent();
        Library.OpenRequested += OpenItem;
        Library.PropertiesRequested += ShowItemProperties;
        Library.DeleteRequested += DeleteItem;
        Library.NewChainRequested += NewChain;
        Library.ImportRequested += ImportFile;
        Library.BeforeExport = ConfirmSaveActive;
        Chain.NewScriptRequested += () => NewScript();
        Chain.BusyChanged += _ => RefreshChrome();
        Chain.TitleChanged += RefreshChrome;
        _checkTimer.Tick += (_, _) => CheckScript();
        InitializeSession();
        Editor.TextChanged += (_, _) =>
        {
            if (_switching || _active is null)
                return;
            UpdateHeader();
            RefreshChrome(_active);
            ScheduleSessionSave();
            _checkTimer.Stop();
            _checkTimer.Start();
        };
    }

    /// <summary>Вызывается при первом показе раздела, когда ресурсы приложения уже готовы.</summary>
    public void Activate()
    {
        if (!_initialized)
        {
            _initialized = true;
            ApplyHighlighting();
            ThemeService.ThemeChanged += ApplyHighlighting;
            App.Connections.Changed += ReloadConnections;
            App.Connections.Changed += () =>
            {
                if (_chainDocument is not null)
                    Chain.Reload();
                RefreshChrome();
            };
            App.Library.Changed += SyncWithLibrary;
            InitializeRegions();
            ReloadConnections();
            Library.Initialize();
            InitializeExplorer();
            RestoreSession();
            if (App.Connections.LoadError is { } error && _active is { IsChain: false } doc)
                doc.Results.Summary = error;
        }

        if (_active is { IsChain: false })
            Editor.Focus();
    }

    /// <summary>Отменяет все выполняющиеся запросы.</summary>
    public void CancelRunning()
    {
        foreach (var doc in _documents)
            doc.Running?.Cancel();
    }

    private void ApplyHighlighting() =>
        Editor.SyntaxHighlighting = SqlHighlightingLoader.GetDefinition()
                                    ?? HighlightingManager.Instance.GetDefinition("TSQL");

    /// <summary>Результаты активной вкладки скрипта.</summary>
    private ResultsPanel Results => _active!.Results;

    private DbConnectionProfile? SelectedConnection => (ConnectionBox.SelectedItem as ComboBoxItem)?.Tag as DbConnectionProfile;

    private void ReloadConnections()
    {
        _switching = true;
        try
        {
            ConnectionBox.Items.Clear();
            ConnectionBox.Items.Add(new ComboBoxItem { Content = "🌍 " + RegionTargets.Title, Tag = RegionalTag });
            foreach (var profile in App.Connections.GetAll())
                // Выбранный пункт ComboBox показывает Content как есть, поэтому нужна строка, а не элемент.
                ConnectionBox.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile });
        }
        finally
        {
            _switching = false;
        }

        if (_active is { IsChain: false } doc)
            ShowConnection(doc);
    }

    /// <summary>Показывает в панели подключение вкладки, не меняя его.</summary>
    private void ShowConnection(QueryDocument doc)
    {
        _switching = true;
        try
        {
            ConnectionBox.SelectedItem = doc.Regional
                ? ConnectionBox.Items.OfType<ComboBoxItem>().First(i => i.Tag is RegionalTag)
                : ConnectionBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is DbConnectionProfile p && p.Id == doc.ConnectionId);
            RegionTemplateBox.Text = doc.RegionDatabase;
            Regions.SetSelected(doc.RegionCodes);
        }
        finally
        {
            _switching = false;
        }

        UpdateDatabaseControls(doc);
        UpdateBadge();
    }

    private void Connection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_switching && _active is { IsChain: false } doc)
        {
            // Меняется подключение только этой вкладки.
            doc.Regional = IsRegional;
            doc.ConnectionId = SelectedConnection?.Id;
            // База другого сервера здесь не имеет смысла.
            doc.Database = string.Empty;
            UpdateDatabaseControls(doc);
            Explorer.SetActiveConnection(doc.Regional ? null : doc.ConnectionId, EffectiveDatabase(doc));
            RefreshChrome(doc);
            ScheduleSessionSave();
        }

        UpdateBadge();
        // Предупреждение о базе зависит от выбранного подключения.
        if (_initialized && !_switching)
            CheckScript();
    }

    private void UpdateBadge()
    {
        RegionControls.Visibility = IsRegional ? Visibility.Visible : Visibility.Collapsed;
        if (IsRegional)
        {
            UpdateRegionBadge();
            return;
        }

        if (SelectedConnection is not { } profile)
        {
            GroupBadge.Visibility = Visibility.Collapsed;
            DatabaseControls.Visibility = Visibility.Collapsed;
            TargetText.Text = string.Empty;
            return;
        }

        ShowGroups([ConnectionUi.GroupOf(profile)]);
        var database = _active is { IsChain: false } doc && doc.Database.Length > 0 ? doc.Database : null;
        var cluster = profile.IsClusterLevel && database is null;
        TargetText.Text = cluster ? $"КЛАСТЕР {profile.Server}" : $"БАЗА {database ?? profile.Database}";
        TargetText.ToolTip = $"{ConnectionStringFactory.DataSource(profile)} · {ConnectionUi.Target(profile)}";
        TargetText.SetResourceReference(TextBlock.ForegroundProperty, cluster ? "WarnBrush" : "FgInfoBrush");
    }

    /// <summary>Группа подключения рядом с ним — вместо прежней плашки среды.</summary>
    private void ShowGroups(IEnumerable<string> groups)
    {
        var list = groups.Where(g => g.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        GroupBadge.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupText.Text = string.Join(", ", list);
        GroupBadge.ToolTip = "Группа подключения";
    }

    private void CheckScript()
    {
        CheckParameters();
        UpdateHeader();
    }

    private void CheckParameters()
    {
        _checkTimer.Stop();
        if (_active is not { IsChain: false } doc)
            return;

        if (string.IsNullOrWhiteSpace(Editor.Text))
        {
            SetCheck("Напишите или откройте SQL.", "FgMutedBrush");
            ShowParameters(doc, []);
            return;
        }

        var analysis = ReadOnlyValidator.Analyze(Editor.Text);
        if (analysis.IsAllowed)
        {
            if (SelectedConnection is { IsClusterLevel: true } && doc.Database.Length == 0 && DatabaseChoice.IsMissing(Editor.Text))
                SetCheck("⚠ " + ConnectionUi.ClusterWarning, "WarnBrush");
            else
                SetCheck("✔ Проверка «только чтение» пройдена.", "SuccessBrush");
            ShowParameters(doc, analysis.Parameters);
        }
        else
        {
            SetCheck("✖ " + string.Join(Environment.NewLine, analysis.Violations.Take(3)), "DangerBrush");
            // При ошибке разбора список параметров неизвестен — оставляем прежний.
            if (analysis.Batches.Count > 0)
                ShowParameters(doc, analysis.Parameters);
        }
    }

    private void SetCheck(string text, string brushKey)
    {
        CheckText.Text = text;
        CheckText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private void ShowParameters(QueryDocument doc, IReadOnlyList<string> names)
    {
        var shown = ParametersHost.Children.OfType<FrameworkElement>().ToList();
        var same = shown.Count == names.Count
                   && shown.Zip(names).All(x => doc.Rows.TryGetValue(x.Second, out var row) && ReferenceEquals(row.Element, x.First));
        if (!same)
        {
            ParametersHost.Children.Clear();
            foreach (var name in names)
            {
                if (!doc.Rows.TryGetValue(name, out var row))
                    doc.Rows[name] = row = CreateRow(doc, name);
                ParametersHost.Children.Add(row.Element);
            }
        }

        NoParametersText.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    public async Task RunAsync()
    {
        if (_active is not { } doc)
            return;
        if (doc.IsChain)
        {
            await Chain.RunAsync();
            return;
        }

        if (doc.IsRunning)
            return;

        CheckScript();
        var selection = Editor.SelectionLength > 0;
        var sql = selection ? Editor.SelectedText : Editor.Text;
        var results = doc.Results;
        var regional = doc.Regional;
        var profile = regional ? null : App.Connections.GetAll().FirstOrDefault(p => p.Id == doc.ConnectionId);
        // База, выбранная во вкладке, заменяет базу подключения только для этого запуска.
        if (profile is not null && doc.Database.Length > 0)
            profile.Database = doc.Database;
        if (profile is null && !regional)
        {
            results.ShowFailure(App.Connections.GetAll().Count == 0
                ? "Сначала добавьте подключение (кнопка «Подключения…»)."
                : "Выберите подключение для этой вкладки.");
            return;
        }

        var analysis = ReadOnlyValidator.Analyze(sql);
        if (!analysis.IsAllowed)
        {
            results.ShowFailure("Скрипт не выполнен: он не прошёл проверку «только чтение»."
                        + Environment.NewLine + string.Join(Environment.NewLine, analysis.Violations));
            return;
        }

        if (!int.TryParse(MaxRowsBox.Text.Trim(), out var maxRows) || maxRows <= 0)
        {
            results.ShowFailure("Лимит строк должен быть положительным числом.");
            return;
        }

        if (profile is null)
        {
            await RunRegionsAsync(doc, sql, analysis, maxRows, selection);
            return;
        }

        var password = profile.AuthMode == DbAuthMode.SqlLogin ? App.Connections.LoadPassword(profile.Id) : null;
        if (profile.AuthMode == DbAuthMode.SqlLogin && password is null)
        {
            results.ShowFailure("Для этого подключения не сохранён пароль.");
            return;
        }

        var source = new CancellationTokenSource();
        var scriptId = doc.ScriptId;
        SetBusy(doc, source);
        results.Clear();
        results.Summary = selection ? "Выполняется выделенный фрагмент…" : "Выполняется…";
        try
        {
            var request = new QueryRequest
            {
                ConnectionString = ConnectionStringFactory.Build(profile, password),
                Sql = sql,
                Parameters = analysis.Parameters
                    .Where(doc.Rows.ContainsKey)
                    .Select(name => doc.Rows[name].Value())
                    .ToList(),
                MaxRows = maxRows,
                CommandTimeoutSeconds = profile.CommandTimeoutSeconds
            };

            // Ни текст запроса, ни значения параметров в лог не пишем.
            App.Log.Info($"Запрос: подключение «{profile.Name}».");
            var result = await Task.Run(() => QueryRunner.RunAsync(request, source.Token));
            ShowResult(results, result, selection);
            if (scriptId is { } id)
                MarkRun(id);
            App.Log.Info(result.Success
                ? $"Запрос выполнен за {ResultsPanel.Seconds(result.Elapsed)}."
                : $"Запрос завершился ошибкой за {ResultsPanel.Seconds(result.Elapsed)}.");
        }
        catch (OperationCanceledException)
        {
            results.ShowFailure("Выполнение отменено.");
            App.Log.Info("Запрос отменён.");
        }
        catch (Exception ex)
        {
            // Любой сбой запуска показываем в «Сообщениях», а не роняем приложение.
            results.ShowFailure(ex.Message);
            App.Log.Warn("Запрос не выполнен.");
        }
        finally
        {
            SetBusy(doc, null);
            source.Dispose();
        }
    }

    /// <summary>У каждой вкладки свой запуск: другие вкладки в это время можно править и запускать.</summary>
    private void SetBusy(QueryDocument doc, CancellationTokenSource? running)
    {
        doc.Running = running;
        if (doc == _active)
            ShowBusy(doc.IsRunning);
        RefreshChrome(doc);
    }

    private void ShowBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        // Пока идёт запрос, подключение вкладки не меняем: иначе непонятно, откуда результаты.
        ConnectionBox.IsEnabled = !busy;
        RegionControls.IsEnabled = !busy;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _active?.Running?.Cancel();

    private static void ShowResult(ResultsPanel results, QueryResult result, bool selection)
    {
        var messages = new List<string>();
        if (result.Error is not null)
            messages.Add(result.Error);
        messages.AddRange(result.Messages);

        var rows = result.ResultSets.Sum(s => s.Rows.Count);
        var summary = (result.Success ? "Выполнено" : "Ошибка — подробности в «Сообщениях»")
                      + (selection ? " (выделенный фрагмент)" : "")
                      + $" · строк: {rows} · {ResultsPanel.Seconds(result.Elapsed)}"
                      + (result.ResultSets.Any(s => s.Truncated) ? " · показаны не все строки" : "");
        results.Show(
            result.ResultSets.Select((set, i) => ($"Результат {i + 1}", set)).ToList(),
            messages, summary, openMessages: !result.Success);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenFile();

    /// <summary>Файл .sql открывается в новой вкладке как черновик.</summary>
    public void OpenFile()
    {
        var dialog = new OpenFileDialog { Filter = "SQL (*.sql)|*.sql|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var doc = CreateDraft(File.ReadAllText(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
            ActivateDocument(doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось открыть файл: " + ex.Message, "Запросы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Connections_Click(object sender, RoutedEventArgs e) => ShowConnections();

    public void ShowConnections() =>
        new ConnectionsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
}
