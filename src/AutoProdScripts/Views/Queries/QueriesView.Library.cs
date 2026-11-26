using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Views.Queries;

/// <summary>Связь редактора с библиотекой: какой скрипт открыт и есть ли несохранённые изменения.</summary>
public partial class QueriesView
{
    private LibraryScript? _current;

    // Для ещё не сохранённого скрипта: текст, с которым сравниваем, и предлагаемое название.
    private string _draftText = string.Empty;
    private string _draftName = string.Empty;

    private bool InChain => Chain.Visibility == Visibility.Visible;

    private void ShowArea(bool chain)
    {
        Chain.Visibility = chain ? Visibility.Visible : Visibility.Collapsed;
        ScriptArea.Visibility = chain ? Visibility.Collapsed : Visibility.Visible;
        ScriptToolbar.Visibility = ScriptArea.Visibility;
    }

    private void OpenItem(Guid id)
    {
        if (App.Library.FindChain(id) is { } chain)
            OpenChain(chain);
        else
            OpenScript(id);
    }

    private void OpenChain(Core.Library.LibraryChain chain)
    {
        if (_running is not null || Chain.IsBusy || InChain && Chain.ChainId == chain.Id)
            return;
        if (!ConfirmDiscard())
        {
            Library.Select(InChain ? Chain.ChainId : _current?.Id);
            return;
        }

        // Редактор скрипта очищаем, чтобы в нём не остались скрытые несохранённые правки.
        if (!InChain)
            ShowDraft(string.Empty, string.Empty);
        ShowArea(chain: true);
        Chain.Open(chain);
        Library.Select(chain.Id);
    }

    private void NewChain()
    {
        if (!ConfirmDiscard())
            return;
        if (!InChain)
            ShowDraft(string.Empty, string.Empty);
        ShowArea(chain: true);
        Chain.Open(new Core.Library.LibraryChain());
        Library.Select(null);
    }

    /// <returns>false — пользователь остался в цепочке.</returns>
    private bool NewScript()
    {
        if (!ConfirmDiscard())
            return false;
        ShowArea(chain: false);
        ShowDraft(string.Empty, string.Empty);
        return true;
    }

    private void ImportFile()
    {
        if (_running is not null || Chain.IsBusy)
            return;

        var owner = Window.GetWindow(this);
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"Обмен AutoProdScripts (*{LibraryPackage.Extension})|*{LibraryPackage.Extension}|Все файлы (*.*)|*.*"
        };
        if (picker.ShowDialog(owner) != true)
            return;

