using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Sql;
using AutoProdScripts.Themes;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AutoProdScripts.Views.Queries;

/// <summary>
/// Редактор SQL выбранного шага. Скрипт либо хранится в цепочке (правится напрямую), либо взят из
/// библиотеки — тогда правки держатся отдельно, пока пользователь не решит, куда их сохранить.
/// </summary>
public partial class ChainView
{
    // Несохранённые правки скриптов библиотеки: Id скрипта -> текст.
    private readonly Dictionary<Guid, string> _drafts = new();
    private readonly DispatcherTimer _sqlTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Guid? _selectedStep;
    private bool _loadingEditor;

    private ChainStep? SelectedStep => _chain.Steps.FirstOrDefault(s => s.Id == _selectedStep);

    private void InitializeStepEditor()
    {
        _sqlTimer.Tick += (_, _) => SqlSettled();
        StepEditor.TextChanged += (_, _) => StepSqlChanged();
        Loaded += (_, _) =>
        {
            ApplyHighlighting();
            ThemeService.ThemeChanged -= ApplyHighlighting;
            ThemeService.ThemeChanged += ApplyHighlighting;
        };
    }

    private void ApplyHighlighting() =>
        StepEditor.SyntaxHighlighting = SqlHighlightingLoader.GetDefinition()
                                        ?? HighlightingManager.Instance.GetDefinition("TSQL");

    /// <summary>Скрипт библиотеки с учётом несохранённых правок.</summary>
    private LibraryScript? FindScript(Guid id)
    {
        var script = App.Library.Find(id);
        if (script is not null && _drafts.TryGetValue(id, out var sql))
            script.Sql = sql;
        return script;
    }

    private void SelectStep(Guid id)
    {
        if (_selectedStep == id)
            return;
        _selectedStep = id;
        MarkSelected();
        LoadStepEditor();
    }

    // Только меняем рамки: перестройка карточек уничтожила бы элемент, по которому щёлкают.
    private void MarkSelected()
    {
        foreach (var card in StepsHost.Children.OfType<Border>())
            card.SetResourceReference(Border.BorderBrushProperty, (Guid)card.Tag == _selectedStep ? "AccentBrush" : "BorderBrush");
    }

    private void LoadStepEditor()
    {
        _loadingEditor = true;
        var step = SelectedStep;
        var script = step is null ? null : step.Inline ?? FindScript(step.ScriptId);
        var sql = script?.Sql ?? string.Empty;
        if (StepEditor.Document.Text != sql)
        {
            StepEditor.Document.Text = sql;
            // Иначе Ctrl+Z принёс бы в этот шаг текст другого.
            StepEditor.Document.UndoStack.ClearAll();
        }

        StepEditor.IsEnabled = script is not null;
        StepNameBox.Text = step?.Inline?.Name ?? string.Empty;
        StepNameBox.Visibility = step?.Inline is not null ? Visibility.Visible : Visibility.Collapsed;
        _loadingEditor = false;
        UpdateStepHeader();
    }

