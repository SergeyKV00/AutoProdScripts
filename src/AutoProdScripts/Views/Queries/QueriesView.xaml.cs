using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
        Library.OpenRequested += OpenScript;
        Library.PropertiesRequested += ShowProperties;
        Library.DeleteRequested += DeleteScript;
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
            ReloadConnections();
            Library.Initialize();
            UpdateHeader();
            CheckScript();
            if (App.Connections.LoadError is { } error)
                SummaryText.Text = error;
        }

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
        if (_running is not null)
            return;

        CheckScript();
        var selection = Editor.SelectionLength > 0;
        var sql = selection ? Editor.SelectedText : Editor.Text;
        if (SelectedConnection is not { } profile)
        {
            ShowFailure(ConnectionBox.Items.Count == 0
                ? "Сначала добавьте подключение (кнопка «Подключения…»)."
                : "Выберите подключение.");
            return;
        }

        var analysis = ReadOnlyValidator.Analyze(sql);
        if (!analysis.IsAllowed)
        {
            ShowFailure("Скрипт не выполнен: он не прошёл проверку «только чтение»."
                        + Environment.NewLine + string.Join(Environment.NewLine, analysis.Violations));
            return;
        }

        if (!int.TryParse(MaxRowsBox.Text.Trim(), out var maxRows) || maxRows <= 0)
        {
            ShowFailure("Лимит строк должен быть положительным числом.");
            return;
        }

        var password = profile.AuthMode == DbAuthMode.SqlLogin ? App.Connections.LoadPassword(profile.Id) : null;
        if (profile.AuthMode == DbAuthMode.SqlLogin && password is null)
        {
            ShowFailure("Для этого подключения не сохранён пароль.");
            return;
        }

        var source = _running = new CancellationTokenSource();
        var scriptId = _current?.Id;
        SetBusy(true);
        ClearResults();
        SummaryText.Text = selection ? "Выполняется выделенный фрагмент…" : "Выполняется…";
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
                ? $"Запрос выполнен за {Seconds(result.Elapsed)}."
                : $"Запрос завершился ошибкой за {Seconds(result.Elapsed)}.");
        }
        catch (OperationCanceledException)
        {
            ShowFailure("Выполнение отменено.");
            App.Log.Info("Запрос отменён.");
        }
        catch (Exception ex)
        {
            // Любой сбой запуска показываем в «Сообщениях», а не роняем приложение.
            ShowFailure(ex.Message);
            App.Log.Warn("Запрос не выполнен.");
        }
        finally
        {
            _running = null;
            source.Dispose();
            SetBusy(false);
        }
    }

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) + " с";

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

    private void ClearResults()
    {
        while (ResultTabs.Items.Count > 1)
            ResultTabs.Items.RemoveAt(0);
        MessagesBox.Clear();
        ResultTabs.SelectedItem = MessagesTab;
        UpdateResultButtons();
    }

    private void ShowFailure(string message)
    {
        ClearResults();
        SummaryText.Text = "Не выполнено — подробности в «Сообщениях».";
        MessagesBox.Text = message;
    }

    private void ShowResult(QueryResult result, bool selection)
    {
        for (var i = 0; i < result.ResultSets.Count; i++)
        {
            var set = result.ResultSets[i];
            ResultTabs.Items.Insert(i, new TabItem
            {
                Header = $"Результат {i + 1} ({set.Rows.Count}{(set.Truncated ? "+" : "")})",
                Content = CreateGrid(set),
                Tag = set
            });
        }

        var messages = new List<string>();
        if (result.Error is not null)
            messages.Add(result.Error);
        messages.AddRange(result.Messages);
        if (messages.Count == 0)
            messages.Add("Выполнено без сообщений.");
        MessagesBox.Text = string.Join(Environment.NewLine, messages);

        var rows = result.ResultSets.Sum(s => s.Rows.Count);
        SummaryText.Text = (result.Success ? "Выполнено" : "Ошибка — подробности в «Сообщениях»")
                           + (selection ? " (выделенный фрагмент)" : "")
                           + $" · строк: {rows} · {Seconds(result.Elapsed)}"
                           + (result.ResultSets.Any(s => s.Truncated) ? " · показаны не все строки" : "");

        ResultTabs.SelectedIndex = result.Success && result.ResultSets.Count > 0 ? 0 : ResultTabs.Items.Count - 1;
        UpdateResultButtons();
    }

    private static DataGrid CreateGrid(QueryResultSet set)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableColumnVirtualization = true,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader
        };
        // Через шаблон, иначе «_» в имени колонки съедается как горячая клавиша.
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        var header = new DataTemplate { VisualTree = text };

        for (var i = 0; i < set.Columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = set.Columns[i],
                HeaderTemplate = header,
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay, Converter = CellConverter.Instance },
                MaxWidth = 500
            });
        }

        grid.ItemsSource = set.Rows;
        return grid;
    }

    private (QueryResultSet Set, DataGrid Grid)? CurrentResult() =>
        ResultTabs.SelectedItem is TabItem { Tag: QueryResultSet set, Content: DataGrid grid } ? (set, grid) : null;

    private void ResultTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ResultTabs))
            UpdateResultButtons();
    }

    private void UpdateResultButtons()
    {
        var hasResult = CurrentResult() is not null;
        CopyButton.IsEnabled = hasResult;
        ExportButton.IsEnabled = hasResult;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResult() is not { } current)
            return;

        var all = current.Grid.SelectedCells.Count <= 1;
        if (all)
            current.Grid.SelectAllCells();
        try
        {
            ApplicationCommands.Copy.Execute(null, current.Grid);
        }
        catch (ExternalException)
        {
            SummaryText.Text = "Буфер обмена занят другой программой, попробуйте ещё раз.";
        }

        if (all)
            current.Grid.UnselectAllCells();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResult() is not { } current)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"result_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, ResultFormatter.ToCsv(current.Set), new UTF8Encoding(true));
            SummaryText.Text = current.Set.Truncated
                ? "Сохранено. В файле только строки в пределах лимита."
                : "Сохранено в CSV.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = "Не удалось сохранить файл: " + ex.Message;
        }
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
            SummaryText.Text = "Не удалось открыть файл: " + ex.Message;
        }
    }

    private void Connections_Click(object sender, RoutedEventArgs e) =>
        new ConnectionsWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private sealed class CellConverter : IValueConverter
    {
        public static readonly CellConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ResultFormatter.Format(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ParameterRow
    {
        private readonly string _name;
        private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
        private readonly ComboBox _kind = new() { ItemsSource = ParameterKinds.Titles, SelectedIndex = 0 };
        private readonly TextBox _value = new() { Margin = new Thickness(0, 4, 0, 0) };
        private readonly CheckBox _isNull = new() { Content = "NULL", Margin = new Thickness(0, 4, 0, 0) };

        public ParameterRow(string name, Action kindChanged)
        {
            _name = name;
            _title.Text = name;
            _title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            _isNull.Checked += (_, _) => _value.IsEnabled = false;
            _isNull.Unchecked += (_, _) => _value.IsEnabled = true;
            _kind.SelectionChanged += (_, e) =>
            {
                e.Handled = true;
                kindChanged();
            };

            var panel = new StackPanel { Tag = name };
            panel.Children.Add(_title);
            panel.Children.Add(_kind);
            panel.Children.Add(_value);
            panel.Children.Add(_isNull);
            Element = panel;
        }

        public FrameworkElement Element { get; }

        public SqlParameterKind Kind => ParameterKinds.At(_kind.SelectedIndex);

        /// <summary>Подпись и тип из библиотеки; введённое значение не трогает.</summary>
        public void Describe(ScriptParameter parameter)
        {
            _title.Text = parameter.Label.Length > 0 ? $"{parameter.Label}  ({_name})" : _name;
            _title.TextWrapping = TextWrapping.Wrap;
            _kind.SelectedIndex = ParameterKinds.IndexOf(parameter.Kind);
        }

        public void SetValue(string value) => _value.Text = value;

        /// <summary>Значение по умолчанию подставляется только в пустое поле.</summary>
        public void SetDefault(string? value)
        {
            if (_value.Text.Length == 0 && value is not null)
                _value.Text = value;
        }

        public SqlParameterValue Value() => new(_name, Kind, _isNull.IsChecked == true ? null : _value.Text);
    }
}
