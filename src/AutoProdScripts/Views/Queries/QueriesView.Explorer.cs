using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Explorer;
using AutoProdScripts.Core.Workspace;

namespace AutoProdScripts.Views.Queries;

/// <summary>Левая панель: открытые редакторы, навигатор подключений и библиотека; база вкладки.</summary>
public partial class QueriesView
{
    private readonly DispatcherTimer _explorerTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<Guid, IReadOnlyList<string>> _databaseLists = new();
    private ExplorerState _explorerState = new();

    private void InitializeExplorer()
    {
        _explorerState = App.ExplorerStates.Load();
        _explorerTimer.Tick += (_, _) => SaveExplorerState();
        Explorer.StateChanged += () =>
        {
            _explorerTimer.Stop();
            _explorerTimer.Start();
        };
        Explorer.OpenEditorRequested += OpenEditorWith;
        Explorer.OpenSqlRequested += request => _ = OpenSqlAsync(request);
        Explorer.Initialize(_explorerState);

        ShowExplorerView(_explorerState.View);
        ShowOpenEditors(!_explorerState.OpenEditorsCollapsed);

        App.Connections.Changed += () => _databaseLists.Clear();
        Editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                _ = ShowInExplorerAsync();
            }
        };
        Editor.ContextMenu = EditorMenu();
    }

    private void SaveExplorerState()
    {
        _explorerTimer.Stop();
        if (!_initialized)
            return;
        Explorer.ExportState(_explorerState);
        _explorerState.View = LibraryViewButton.IsChecked == true ? ExplorerView.Library : ExplorerView.Connections;
        _explorerState.OpenEditorsCollapsed = OpenEditorsList.Visibility != Visibility.Visible;
        try
        {
            App.ExplorerStates.Save(_explorerState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log.Warn("Не удалось сохранить состояние Explorer.");
        }
    }

    private void ScheduleExplorerSave()
    {
        _explorerTimer.Stop();
        _explorerTimer.Start();
    }

    // ---------- заголовок панели ----------

    private void NewConnection_Click(object sender, RoutedEventArgs e) => Explorer.NewConnection(null);

    private void RefreshExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryViewButton.IsChecked == true)
            Library.Initialize();
        else
            Explorer.RefreshAll();
    }

    private void CollapseExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryViewButton.IsChecked == true)
            Library.CollapseAll();
        else
            Explorer.CollapseAll();
    }

    private void ExplorerSettings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ExplorerSettingsButton };
        var system = new MenuItem { Header = "Скрывать системные объекты", IsCheckable = true, IsChecked = Explorer.HideSystemObjects };
        system.Click += (_, _) => Explorer.HideSystemObjects = system.IsChecked;
        menu.Items.Add(system);

        var kinds = new MenuItem { Header = "Показывать объекты" };
        foreach (var folder in SqlServerMetadata.Instance.DatabaseFolders)
        {
            var item = new MenuItem { Header = folder.Title, IsCheckable = true, IsChecked = !Explorer.IsKindHidden(folder.Kind), StaysOpenOnClick = true };
            var kind = folder.Kind;
            item.Click += (_, _) => Explorer.SetKindHidden(kind, !item.IsChecked);
            kinds.Items.Add(item);
        }

        menu.Items.Add(kinds);
        menu.Items.Add(new Separator());
        var connections = new MenuItem { Header = "Подключения и регионы…" };
        connections.Click += (_, _) => ShowConnections();
        menu.Items.Add(connections);
        menu.IsOpen = true;
    }

    private void ExplorerView_Click(object sender, RoutedEventArgs e)
    {
        ShowExplorerView(LibraryViewButton.IsChecked == true ? ExplorerView.Library : ExplorerView.Connections);
        ScheduleExplorerSave();
    }

    private void ShowExplorerView(ExplorerView view)
    {
        var library = view == ExplorerView.Library;
        LibraryViewButton.IsChecked = library;
        ConnectionsViewButton.IsChecked = !library;
        Library.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        Explorer.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenEditorsToggle_Click(object sender, RoutedEventArgs e)
    {
        ShowOpenEditors(OpenEditorsList.Visibility != Visibility.Visible);
        ScheduleExplorerSave();
    }

    private void ShowOpenEditors(bool show)
    {
        OpenEditorsList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateOpenEditorsTitle();
    }

    private void UpdateOpenEditorsTitle() =>
        OpenEditorsTitle.Text = (OpenEditorsList.Visibility == Visibility.Visible ? "▾ " : "▸ ")
                                + $"ОТКРЫТЫЕ РЕДАКТОРЫ ({_documents.Count})";

    // ---------- открытие из дерева ----------

    /// <summary>Новая вкладка с подключением из дерева. Другие вкладки не меняются.</summary>
    private void OpenEditorWith(Guid connectionId, string? database)
    {
        var doc = CreateDraft(string.Empty, string.Empty);
        SetConnection(doc, connectionId, database);
        ActivateDocument(doc);
    }

    private static void SetConnection(QueryDocument doc, Guid connectionId, string? database)
    {
        doc.Regional = false;
        doc.ConnectionId = connectionId;
        var profile = App.Connections.GetAll().FirstOrDefault(p => p.Id == connectionId);
        // База подключения задаётся им самим; во вкладке запоминаем только другую.
        doc.Database = database is null || string.Equals(database, profile?.Database, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : database;
    }

    private async Task OpenSqlAsync(OpenSqlRequest request)
    {
        var doc = CreateDraft(request.Sql, request.Title);
        SetConnection(doc, request.ConnectionId, request.Database);
        ActivateDocument(doc);
        if (!request.Run)
            return;

        await RunAsync();
        if (request.ExportAfterRun && _documents.Contains(doc))
            doc.Results.ExportCsv();
    }

    // ---------- база вкладки ----------

    /// <summary>База, в которой выполняется вкладка: выбранная во вкладке или база подключения.</summary>
    private static string? EffectiveDatabase(QueryDocument doc)
    {
        if (doc.Regional)
            return null;
        if (doc.Database.Length > 0)
            return doc.Database;
        var profile = App.Connections.GetAll().FirstOrDefault(p => p.Id == doc.ConnectionId);
        return profile is { IsClusterLevel: false } ? profile.Database : null;
    }

    private void UpdateDatabaseControls(QueryDocument doc)
    {
        // Выбор базы — для подключения к кластеру (или если база вкладки уже задана из дерева).
        var show = !doc.Regional && SelectedConnection is { } profile && (profile.IsClusterLevel || doc.Database.Length > 0);
        DatabaseControls.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _switching = true;
        try
        {
            DatabaseBox.Text = doc.Database;
        }
        finally
        {
            _switching = false;
        }
    }

    private async void DatabaseBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (SelectedConnection is not { } profile)
            return;
        if (!_databaseLists.TryGetValue(profile.Id, out var names))
        {
            DatabaseBox.ItemsSource = new[] { "Загрузка…" };
            try
            {
                var connectionString = DbExplorerPanel.ConnectionString(profile.Id, null);
                var databases = await Task.Run(() =>
                    DbMetadata.For(profile.Provider).GetDatabasesAsync(connectionString, default));
                names = databases.Where(d => d.IsAvailable && !d.IsSystem).Select(d => d.Name).ToList();
                _databaseLists[profile.Id] = names;
            }
            catch (Exception ex)
            {
                DatabaseBox.ItemsSource = null;
                if (_active is { IsChain: false } doc)
                    doc.Results.Summary = "Не удалось получить список баз: " + ex.Message;
                return;
            }
        }

        var text = DatabaseBox.Text;
        _switching = true;
        DatabaseBox.ItemsSource = names;
        DatabaseBox.Text = text;
        _switching = false;
    }

    private void DatabaseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_switching && DatabaseBox.SelectedItem is string name && name != "Загрузка…")
            CommitDatabase(name);
    }

    private void DatabaseBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_switching && !DatabaseBox.IsKeyboardFocusWithin)
            CommitDatabase(DatabaseBox.Text);
    }

    private void CommitDatabase(string name)
    {
        if (_active is not { IsChain: false } doc)
            return;
        name = name.Trim();
        if (name == doc.Database)
            return;
        doc.Database = name;
        UpdateBadge();
        CheckScript();
        RefreshChrome(doc);
        Explorer.SetActiveConnection(doc.ConnectionId, EffectiveDatabase(doc));
        ScheduleSessionSave();
    }

    // ---------- переход к объекту в дереве ----------

    private ContextMenu EditorMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Вырезать", Command = ApplicationCommands.Cut });
        menu.Items.Add(new MenuItem { Header = "Копировать", Command = ApplicationCommands.Copy });
        menu.Items.Add(new MenuItem { Header = "Вставить", Command = ApplicationCommands.Paste });
        menu.Items.Add(new Separator());
        var reveal = new MenuItem { Header = "Показать в Explorer", InputGestureText = "F12" };
        reveal.Click += (_, _) => _ = ShowInExplorerAsync();
        menu.Items.Add(reveal);
        return menu;
    }

    /// <summary>Имя объекта под курсором (или выделение): «dbo.Users», «[Db].[dbo].[Users]».</summary>
    private string NameAtCaret()
    {
        if (Editor.SelectionLength > 0)
            return Editor.SelectedText.Trim();
        var document = Editor.Document;
        var line = document.GetLineByOffset(Editor.CaretOffset);
        var text = document.GetText(line.Offset, line.Length);
        var caret = Editor.CaretOffset - line.Offset;
        static bool Part(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$' or '.' or '[' or ']' or '"' or ' ';

        // Пробел допустим только внутри [...]: сначала берём грубо, потом обрезаем по скобкам.
        var start = caret;
        while (start > 0 && Part(text[start - 1]))
            start--;
        var end = caret;
        while (end < text.Length && Part(text[end]))
            end++;
        var candidate = text[start..end];
        var relative = caret - start;

        // Разбиваем по пробелам вне скобок и берём кусок, в котором курсор.
        var pieces = new List<(int Start, string Text)>();
        var depth = 0;
        var pieceStart = 0;
        for (var i = 0; i <= candidate.Length; i++)
        {
            if (i == candidate.Length || candidate[i] == ' ' && depth == 0)
            {
                pieces.Add((pieceStart, candidate[pieceStart..i]));
                pieceStart = i + 1;
                continue;
            }

            if (candidate[i] == '[')
                depth++;
            else if (candidate[i] == ']' && depth > 0)
                depth--;
        }

        return pieces.LastOrDefault(p => p.Start <= relative).Text?.Trim() ?? string.Empty;
    }

    private async Task ShowInExplorerAsync()
    {
        if (_active is not { IsChain: false } doc)
            return;
        var results = doc.Results;
        if (doc.Regional)
        {
            results.Summary = "У вкладки «По регионам» нет одного подключения для перехода в дерево.";
            return;
        }

        if (App.Connections.GetAll().FirstOrDefault(p => p.Id == doc.ConnectionId) is not { } profile)
        {
            results.Summary = "Выберите подключение вкладки.";
            return;
        }

        var parts = SqlGenerator.ParseName(NameAtCaret());
        if (parts is null)
        {
            results.Summary = "Поставьте курсор на имя таблицы, представления или процедуры.";
            return;
        }

        var name = parts[^1];
        var schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null;
        var database = parts.Count == 3 && parts[0].Length > 0 ? parts[0] : EffectiveDatabase(doc);
        if (string.IsNullOrEmpty(database))
        {
            results.Summary = "Выберите базу вкладки или укажите её в имени: База.dbo.Таблица.";
            return;
        }

        try
        {
            var connectionString = DbExplorerPanel.ConnectionString(profile.Id, database);
            var found = await Task.Run(() =>
                DbMetadata.For(profile.Provider).FindObjectAsync(connectionString, schema, name, default));
            if (found is null)
            {
                results.Summary = $"Объект «{string.Join('.', parts)}» не найден в базе {database}.";
                return;
            }

            ShowExplorerView(ExplorerView.Connections);
            ScheduleExplorerSave();
            if (!await Explorer.RevealAsync(profile.Id, database, found))
                results.Summary = $"«{found.DisplayName}» есть в базе, но скрыт в дереве (фильтр, системные объекты или вложенный объект).";
        }
        catch (Exception ex)
        {
            results.Summary = "Не удалось найти объект: " + ex.Message;
        }
    }
}