    private void UpdateStepHeader()
    {
        var step = SelectedStep;
        var library = step is { Inline: null } ? App.Library.Find(step.ScriptId) : null;
        var changed = library is not null && _drafts.ContainsKey(library.Id);
        var number = step is null ? 0 : _chain.Steps.IndexOf(step) + 1;

        StepTitle.Text = step switch
        {
            null => "Добавьте шаг — здесь появится его SQL.",
            { Inline: not null } => $"Шаг {number}: скрипт хранится в цепочке. Название:",
            _ when library is null => $"Шаг {number}: скрипт удалён из библиотеки — выберите другой.",
            _ => $"Шаг {number}: из библиотеки «{library.Name}»" + (changed ? " · изменён, не сохранён" : string.Empty)
        };

        Show(ToLibraryButton, step?.Inline is not null);
        Show(UpdateLibraryButton, changed);
        Show(KeepInChainButton, library is not null);
        Show(RevertButton, changed);
        if (library is not null)
        {
            var used = App.Library.ChainsUsing(library.Id).Where(c => c.Id != _chain.Id).Select(c => c.Name).ToList();
            UpdateLibraryButton.ToolTip = used.Count == 0
                ? "Записать правки в скрипт библиотеки"
                : "Изменится и в цепочках: " + string.Join(", ", used);
        }

        var sql = step is null ? string.Empty : StepEditor.Text;
        if (string.IsNullOrWhiteSpace(sql))
        {
            SetStepCheck(step is null ? string.Empty : "Напишите SQL шага.", "FgMutedBrush");
            return;
        }

        var analysis = ReadOnlyValidator.Analyze(sql);
        SetStepCheck(
            analysis.IsAllowed
                ? "✔ Проверка «только чтение» пройдена."
                : "✖ " + string.Join(Environment.NewLine, analysis.Violations.Take(3)),
            analysis.IsAllowed ? "SuccessBrush" : "DangerBrush");
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void SetStepCheck(string text, string brush)
    {
        StepCheck.Text = text;
        StepCheck.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private void StepSqlChanged()
    {
        if (_loadingEditor || SelectedStep is not { } step)
            return;

        if (step.Inline is not null)
        {
            step.Inline.Sql = StepEditor.Text;
        }
        else if (App.Library.Find(step.ScriptId) is { } library)
        {
            if (StepEditor.Text == library.Sql)
                _drafts.Remove(library.Id);
            else
                _drafts[library.Id] = StepEditor.Text;
        }

        _sqlTimer.Stop();
        _sqlTimer.Start();
    }

    // После паузы в наборе: параметры шага и общие параметры могли измениться.
    private void SqlSettled()
    {
        _sqlTimer.Stop();
        Results.Clear();
        StepStatuses.Children.Clear();
        RebuildSteps();
        UpdateStepHeader();
    }

    private void StepName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || SelectedStep?.Inline is not { } inline)
            return;
        inline.Name = StepNameBox.Text;
        UpdateTitle();
    }

    private bool Confirm(string question) =>
        MessageBox.Show(Window.GetWindow(this), question, "Цепочка", MessageBoxButton.YesNo, MessageBoxImage.Warning)
        == MessageBoxResult.Yes;

    private void ChangeScript(ChainStep step, object? choice)
    {
        _selectedStep = step.Id;
        if (choice is "own")
        {
            // Берём копию текущего текста, чтобы было с чего начать.
            var previous = step.ScriptId;
            if (FindScript(previous) is { } source)
                Embed(step, source);
            else
                step.Inline = new LibraryScript();
            DropUnusedDraft(previous);
        }
        else if (choice is Guid id)
        {
            if (!string.IsNullOrWhiteSpace(step.Inline?.Sql)
                && !Confirm("Свой скрипт этого шага будет удалён из цепочки. Заменить скриптом из библиотеки?"))
            {
                Structural();
                return;
            }

            var previous = step.ScriptId;
            step.Inline = null;
            step.ScriptId = id;
            step.Bindings.Clear();
            DropUnusedDraft(previous);
        }

        Structural();
    }

    private void RemoveStep(int index)
    {
        var step = _chain.Steps[index];
        if (!string.IsNullOrWhiteSpace(step.Inline?.Sql)
            && !Confirm($"Убрать шаг {index + 1}? Его скрипт хранится только в цепочке и будет удалён."))
            return;

        _chain.Steps.RemoveAt(index);
        DropUnusedDraft(step.ScriptId);
        Structural();
    }

    private void DropUnusedDraft(Guid scriptId)
    {
        if (_chain.Steps.All(s => s.Inline is not null || s.ScriptId != scriptId))
            _drafts.Remove(scriptId);
    }

