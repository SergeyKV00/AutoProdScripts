using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Chains;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Views.Queries;

/// <summary>Настройка и запуск одной цепочки.</summary>
public partial class ChainView : UserControl
{
    private readonly Dictionary<string, ParameterRow> _runRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptParameter> _described = new(StringComparer.OrdinalIgnoreCase);
    private LibraryChain _chain = new();
    private string _baseline = string.Empty;
    private CancellationTokenSource? _running;
    private bool _loading;

    public ChainView()
    {
        InitializeComponent();
        InitializeStepEditor();
    }

    /// <summary>Запрошен переход к редактору скриптов.</summary>
    public event Action? NewScriptRequested;

    public event Action<bool>? BusyChanged;

    public Guid ChainId => _chain.Id;

    public bool IsBusy => _running is not null;

    private bool IsSaved => App.Library.FindChain(_chain.Id) is not null;

    public bool IsModified => _drafts.Count > 0 || Snapshot() != _baseline;

    public void Open(LibraryChain chain)
    {
        _loading = true;
        _chain = chain.Clone();
        _runRows.Clear();
        _described.Clear();
        foreach (var parameter in _chain.Parameters)
            _described[parameter.Name] = parameter.Clone();

        NameBox.Text = _chain.Name;
        GroupBox.ItemsSource = App.Library.Groups();
        GroupBox.Text = _chain.Group;
        TagsBox.Text = string.Join(", ", _chain.Tags);
        DescriptionBox.Text = _chain.Description;
        _drafts.Clear();
        _selectedStep = _chain.Steps.FirstOrDefault()?.Id;
        Results.Clear();
        StepStatuses.Children.Clear();
        _loading = false;

        RebuildSteps();
        LoadStepEditor();
        _baseline = Snapshot();
        UpdateTitle();
        Tabs.SelectedItem = IsSaved ? RunTab : EditTab;
    }

    /// <summary>Перечитать скрипты и подключения, не теряя несохранённых правок.</summary>
    public void Reload()
    {
        RebuildSteps();
        LoadStepEditor();
    }

    private LibraryChain Collect()
    {
        var chain = _chain.Clone();
        chain.Name = NameBox.Text.Trim();
        chain.Group = ScriptLibrary.NormalizeGroup(GroupBox.Text ?? string.Empty);
        chain.Tags = ScriptLibrary.ParseTags(TagsBox.Text);
        chain.Description = DescriptionBox.Text.Trim();
        chain.Parameters = Plan().CommonParameters
            .Select(p => (_described.GetValueOrDefault(p.Name) ?? p).Clone())
            .ToList();
        return chain;
    }

    private string Snapshot()
    {
        var chain = Collect();
        chain.UpdatedUtc = default;
        return JsonSerializer.Serialize(chain);
    }

    private ChainPlan Plan() => ChainPlan.Build(_chain, FindScript);

    private void UpdateTitle()
    {
        var name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Новая цепочка";
        TitleText.Text = "🔗 " + name + (IsModified ? " •" : "");
        TitleText.ToolTip = _chain.Description.Length > 0 ? _chain.Description : null;
    }