        LibraryPackage package;
        try
        {
            if (new FileInfo(picker.FileName).Length > 40_000_000)
                throw new InvalidDataException("Файл слишком большой.");
            package = LibraryExchange.Parse(File.ReadAllText(picker.FileName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, ex.Message, "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Открытая запись может обновиться из файла: сначала решаем, что с несохранёнными правками.
        if (!ConfirmDiscard())
            return;
        var dialog = new ImportDialog(package, Path.GetFileName(picker.FileName)) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
            return;

        // Иначе «Сохранить» вернуло бы поверх импортированного старую открытую копию.
        ShowArea(chain: false);
        ShowDraft(string.Empty, string.Empty);

        var broken = App.Library.GetChains()
            .Where(c => c.Steps.Any(s => s.Inline is null && App.Library.Find(s.ScriptId) is null))
            .Select(c => c.Name)
            .ToList();
        var message = $"Добавлено: {result.Added}, обновлено: {result.Updated}, пропущено: {result.Skipped}."
                      + string.Concat(result.Errors.Select(e => "\n" + e))
                      + (broken.Count > 0 ? $"\n\nВ этих цепочках не хватает скриптов: {string.Join(", ", broken)}." : string.Empty);
        MessageBox.Show(owner, message, "Импорт", MessageBoxButton.OK,
            result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ShowItemProperties(Guid id)
    {
        if (App.Library.FindChain(id) is { } chain)
            OpenChain(chain);
        else
            ShowProperties(id);
    }

    private void DeleteItem(Guid id)
    {
        if (App.Library.FindChain(id) is not { } chain)
        {
            DeleteScript(id);
            return;
        }

        var answer = MessageBox.Show(Window.GetWindow(this), $"Удалить цепочку «{chain.Name}»? Скрипты останутся.",
            "Библиотека", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            App.Library.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Results.Summary = "Не удалось удалить: " + ex.Message;
            return;
        }

        if (InChain && Chain.ChainId == id)
        {
            ShowArea(chain: false);
            ShowDraft(string.Empty, string.Empty);
        }
    }

    private bool IsModified
    {
        get
        {
            if (_current is null)
                return Editor.Text != _draftText && !string.IsNullOrWhiteSpace(Editor.Text);

            return Editor.Text != _current.Sql
                   || _current.Parameters.Any(p => _rows.TryGetValue(p.Name, out var row) && row.Kind != p.Kind);
        }
    }

    private void UpdateHeader()
    {
        var name = _current?.Name ?? (_draftName.Length > 0 ? _draftName : "Новый скрипт");
        TitleText.Text = IsModified ? name + " •" : name;
        TitleText.ToolTip = _current is null ? "Скрипт ещё не сохранён в библиотеке" : _current.Group;

        var description = _current?.Description ?? string.Empty;
        DescriptionText.Text = description.ReplaceLineEndings(" ");
        DescriptionText.ToolTip = description.Length > 0 ? description : null;
        DescriptionText.Visibility = description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PropertiesButton.IsEnabled = _current is not null;
    }

    private ParameterRow CreateRow(string name)
    {
        var row = new ParameterRow(name, UpdateHeader, async () => await RunAsync());
        if (_current?.Parameters.FirstOrDefault(p => SameName(p.Name, name)) is { } saved)
        {
            row.Describe(saved);
            row.SetValue(saved.DefaultValue ?? string.Empty);
        }

        return row;
    }

    private void ClearParameters_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Values)
            row.Clear();
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Заменяет содержимое редактора и забывает значения параметров прежнего скрипта.</summary>
    private void ResetEditor(string sql)
    {
        _rows.Clear();
        ParametersHost.Children.Clear();
        Editor.Document.Text = sql;
        // Иначе Ctrl+Z вернул бы текст прежнего скрипта, а «Сохранить» записал бы его в этот.
        Editor.Document.UndoStack.ClearAll();
        Results.Clear();
    }

    private void ShowDraft(string sql, string name)
    {
        _current = null;
        _draftText = sql;
        _draftName = name;
        ResetEditor(sql);
        Library.Select(null);
        CheckScript();
    }

    private void OpenScript(Guid id)
    {
        if (!InChain && _current?.Id == id)
            return;
        if (!ConfirmDiscard() || App.Library.Find(id) is not { } script)
        {
            // Выделение в списке должно показывать то, что открыто на самом деле.
            Library.Select(InChain ? Chain.ChainId : _current?.Id);
            return;
        }

        ShowArea(chain: false);
        _current = script;
        _draftText = _draftName = string.Empty;
        ResetEditor(script.Sql);
        SelectScriptConnection(script);
        Library.Select(id);
        CheckScript();
    }

    private void SelectScriptConnection(LibraryScript script)
    {
        var profile = ScriptConnection.Resolve(script, App.Connections.GetAll());
        // Если подключения нет, не оставляем выбранным чужое: запрос ушёл бы не в ту базу.
        ConnectionBox.SelectedItem = ConnectionBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => ((Core.Connections.DbConnectionProfile)i.Tag).Id == profile?.Id);
        if (profile is null && script.ConnectionName.Length > 0)
            Results.Summary = $"Подключения «{script.ConnectionName}» на этом ПК нет. Выберите другое.";
    }

    /// <summary>Параметры текущего текста вместе с сохранёнными подписями и значениями по умолчанию.</summary>
    private List<ScriptParameter> CollectParameters()
    {
        var saved = _current?.Parameters ?? [];
        var analysis = ReadOnlyValidator.Analyze(Editor.Text);
        // Текст не разбирается — список параметров неизвестен, оставляем прежний.
        if (analysis.Batches.Count == 0 && !string.IsNullOrWhiteSpace(Editor.Text))
            return saved.Select(p => p.Clone()).ToList();

        return analysis.Parameters.Select(name =>
        {
            var parameter = saved.FirstOrDefault(p => SameName(p.Name, name))?.Clone()
                            ?? new ScriptParameter { Name = name };
            if (_rows.TryGetValue(name, out var row))
                parameter.Kind = row.Kind;
            return parameter;
        }).ToList();
    }

    /// <returns>false — скрипт не сохранён.</returns>
    public bool SaveScript()
    {
        if (InChain)
            return Chain.Save();
        if (_running is not null)
            return false;
        CheckScript();
        if (_current is null)
            return SaveAs();

        // Подключение меняется только в «Свойствах»: временный выбор другого для проверки не запоминаем.
        var script = _current.Clone();
        script.Sql = Editor.Text;
        script.Parameters = CollectParameters();
        try
        {
            _current = App.Library.Save(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Results.Summary = "Не удалось сохранить: " + ex.Message;
            return false;
        }

        Results.Summary = "Скрипт сохранён.";
        UpdateHeader();
        return true;
    }

    private bool SaveAs()
    {
        var script = _current?.Clone() ?? new LibraryScript { Name = _draftName };
        if (_current is not null)
        {
            script.Id = Guid.NewGuid();
            script.Name = App.Library.MakeUniqueName(script.Name, script.Group);
        }

        script.Sql = Editor.Text;
        script.Parameters = CollectParameters();
        if (SelectedConnection is { } profile)
        {
            script.ConnectionId = profile.Id;
            script.ConnectionName = profile.Name;
        }

        var dialog = new ScriptPropertiesDialog(script) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Saved is not { } saved)
            return false;

        _current = saved;
        _draftText = _draftName = string.Empty;
        ApplyMetadata(saved);
        Library.Select(saved.Id);
        Results.Summary = "Скрипт сохранён в библиотеке.";
        UpdateHeader();
        return true;
    }

    private void ApplyMetadata(LibraryScript script)
    {
        foreach (var parameter in script.Parameters)
        {
            if (!_rows.TryGetValue(parameter.Name, out var row))
                continue;
            row.Describe(parameter);
            row.SetDefault(parameter.DefaultValue);
        }
    }

    private void ShowProperties(Guid id)
    {
        if (App.Library.Find(id) is not { } script)
            return;

        // У открытого скрипта показываем и параметры, появившиеся в ещё не сохранённом тексте.
        if (_current?.Id == id)
        {
            var edited = CollectParameters();
            script.Parameters = edited
                .Concat(script.Parameters.Where(p => !edited.Any(e => SameName(e.Name, p.Name))))
                .ToList();
        }

        var dialog = new ScriptPropertiesDialog(script) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Saved is not { } saved || _current?.Id != id)
            return;

        // Несохранённый текст в редакторе остаётся: свойства его не трогают.
        _current = saved;
        ApplyMetadata(saved);
        SelectScriptConnection(saved);
        UpdateHeader();
    }

    private void DeleteScript(Guid id)
    {
        if (App.Library.Find(id) is not { } script)
            return;

        var used = App.Library.ChainsUsing(id);
        var warning = used.Count == 0
            ? string.Empty
            : $"\n\nОн используется в цепочках: {string.Join(", ", used.Select(c => c.Name))}. Они перестанут запускаться.";
        var answer = MessageBox.Show(Window.GetWindow(this), $"Удалить скрипт «{script.Name}» из библиотеки?{warning}",
            "Библиотека", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            App.Library.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Results.Summary = "Не удалось удалить: " + ex.Message;
            return;
        }

        if (InChain)
            Chain.Reload();
        if (_current?.Id != id)
            return;

        // Текст остаётся в редакторе как несохранённый черновик.
        _current = null;
        _draftText = string.Empty;
        _draftName = script.Name;
        UpdateHeader();
    }

    private void MarkRun(Guid id)
    {
        try
        {
            App.Library.MarkRun(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log.Warn("Не удалось записать дату запуска скрипта.");
        }
    }

    /// <returns>false — пользователь решил остаться в текущем скрипте.</returns>
    public bool ConfirmDiscard()
    {
        if (!_initialized || !(InChain ? Chain.IsModified : IsModified))
            return true;

        var what = InChain ? "цепочки" : "скрипта";
        var answer = MessageBox.Show(Window.GetWindow(this), $"Сохранить изменения {what} в библиотеке?", "Запросы",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveScript(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscard())
            ShowDraft(string.Empty, string.Empty);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveScript();

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null)
            ShowProperties(_current.Id);
    }
}