    /// <summary>Делает скрипт собственным для шага; подключение скрипта переносится на шаг.</summary>
    private static void Embed(ChainStep step, LibraryScript source)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        copy.Group = string.Empty;
        step.Inline = copy;
        step.ScriptId = Guid.Empty;
        if (step.ConnectionId is null && step.ConnectionName.Length == 0 && step.RegionDatabase.Length == 0)
        {
            step.RegionDatabase = source.RegionDatabase;
            step.ConnectionId = source.ConnectionId;
            step.ConnectionName = source.ConnectionName;
        }
    }

    private void KeepInChain_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is not { Inline: null } step || FindScript(step.ScriptId) is not { } source)
            return;

        var previous = step.ScriptId;
        Embed(step, source);
        DropUnusedDraft(previous);
        Structural();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is not { Inline: null } step || !Confirm("Отменить правки и вернуть текст из библиотеки?"))
            return;
        _drafts.Remove(step.ScriptId);
        Structural();
    }

    private void UpdateLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is not { Inline: null } step)
            return;

        var used = App.Library.ChainsUsing(step.ScriptId).Where(c => c.Id != _chain.Id).Select(c => c.Name).ToList();
        if (used.Count > 0 && !Confirm($"Скрипт используется и в цепочках: {string.Join(", ", used)}. Изменить его для всех?"))
            return;

        if (TryUpdateLibrary(step.ScriptId))
            Structural();
    }

    private bool TryUpdateLibrary(Guid scriptId)
    {
        if (FindScript(scriptId) is not { } script)
        {
            _drafts.Remove(scriptId);
            return true;
        }

        // Подписи и типы прежних параметров сохраняются, убранные из текста — удаляются.
        var analysis = ReadOnlyValidator.Analyze(script.Sql);
        if (analysis.Batches.Count > 0)
        {
            script.Parameters = analysis.Parameters
                .Select(name => script.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                                ?? new ScriptParameter { Name = name })
                .ToList();
        }

        try
        {
            App.Library.Save(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Скрипт не сохранён", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _drafts.Remove(scriptId);
        return true;
    }

    /// <summary>Перед сохранением цепочки решаем, куда записать правки скриптов библиотеки.</summary>
    private bool ResolveDrafts()
    {
        if (_drafts.Count == 0)
            return true;
        // Иначе правки записались бы, а сама цепочка не сохранилась.
        if (NameBox.Text.Trim().Length == 0)
            return true;

        var names = string.Join(", ", _drafts.Keys.Select(id => App.Library.Find(id)?.Name).OfType<string>());
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Изменены скрипты из библиотеки: {names}.\n\n"
            + "«Да» — обновить их в библиотеке (изменятся везде).\n"
            + "«Нет» — оставить изменённые копии только в этой цепочке.",
            "Куда сохранить правки", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
            return false;

        foreach (var id in _drafts.Keys.ToList())
        {
            if (answer == MessageBoxResult.Yes)
            {
                if (!TryUpdateLibrary(id))
                    return false;
                continue;
            }

            if (FindScript(id) is { } source)
            {
                foreach (var step in _chain.Steps.Where(s => s.Inline is null && s.ScriptId == id).ToList())
                    Embed(step, source);
            }

            _drafts.Remove(id);
        }

        return true;
    }

    private void ToLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is not { Inline: { } inline } step)
            return;
        if (string.IsNullOrWhiteSpace(inline.Sql))
        {
            SetStepCheck("Сначала напишите SQL.", "WarnBrush");
            return;
        }

        var script = inline.Clone();
        script.Id = Guid.NewGuid();
        script.Group = ScriptLibrary.NormalizeGroup(GroupBox.Text ?? string.Empty);
        script.ConnectionId = step.ConnectionId;
        script.ConnectionName = step.ConnectionName;
        script.RegionDatabase = step.RegionDatabase.Trim();
        var analysis = ReadOnlyValidator.Analyze(script.Sql);
        script.Parameters = analysis.Parameters
            .Select(name =>
            {
                var parameter = (_described.GetValueOrDefault(name) ?? new ScriptParameter()).Clone();
                parameter.Name = name;
                parameter.DefaultValue = null;
                return parameter;
            })
            .ToList();

        var dialog = new ScriptPropertiesDialog(script) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Saved is not { } saved)
            return;

        step.Inline = null;
        step.ScriptId = saved.Id;
        // Подключение теперь берётся из скрипта.
        step.ConnectionId = null;
        step.ConnectionName = string.Empty;
        step.RegionDatabase = string.Empty;
        Structural();
    }
}
