using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Sql;
using AutoProdScripts.Themes;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;

namespace AutoProdScripts.Views.Queries;

public partial class QueriesView : UserControl
{
    // Строки параметров переживают временное исчезновение переменной из текста.
    private readonly Dictionary<string, ParameterRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _running;
    private bool _initialized;

    public QueriesView()
    {
        InitializeComponent();
        Library.OpenRequested += OpenItem;
        Library.PropertiesRequested += ShowItemProperties;
        Library.DeleteRequested += DeleteItem;
        Library.NewChainRequested += NewChain;
        Library.ImportRequested += ImportFile;
        Library.BeforeExport = ConfirmDiscard;
        Chain.NewScriptRequested += () => NewScript();
        Chain.BusyChanged += busy => Library.IsEnabled = !busy;
        _checkTimer.Tick += (_, _) => CheckScript();
        Editor.TextChanged += (_, _) =>
        {
            UpdateHeader();
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
                if (InChain)
                    Chain.Reload();
            };
            ReloadConnections();
            Library.Initialize();
            UpdateHeader();
            CheckScript();
            if (App.Connections.LoadError is { } error)
                Results.Summary = error;
        }

        if (!InChain)
            Editor.Focus();
    }

    public void CancelRunning() => _running?.Cancel();

    private void ApplyHighlighting() =>
        Editor.SyntaxHighlighting = SqlHighlightingLoader.GetDefinition()
                                    ?? HighlightingManager.Instance.GetDefinition("TSQL");

    private DbConnectionProfile? SelectedConnection => (ConnectionBox.SelectedItem as ComboBoxItem)?.Tag as DbConnectionProfile;

    private void ReloadConnections()
    {
        var selected = SelectedConnection?.Id;
        ConnectionBox.Items.Clear();
        foreach (var profile in App.Connections.GetAll())
        {
            // Выбранный пункт ComboBox показывает Content как есть, поэтому нужна строка, а не элемент.
            var item = new ComboBoxItem { Content = profile.Name, Tag = profile };
            ConnectionBox.Items.Add(item);
            if (profile.Id == selected)
                ConnectionBox.SelectedItem = item;
        }

        UpdateBadge();
    }

    private void Connection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        UpdateBadge();
    }

    private void UpdateBadge()
    {
        if (SelectedConnection is not { } profile)
        {
            EnvironmentBadge.Visibility = Visibility.Collapsed;
            return;
        }

        EnvironmentBadge.Visibility = Visibility.Visible;
        EnvironmentBadge.SetResourceReference(Border.BackgroundProperty, EnvironmentUi.BrushKey(profile.Environment));
        EnvironmentText.Text = EnvironmentUi.Title(profile.Environment);
        EnvironmentBadge.ToolTip = $"{profile.Server} · {profile.Database}";
    }

    private void CheckScript()
    {
        CheckParameters();
        UpdateHeader();
    }

    private void CheckParameters()
    {
        _checkTimer.Stop();
        if (string.IsNullOrWhiteSpace(Editor.Text))
        {
            SetCheck("Напишите или откройте SQL. Разрешено только чтение и работа с временными таблицами.", "FgMutedBrush");
            ShowParameters([]);
            return;
        }

        var analysis = ReadOnlyValidator.Analyze(Editor.Text);
        if (analysis.IsAllowed)
        {
            SetCheck("✔ Проверка «только чтение» пройдена.", "SuccessBrush");
            ShowParameters(analysis.Parameters);
        }
        else
        {
            SetCheck("✖ " + string.Join(Environment.NewLine, analysis.Violations.Take(3)), "DangerBrush");
            // При ошибке разбора список параметров неизвестен — оставляем прежний.
            if (analysis.Batches.Count > 0)
                ShowParameters(analysis.Parameters);
        }
    }

    private void SetCheck(string text, string brushKey)
    {
        CheckText.Text = text;
        CheckText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private void ShowParameters(IReadOnlyList<string> names)
    {
        var shown = ParametersHost.Children.OfType<FrameworkElement>().Select(c => (string)c.Tag).ToList();
        if (!shown.SequenceEqual(names, StringComparer.OrdinalIgnoreCase))
        {
            ParametersHost.Children.Clear();
            foreach (var name in names)
            {
                if (!_rows.TryGetValue(name, out var row))
                    _rows[name] = row = CreateRow(name);
                ParametersHost.Children.Add(row.Element);
            }
        }

        NoParametersText.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    public async Task RunAsync()
    {
        if (InChain)
        {
            await Chain.RunAsync();
            return;
        }

        if (_running is not null)
            return;

        CheckScript();
        var selection = Editor.SelectionLength > 0;
        var sql = selection ? Editor.SelectedText : Editor.Text;
        if (SelectedConnection is not { } profile)
        {
            Results.ShowFailure(ConnectionBox.Items.Count == 0
                ? "Сначала добавьте подключение (кнопка «Подключения…»)."
                : "Выберите подключение.");
            return;
        }

        var analysis = ReadOnlyValidator.Analyze(sql);
        if (!analysis.IsAllowed)
        {
            Results.ShowFailure("Скрипт не выполнен: он не прошёл проверку «только чтение»."
                        + Environment.NewLine + string.Join(Environment.NewLine, analysis.Violations));
            return;
        }

        if (!int.TryParse(MaxRowsBox.Text.Trim(), out var maxRows) || maxRows <= 0)
        {
            Results.ShowFailure("Лимит строк должен быть положительным числом.");
            return;
        }

        var password = profile.AuthMode == DbAuthMode.SqlLogin ? App.Connections.LoadPassword(profile.Id) : null;
        if (profile.AuthMode == DbAuthMode.SqlLogin && password is null)
        {
            Results.ShowFailure("Для этого подключения не сохранён пароль.");
            return;
        }

        var source = _running = new CancellationTokenSource();
        var scriptId = _current?.Id;
        SetBusy(true);
        Results.Clear();
        Results.Summary = selection ? "Выполняется выделенный фрагмент…" : "Выполняется…";
        try
        {
            var request = new QueryRequest
            {
                ConnectionString = ConnectionStringFactory.Build(profile, password),
                Sql = sql,
                Parameters = analysis.Parameters
                    .Where(_rows.ContainsKey)
                    .Select(name => _rows[name].Value())
                    .ToList(),
                MaxRows = maxRows,
                CommandTimeoutSeconds = profile.CommandTimeoutSeconds
            };

            // Ни текст запроса, ни значения параметров в лог не пишем.
            App.Log.Info($"Запрос: подключение «{profile.Name}».");
            var result = await Task.Run(() => QueryRunner.RunAsync(request, source.Token));
            ShowResult(result, selection);
            if (scriptId is { } id)
                MarkRun(id);
            App.Log.Info(result.Success
                ? $"Запрос выполнен за {ResultsPanel.Seconds(result.Elapsed)}."
                : $"Запрос завершился ошибкой за {ResultsPanel.Seconds(result.Elapsed)}.");
        }
        catch (OperationCanceledException)
        {
            Results.ShowFailure("Выполнение отменено.");
            App.Log.Info("Запрос отменён.");
        }
        catch (Exception ex)
        {
            // Любой сбой запуска показываем в «Сообщениях», а не роняем приложение.
            Results.ShowFailure(ex.Message);
            App.Log.Warn("Запрос не выполнен.");
        }
        finally
        {
            _running = null;
            source.Dispose();
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ConnectionBox.IsEnabled = !busy;
        // Пока идёт запрос, скрипт не меняем: иначе результаты окажутся от другого.
        Library.IsEnabled = !busy;
        ScriptHeader.IsEnabled = !busy;
        OpenButton.IsEnabled = !busy;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _running?.Cancel();

    private void ShowResult(QueryResult result, bool selection)
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
        Results.Show(
            result.ResultSets.Select((set, i) => ($"Результат {i + 1}", set)).ToList(),
            messages, summary, openMessages: !result.Success);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SQL (*.sql)|*.sql|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || !ConfirmDiscard())
            return;

        try
        {
            ShowDraft(File.ReadAllText(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Results.Summary = "Не удалось открыть файл: " + ex.Message;
        }
    }

    private void Connections_Click(object sender, RoutedEventArgs e) =>
        new ConnectionsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
}
