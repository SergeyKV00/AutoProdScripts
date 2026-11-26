using System.IO;
using System.Windows;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Views.Queries;

/// <summary>
/// Связь вкладок с библиотекой. Вкладка — открытый скрипт; в библиотеку он попадает только по «Сохранить».
/// </summary>
public partial class QueriesView
{
    private void ImportFile()
    {
        if (_documents.Any(d => d.IsRunning) || Chain.IsBusy)
        {
            MessageBox.Show(Window.GetWindow(this), "Дождитесь окончания запросов.", "Импорт",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

        // Открытая цепочка может обновиться из файла: сначала решаем, что с её несохранёнными правками.
        if (_chainDocument is not null && Chain.IsModified)
        {
            ActivateDocument(_chainDocument);
            if (!AskSave(_chainDocument, "Импорт"))
                return;
        }

        var dialog = new ImportDialog(package, Path.GetFileName(picker.FileName)) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
            return;

        // Иначе «Сохранить» в цепочке вернуло бы поверх импортированной старую открытую копию.
        if (_chainDocument is not null && App.Library.FindChain(Chain.ChainId) is { } chain)
            Chain.Open(chain);
        RefreshChrome();

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

    /// <summary>
    /// Библиотека изменилась (сохранение, свойства, импорт, удаление): вкладки, связанные со скриптами,
    /// получают новую сохранённую версию. Несохранённый текст вкладки при этом не трогаем.
    /// </summary>
    private void SyncWithLibrary()
    {
        foreach (var doc in _documents.Where(d => d.Saved is not null).ToList())
        {
            var old = doc.Saved!;
            var fresh = App.Library.Find(old.Id);
            if (fresh is null)
            {
                // Скрипт удалён из библиотеки — вкладка остаётся черновиком с тем же текстом.
                doc.Saved = null;
                doc.DraftText = string.Empty;
                doc.DraftName = old.Name;
                continue;
            }

            var unchanged = !doc.IsModified;
            doc.Saved = fresh;
            if (unchanged && doc.Document.Text != fresh.Sql && !doc.IsRunning)
            {
                var caret = doc == _active ? Editor.CaretOffset : doc.CaretOffset;
                SetText(doc, fresh.Sql);
                doc.CaretOffset = Math.Min(caret, fresh.Sql.Length);
            }

            ApplyMetadata(doc, fresh);
        }

        if (_active is { IsChain: false })
            UpdateHeader();
        RefreshChrome();
        ScheduleSessionSave();
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
            ShowMessage("Не удалось удалить: " + ex.Message);
            return;
        }

        // Вкладка удалённой цепочки закрывается: несохранённой копии цепочки нет.
        if (_chainDocument is { } open && Chain.ChainId == id && !Chain.IsBusy)
        {
            Chain.Open(new LibraryChain());
            CloseDocument(open);
        }
    }

    private void ShowMessage(string text)
    {
        if (_active is { IsChain: false } doc)
            doc.Results.Summary = text;
        else
            MessageBox.Show(Window.GetWindow(this), text, "Запросы", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdateHeader()
    {
        if (_active is not { IsChain: false } doc)
            return;

        var name = TitleOf(doc);
        TitleText.Text = doc.IsModified ? name + " •" : name;
        TitleText.ToolTip = doc.Saved is null ? "Скрипт ещё не сохранён в библиотеке" : doc.Saved.Group;

        // Под названием: где хранится скрипт и его описание.
        var place = doc.Saved is null
            ? "Не сохранён в библиотеке — «Сохранить», чтобы добавить"
            : "Библиотека" + (doc.Saved.Group.Length > 0 ? " / " + doc.Saved.Group : string.Empty);
        var description = doc.Saved?.Description ?? string.Empty;
        DescriptionText.Text = description.Length > 0 ? place + " · " + description.ReplaceLineEndings(" ") : place;
        DescriptionText.ToolTip = description.Length > 0 ? description : null;
        PropertiesButton.IsEnabled = doc.Saved is not null;
        RefreshChrome(doc);
    }

    private ParameterRow CreateRow(QueryDocument doc, string name)
    {
        var row = new ParameterRow(name, UpdateHeader, async () => await RunAsync());
        if (doc.Saved?.Parameters.FirstOrDefault(p => SameName(p.Name, name)) is { } saved)
        {
            row.Describe(saved);
            row.SetValue(saved.DefaultValue ?? string.Empty);
        }

        return row;
    }

    private void ClearParameters_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null)
            return;
        foreach (var row in _active.Rows.Values)
            row.Clear();
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Параметры текста вкладки вместе с сохранёнными подписями и значениями по умолчанию.</summary>
    private static List<ScriptParameter> CollectParameters(QueryDocument doc)
    {
        var saved = doc.Saved?.Parameters ?? [];
        var text = doc.Document.Text;
        var analysis = ReadOnlyValidator.Analyze(text);
        // Текст не разбирается — список параметров неизвестен, оставляем прежний.
        if (analysis.Batches.Count == 0 && !string.IsNullOrWhiteSpace(text))
            return saved.Select(p => p.Clone()).ToList();

        return analysis.Parameters.Select(name =>
        {
            var parameter = saved.FirstOrDefault(p => SameName(p.Name, name))?.Clone()
                            ?? new ScriptParameter { Name = name };
            if (doc.Rows.TryGetValue(name, out var row))
                parameter.Kind = row.Kind;
            return parameter;
        }).ToList();
    }

    /// <summary>Сохранить активную вкладку в библиотеку (Ctrl+S).</summary>
    /// <returns>false — не сохранено.</returns>
    public bool SaveScript()
    {
        if (_active is not { } doc)
            return false;
        if (doc.IsChain)
        {
            var saved = Chain.Save();
            RefreshChrome();
            SelectInLibrary();
            ScheduleSessionSave();
            return saved;
        }

        CheckScript();
        if (doc.Saved is null)
            return SaveAs();

        // Подключение скрипта меняется только в «Свойствах»: выбор во вкладке его не трогает.
        var script = doc.Saved.Clone();
        script.Sql = doc.Document.Text;
        script.Parameters = CollectParameters(doc);
        try
        {
            doc.Saved = App.Library.Save(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            doc.Results.Summary = "Не удалось сохранить: " + ex.Message;
            return false;
        }

        doc.Results.Summary = "Скрипт сохранён.";
        UpdateHeader();
        ScheduleSessionSave();
        return true;
    }

    /// <summary>Сохраняет вкладку в библиотеку новым скриптом; вкладка связывается с ним.</summary>
    private bool SaveAs()
    {
        if (_active is not { IsChain: false } doc)
            return false;

        var script = doc.Saved?.Clone() ?? new LibraryScript { Name = TitleOf(doc) };
        if (doc.Saved is not null)
        {
            script.Id = Guid.NewGuid();
            script.Name = App.Library.MakeUniqueName(script.Name, script.Group);
        }

        script.Sql = doc.Document.Text;
        script.Parameters = CollectParameters(doc);
        // Новому скрипту запоминаем подключение вкладки.
        if (doc.Regional)
        {
            script.ConnectionId = null;
            script.ConnectionName = string.Empty;
            script.RegionDatabase = doc.RegionDatabase.Trim();
        }
        else if (App.Connections.GetAll().FirstOrDefault(p => p.Id == doc.ConnectionId) is { } profile)
        {
            script.ConnectionId = profile.Id;
            script.ConnectionName = profile.Name;
            script.RegionDatabase = string.Empty;
        }

        var dialog = new ScriptPropertiesDialog(script) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Saved is not { } saved)
            return false;

        doc.Saved = saved;
        doc.DraftText = doc.DraftName = string.Empty;
        ApplyMetadata(doc, saved);
        SelectInLibrary();
        doc.Results.Summary = "Скрипт сохранён в библиотеке.";
        UpdateHeader();
        ScheduleSessionSave();
        return true;
    }

    private static void ApplyMetadata(QueryDocument doc, LibraryScript script)
    {
        foreach (var parameter in script.Parameters)
        {
            if (!doc.Rows.TryGetValue(parameter.Name, out var row))
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
        var doc = _documents.FirstOrDefault(d => d.ScriptId == id);
        if (doc is not null)
        {
            var edited = CollectParameters(doc);
            script.Parameters = edited
                .Concat(script.Parameters.Where(p => !edited.Any(e => SameName(e.Name, p.Name))))
                .ToList();
        }

        var dialog = new ScriptPropertiesDialog(script) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Saved is not { } saved || doc is null || !_documents.Contains(doc))
            return;

        // Несохранённый текст вкладки остаётся: свойства его не трогают. Подключение берём из свойств.
        doc.Saved = saved;
        ApplyMetadata(doc, saved);
        UseScriptConnection(doc, saved);
        if (doc == _active)
        {
            ShowConnection(doc);
            CheckScript();
        }

        RefreshChrome(doc);
        ScheduleSessionSave();
    }

    private void DeleteScript(Guid id)
    {
        if (App.Library.Find(id) is not { } script)
            return;

        var used = App.Library.ChainsUsing(id);
        var warning = used.Count == 0
            ? string.Empty
            : $"\n\nОн используется в цепочках: {string.Join(", ", used.Select(c => c.Name))}. Они перестанут запускаться.";
        var open = _documents.Any(d => d.ScriptId == id)
            ? "\n\nОткрытая вкладка останется как несохранённый черновик."
            : string.Empty;
        var answer = MessageBox.Show(Window.GetWindow(this), $"Удалить скрипт «{script.Name}» из библиотеки?{warning}{open}",
            "Библиотека", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            // Вкладки отвязываются в SyncWithLibrary.
            App.Library.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Не удалось удалить: " + ex.Message);
            return;
        }

        if (_chainDocument is not null)
            Chain.Reload();
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

    /// <summary>Перед экспортом библиотеки: предложить сохранить активную вкладку.</summary>
    /// <returns>false — экспорт отменён.</returns>
    private bool ConfirmSaveActive() =>
        _active is not { } doc || !IsModified(doc) || AskSave(doc, "Экспорт");

    private void Save_Click(object sender, RoutedEventArgs e) => SaveScript();

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.Saved is { } saved)
            ShowProperties(saved.Id);
    }
}