    private void Property_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
            UpdateTitle();
    }

    private void Property_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            UpdateTitle();
    }

    // ───── Настройка шагов ─────

    private void RebuildSteps()
    {
        _loading = true;
        StepsHost.Children.Clear();
        var scripts = App.Library.GetAll()
            .OrderBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var connections = App.Connections.GetAll();
        if (_chain.Steps.All(s => s.Id != _selectedStep))
            _selectedStep = _chain.Steps.FirstOrDefault()?.Id;
        for (var i = 0; i < _chain.Steps.Count; i++)
            StepsHost.Children.Add(StepCard(i, scripts, connections));
        _loading = false;
        MarkSelected();
        RefreshParameters();
    }

    private FrameworkElement StepCard(int index, IReadOnlyList<LibraryScript> scripts, IReadOnlyList<DbConnectionProfile> connections)
    {
        var step = _chain.Steps[index];
        var panel = new StackPanel();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(SmallButton("▲", "Выше", index > 0, () => Move(index, -1)));
        buttons.Children.Add(SmallButton("▼", "Ниже", index < _chain.Steps.Count - 1, () => Move(index, 1)));
        buttons.Children.Add(SmallButton("✕", "Убрать шаг", true, () => RemoveStep(index)));
        var head = new DockPanel();
        head.Children.Add(buttons);
        head.Children.Add(Text($"Шаг {index + 1}", "FgBrush", bold: true));
        panel.Children.Add(head);

        panel.Children.Add(Caption("Скрипт (текст редактируется справа)"));
        var script = new ComboBox();
        var own = new ComboBoxItem { Content = "✎ Свой скрипт, хранится в цепочке", Tag = "own" };
        script.Items.Add(own);
        if (step.Inline is not null)
            script.SelectedItem = own;
        foreach (var item in scripts)
        {
            var title = item.Group.Length > 0 ? $"{item.Group} / {item.Name}" : item.Name;
            var option = new ComboBoxItem { Content = title, Tag = item.Id };
            script.Items.Add(option);
            if (step.Inline is null && item.Id == step.ScriptId)
                script.SelectedItem = option;
        }

        script.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            if (!_loading)
                ChangeScript(step, (script.SelectedItem as ComboBoxItem)?.Tag);
        };
        panel.Children.Add(script);

        var selected = step.Inline ?? FindScript(step.ScriptId);
        panel.Children.Add(Caption("Подключение"));
        panel.Children.Add(ConnectionChoice(step, selected, connections));

        var stop = new CheckBox
        {
            Content = "Не выполнять следующие шаги, если строк нет",
            IsChecked = step.StopWhenEmpty,
            Margin = new Thickness(0, 10, 0, 0)
        };
        stop.Click += (_, _) =>
        {
            step.StopWhenEmpty = stop.IsChecked == true;
            UpdateTitle();
        };
        panel.Children.Add(stop);

        panel.Children.Add(Caption("Что показать, если строк нет (например «Сотрудника нет в СУР»)"));
        panel.Children.Add(Input(step.EmptyMessage, value => step.EmptyMessage = value));
        panel.Children.Add(Caption("Передать результат дальше как временную таблицу (например #people), необязательно"));
        panel.Children.Add(Input(step.OutputTable, value => step.OutputTable = value.Trim(), RefreshParameters));

        if (selected is not null && !string.IsNullOrWhiteSpace(selected.Sql))
            AddBindings(panel, index, selected);

        var card = new Border
        {
            Child = panel,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        card.Tag = step.Id;
        // Щелчок или переход в любое поле шага показывает его SQL справа.
        card.PreviewMouseDown += (_, _) => SelectStep(step.Id);
        card.GotKeyboardFocus += (_, _) => SelectStep(step.Id);
        return card;
    }

    private ComboBox ConnectionChoice(ChainStep step, LibraryScript? script, IReadOnlyList<DbConnectionProfile> connections)
    {
        var box = new ComboBox();
        var own = script is null ? null : ScriptConnection.Resolve(script, connections)?.Name ?? script.ConnectionName;
        // У своего скрипта шага отдельного подключения нет — оно выбирается здесь.
        var none = step.Inline is not null
            ? "Выберите подключение"
            : $"Как в скрипте: {(string.IsNullOrEmpty(own) ? "не задано" : own)}";
        box.Items.Add(new ComboBoxItem { Content = none });
        box.SelectedIndex = 0;

        var current = ScriptConnection.Resolve(step.ConnectionId, step.ConnectionName, connections);
        foreach (var profile in connections)
        {
            var item = new ComboBoxItem { Content = profile.Name, Tag = profile };
            box.Items.Add(item);
            if (profile.Id == current?.Id)
                box.SelectedItem = item;
        }

        if (current is null && step.ConnectionName.Length > 0)
        {
            var missing = new ComboBoxItem { Content = $"{step.ConnectionName} (нет на этом ПК)", Tag = step.ConnectionName };
            box.Items.Add(missing);
            box.SelectedItem = missing;
        }

        box.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            var tag = (box.SelectedItem as ComboBoxItem)?.Tag;
            if (tag is string)
                return;
            var profile = tag as DbConnectionProfile;
            step.ConnectionId = profile?.Id;
            step.ConnectionName = profile?.Name ?? string.Empty;
            UpdateTitle();
        };
        return box;
    }

    private void AddBindings(StackPanel panel, int index, LibraryScript script)
    {
        var step = _chain.Steps[index];
        var parameters = ReadOnlyValidator.Analyze(script.Sql).Parameters;
        if (parameters.Count == 0)
        {
            panel.Children.Add(Caption("В скрипте нет параметров. Значения, заданные через DECLARE, берутся из самого скрипта."));
            return;
        }

        panel.Children.Add(Caption("Параметры шага: откуда брать и как называется общий параметр или колонка"));
        foreach (var name in parameters)
        {
            var binding = step.Bindings.FirstOrDefault(b => string.Equals(b.Parameter, name, StringComparison.OrdinalIgnoreCase));
            var source = new ComboBox();
            source.Items.Add(new ComboBoxItem { Content = "Общий параметр" });
            source.SelectedIndex = 0;
            for (var previous = 0; previous < index; previous++)
            {
                var item = new ComboBoxItem { Content = $"Колонка шага {previous + 1}", Tag = _chain.Steps[previous].Id };
                source.Items.Add(item);
                if (binding?.FromStep == _chain.Steps[previous].Id)
                    source.SelectedItem = item;
            }

            var value = new TextBox
            {
                Text = !string.IsNullOrEmpty(binding?.Source) ? binding.Source : name,
                Margin = new Thickness(6, 0, 0, 0)
            };

            void Apply()
            {
                var from = (source.SelectedItem as ComboBoxItem)?.Tag as Guid?;
                var text = value.Text.Trim();
                step.Bindings.RemoveAll(b => string.Equals(b.Parameter, name, StringComparison.OrdinalIgnoreCase));
                // Общий параметр с тем же именем — это поведение по умолчанию, записывать нечего.
                if (from is not null || text.Length > 0 && !string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
                    step.Bindings.Add(new ChainBinding { Parameter = name, FromStep = from, Source = text });
                UpdateTitle();
            }

            source.SelectionChanged += (_, e) =>
            {
                e.Handled = true;
                // Колонка обычно называется как параметр, только без «@».
                value.Text = (source.SelectedItem as ComboBoxItem)?.Tag is Guid ? name.TrimStart('@') : name;
                Apply();
                RefreshParameters();
            };
            value.TextChanged += (_, _) => Apply();
            value.LostFocus += (_, _) => RefreshParameters();

            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            var label = Text(name, "FgBrush");
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(source, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(label);
            row.Children.Add(source);
            row.Children.Add(value);
            panel.Children.Add(row);
        }
    }

    private void Move(int index, int offset)
    {
        var step = _chain.Steps[index];
        _chain.Steps.RemoveAt(index);
        _chain.Steps.Insert(index + offset, step);
        Structural();
    }

    // Изменился состав шагов: прежние результаты и статусы к нему уже не относятся.
    private void Structural()
    {
        Results.Clear();
        StepStatuses.Children.Clear();
        RebuildSteps();
        LoadStepEditor();
        UpdateTitle();
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        var step = new ChainStep { Inline = new LibraryScript() };
        _chain.Steps.Add(step);
        _selectedStep = step.Id;
        Structural();
        StepEditor.Focus();
    }

    /// <summary>Общие параметры и замечания пересчитываются после любого изменения шагов.</summary>
    private void RefreshParameters()
    {
        var plan = Plan();

        RunParameters.Children.Clear();
        CommonHost.Children.Clear();
        foreach (var parameter in plan.CommonParameters)
        {
            if (!_described.TryGetValue(parameter.Name, out var described))
                _described[parameter.Name] = described = parameter.Clone();

            if (!_runRows.TryGetValue(parameter.Name, out var row))
            {
                _runRows[parameter.Name] = row = new ParameterRow(parameter.Name, null, async () => await RunAsync());
                row.SetValue(described.DefaultValue ?? string.Empty);
            }

            row.Describe(described);
            RunParameters.Children.Add(row.Element);
            CommonHost.Children.Add(CommonEditor(described));
        }

        if (plan.CommonParameters.Count == 0)
        {
            RunParameters.Children.Add(Caption("Общих параметров нет."));
            CommonHost.Children.Add(Caption("Появятся после добавления шагов со скриптами, где есть параметры."));
        }

        ProblemsText.Text = plan.IsValid
            ? "✔ Цепочка готова к запуску."
            : "✖ " + string.Join(Environment.NewLine, plan.Problems.Take(3));
        ProblemsText.SetResourceReference(TextBlock.ForegroundProperty, plan.IsValid ? "SuccessBrush" : "DangerBrush");
        UpdateTitle();
    }

    private void ClearParameters_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _runRows.Values)
            row.Clear();
    }

    private FrameworkElement CommonEditor(ScriptParameter parameter)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        double[] widths = [0.9, 1.2, 1.8, 1];
        var label = new TextBox { Text = parameter.Label, ToolTip = "Подпись" };
        var kind = ParameterKinds.Combo(parameter.Kind);
        var fallback = new TextBox { Text = parameter.DefaultValue ?? string.Empty, ToolTip = "Значение по умолчанию" };
        label.TextChanged += (_, _) =>
        {
            parameter.Label = label.Text.Trim();
            _runRows[parameter.Name].Describe(parameter);
            UpdateTitle();
        };
        kind.SelectionChanged += (_, e) =>
        {
            e.Handled = true;
            parameter.Kind = ParameterKinds.At(kind.SelectedIndex);
            UpdateTitle();
        };
        fallback.TextChanged += (_, _) =>
        {
            parameter.DefaultValue = fallback.Text.Length == 0 ? null : fallback.Text;
            _runRows[parameter.Name].SetDefault(parameter.DefaultValue);
            UpdateTitle();
        };

        var name = Text(parameter.Name, "FgBrush");
        name.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement[] cells = [name, label, kind, fallback];
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i], GridUnitType.Star) });
            cells[i].Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }

        return grid;
    }

    private static TextBlock Text(string text, string brush, bool bold = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (bold)
            block.FontWeight = FontWeights.SemiBold;
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    private static TextBlock Caption(string text)
    {
        var block = Text(text, "FgMutedBrush");
        block.Margin = new Thickness(0, 8, 0, 4);
        return block;
    }

    private TextBox Input(string value, Action<string> changed, Action? finished = null)
    {
        var box = new TextBox { Text = value };
        box.TextChanged += (_, _) =>
        {
            changed(box.Text);
            UpdateTitle();
        };
        if (finished is not null)
            box.LostFocus += (_, _) => finished();
        return box;
    }

    private static Button SmallButton(string text, string tooltip, bool enabled, Action click)
    {
        var button = new Button { Content = text, ToolTip = tooltip, IsEnabled = enabled, Margin = new Thickness(4, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        button.Click += (_, _) => click();
        return button;
    }

    // ───── Сохранение ─────

    /// <returns>false — цепочка не сохранена.</returns>
    public bool Save()
    {
        if (IsBusy || !ResolveDrafts())
            return false;

        try
        {
            var chain = Collect();
            foreach (var parameter in chain.Parameters.Where(p => p.DefaultValue is not null))
                new SqlParameterValue(parameter.Name, parameter.Kind, parameter.DefaultValue).ToSqlParameter();
            _chain = App.Library.SaveChain(chain);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Цепочка не сохранена", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Tabs.SelectedItem = EditTab;
            return false;
        }

        RebuildSteps();
        LoadStepEditor();
        _baseline = Snapshot();
        UpdateTitle();
        Results.Summary = "Цепочка сохранена.";
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void NewScript_Click(object sender, RoutedEventArgs e) => NewScriptRequested?.Invoke();

    // ───── Запуск ─────

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    public async Task RunAsync()
    {
        if (IsBusy)
            return;

        Tabs.SelectedItem = RunTab;
        StepStatuses.Children.Clear();
        Results.Clear();
        RefreshParameters();

        var plan = Plan();
        if (!plan.IsValid)
        {
            Results.ShowFailure(string.Join(Environment.NewLine, plan.Problems));
            return;
        }

        if (!int.TryParse(MaxRowsBox.Text.Trim(), out var maxRows) || maxRows <= 0)
        {
            Results.ShowFailure("Лимит строк должен быть положительным числом.");
            return;
        }

        var connections = App.Connections.GetAll();
        var profiles = new List<DbConnectionProfile?>();
        var request = new ChainRunRequest
        {
            Chain = Collect(),
            // Выполняется то, что на экране, включая ещё не сохранённые правки скриптов.
            FindScript = FindScript,
            Connect = (step, script) =>
            {
                var number = plan.Steps.ToList().FindIndex(s => s.Step.Id == step.Id) + 1;
                var profile = (step.ConnectionName.Length > 0 || step.ConnectionId is not null
                                  ? ScriptConnection.Resolve(step.ConnectionId, step.ConnectionName, connections)
                                  : ScriptConnection.Resolve(script, connections))
                              ?? throw new InvalidOperationException($"Шаг {number}: нет подключения. Выберите его в настройке шага.");
                var password = profile.AuthMode == DbAuthMode.SqlLogin ? App.Connections.LoadPassword(profile.Id) : null;
                if (profile.AuthMode == DbAuthMode.SqlLogin && password is null)
                    throw new InvalidOperationException($"Для подключения «{profile.Name}» не сохранён пароль.");
                profiles.Add(profile);
                return new StepConnection(profile.Name, ConnectionStringFactory.Build(profile, password), profile.CommandTimeoutSeconds);
            },
            Values = plan.CommonParameters.ToDictionary(p => p.Name, p => _runRows[p.Name].Text, StringComparer.OrdinalIgnoreCase),
            MaxRows = maxRows,
            Choose = choice => Dispatcher.InvokeAsync(() =>
            {
                var dialog = new ChoiceDialog(choice) { Owner = Window.GetWindow(this) };
                return dialog.ShowDialog() == true ? dialog.Selected : null;
            }).Task
        };

        var source = _running = new CancellationTokenSource();
        SetBusy(true);
        Results.Summary = "Выполняется…";
        var watch = Stopwatch.StartNew();
        var done = new List<ChainStepResult>();
        try
        {
            // Названия параметров, значения и тексты запросов в лог не пишем.
            App.Log.Info($"Цепочка: запуск, шагов {plan.Steps.Count}.");
            var progress = new Progress<ChainStepResult>(result => Results.Summary = $"Выполнен шаг {result.Index + 1}…");
            done.AddRange(await Task.Run(() => ChainRunner.RunAsync(request, progress, source.Token)));
            ShowResults(done, profiles, watch.Elapsed);
            App.Log.Info($"Цепочка завершена за {ResultsPanel.Seconds(watch.Elapsed)}.");
            MarkRun();
        }
        catch (OperationCanceledException)
        {
            Results.ShowFailure("Выполнение отменено.");
            App.Log.Info("Цепочка отменена.");
        }
        catch (Exception ex)
        {
            // Любой сбой запуска показываем в «Сообщениях», а не роняем приложение.
            Results.ShowFailure(ex.Message);
            App.Log.Warn("Цепочка не выполнена.");
        }
        finally
        {
            _running = null;
            source.Dispose();
            SetBusy(false);
        }
    }

    private void ShowResults(IReadOnlyList<ChainStepResult> results, IReadOnlyList<DbConnectionProfile?> profiles, TimeSpan elapsed)
    {
        StepStatuses.Children.Clear();
        var sets = new List<(string, QueryResultSet)>();
        var messages = new List<string>();
        foreach (var result in results)
        {
            var (mark, brush) = result.Status switch
            {
                ChainStepStatus.Succeeded => ("✔", "SuccessBrush"),
                ChainStepStatus.Empty => ("○", "WarnBrush"),
                ChainStepStatus.Failed => ("✖", "DangerBrush"),
                _ => ("—", "FgMutedBrush")
            };
            var environment = profiles.ElementAtOrDefault(result.Index) is { } profile
                ? $" · {EnvironmentUi.Title(profile.Environment)}"
                : string.Empty;
            var title = $"{result.Index + 1}. {result.Title}  [{result.ConnectionName}{environment}]";
            var line = Text($"{mark} {title} — {result.Message}", brush);
            line.Margin = new Thickness(0, 2, 0, 2);
            StepStatuses.Children.Add(line);
            messages.Add($"{title}: {result.Message}");
            if (result.Result is null)
                continue;

            messages.AddRange(result.Result.Messages.Select(m => "    " + m));
            for (var i = 0; i < result.Result.ResultSets.Count; i++)
            {
                var number = result.Result.ResultSets.Count > 1 ? $".{i + 1}" : string.Empty;
                sets.Add(($"{result.Index + 1}{number}. {result.Title}", result.Result.ResultSets[i]));
            }
        }

        var failed = results.Any(r => r.Status == ChainStepStatus.Failed);
        Results.Show(sets, messages,
            (failed ? "Ошибка в одном из шагов" : "Цепочка выполнена") + $" · {ResultsPanel.Seconds(elapsed)}",
            openMessages: failed);
    }

    private void MarkRun()
    {
        try
        {
            App.Library.MarkRun(_chain.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log.Warn("Не удалось записать дату запуска цепочки.");
        }
    }

    private void SetBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        NewScriptButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        EditRoot.IsEnabled = !busy;
        RunParameters.IsEnabled = !busy;
        BusyChanged?.Invoke(busy);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _running?.Cancel();
}
