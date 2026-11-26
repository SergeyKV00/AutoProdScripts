using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Workspace;

namespace AutoProdScripts.Views.Queries;

/// <summary>
/// Открытые вкладки: полоса над редактором, список «Открытые» в Explorer и восстановление после перезапуска.
/// </summary>
public partial class QueriesView
{
    private const string NewScriptName = "Новый скрипт";

    private readonly List<QueryDocument> _documents = new();
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private QueryDocument? _active;

    /// <summary>Вкладка цепочки — не больше одной: редактор цепочки в разделе один.</summary>
    private QueryDocument? _chainDocument;

    /// <summary>Идёт переключение вкладки: изменения элементов не относятся к пользователю.</summary>
    private bool _switching;

    private bool _restoring;
    private bool _sessionWarned;

    private void InitializeSession() =>
        _sessionTimer.Tick += (_, _) => SaveSession();

    private bool InChain => _active?.IsChain == true;

    private void ShowArea(bool chain)
    {
        Chain.Visibility = chain ? Visibility.Visible : Visibility.Collapsed;
        ScriptArea.Visibility = chain ? Visibility.Collapsed : Visibility.Visible;
        ScriptToolbar.Visibility = ScriptArea.Visibility;
    }

    private string TitleOf(QueryDocument doc)
    {
        if (doc.IsChain)
            return Chain.DisplayName;
        return doc.Saved?.Name ?? (doc.DraftName.Length > 0 ? doc.DraftName : NewScriptName);
    }

    private bool IsModified(QueryDocument doc) => doc.IsChain ? Chain.IsModified : doc.IsModified;

    /// <summary>Подпись под названием в «Открытых»: куда смотрит вкладка.</summary>
    private static string HintOf(QueryDocument doc)
    {
        if (doc.IsChain)
            return "цепочка";
        var target = doc.Regional
            ? RegionTargets.Title + (doc.RegionDatabase.Length > 0 ? " · " + doc.RegionDatabase : string.Empty)
            : App.Connections.GetAll().FirstOrDefault(p => p.Id == doc.ConnectionId)?.Name is { } name
                ? name + (doc.Database.Length > 0 ? " · " + doc.Database : string.Empty)
                : "без подключения";
        return doc.Saved is null ? "не в библиотеке · " + target : target;
    }

    // ---------- создание и открытие ----------

    private QueryDocument AddDocument(QueryDocument doc, int? index = null)
    {
        _documents.Insert(index ?? _documents.Count, doc);
        CreateChrome(doc);
        UpdateOpenEditorsTitle();
        return doc;
    }

    /// <summary>Подключение новой вкладки — как у активной: так было до появления вкладок.</summary>
    private void InheritConnection(QueryDocument doc)
    {
        var from = _active is { IsChain: false } ? _active : _documents.LastOrDefault(d => !d.IsChain && d != doc);
        if (from is null)
            return;
        doc.ConnectionId = from.ConnectionId;
        doc.Regional = from.Regional;
        doc.RegionDatabase = from.RegionDatabase;
        doc.RegionCodes = from.RegionCodes.ToList();
        doc.Database = from.Database;
    }

    private QueryDocument CreateDraft(string sql, string name)
    {
        var doc = new QueryDocument { DraftText = sql, DraftName = name.Length > 0 ? name : UniqueDraftName() };
        SetText(doc, sql);
        InheritConnection(doc);
        return AddDocument(doc, _active is null ? null : _documents.IndexOf(_active) + 1);
    }

    private string UniqueDraftName()
    {
        var name = NewScriptName;
        for (var i = 2; _documents.Any(d => !d.IsChain && string.Equals(TitleOf(d), name, StringComparison.CurrentCultureIgnoreCase)); i++)
            name = $"{NewScriptName} {i}";
        return name;
    }

    private static void SetText(QueryDocument doc, string sql)
    {
        doc.Document.Text = sql;
        // Иначе Ctrl+Z вернул бы прежний текст.
        doc.Document.UndoStack.ClearAll();
        doc.CaretOffset = 0;
    }

    private static void UseScriptConnection(QueryDocument doc, LibraryScript script)
    {
        doc.Database = string.Empty;
        if (script.RegionDatabase.Length > 0)
        {
            doc.Regional = true;
            doc.RegionDatabase = script.RegionDatabase;
            return;
        }

        // Если подключения нет, не подставляем чужое: запрос ушёл бы не в ту базу.
        doc.Regional = false;
        doc.ConnectionId = ScriptConnection.Resolve(script, App.Connections.GetAll())?.Id;
    }

    /// <returns>false — пользователь остался в цепочке.</returns>
    private bool NewScript()
    {
        ActivateDocument(CreateDraft(string.Empty, string.Empty));
        return true;
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewScript();

    /// <summary>Новая пустая вкладка (Ctrl+N).</summary>
    public void NewTab() => NewScript();

    private void OpenItem(Guid id)
    {
        if (App.Library.FindChain(id) is { } chain)
            OpenChain(chain);
        else
            OpenScript(id);
    }

    /// <summary>Уже открытый скрипт не открывается второй раз — активируется его вкладка.</summary>
    private void OpenScript(Guid id)
    {
        if (_documents.FirstOrDefault(d => d.ScriptId == id) is { } open)
        {
            ActivateDocument(open);
            return;
        }

        if (App.Library.Find(id) is not { } script)
        {
            SelectInLibrary();
            return;
        }

        var doc = new QueryDocument { Saved = script };
        SetText(doc, script.Sql);
        UseScriptConnection(doc, script);
        AddDocument(doc, _active is null ? null : _documents.IndexOf(_active) + 1);
        ActivateDocument(doc);
        if (!doc.Regional && doc.ConnectionId is null && script.ConnectionName.Length > 0)
            doc.Results.Summary = $"Подключения «{script.ConnectionName}» на этом ПК нет. Выберите другое.";
    }

    private void OpenChain(LibraryChain chain)
    {
        if (_chainDocument is { } open && (Chain.ChainId == chain.Id || Chain.IsBusy))
        {
            // Выполняющуюся цепочку не подменяем.
            ActivateDocument(open);
            return;
        }

        if (!ReplaceChain())
            return;
        Chain.Open(chain);
        ActivateDocument(_chainDocument ??= AddDocument(new QueryDocument(chain: true)));
        RefreshChrome();
    }

    private void NewChain()
    {
        if (_chainDocument is not null && Chain.IsBusy)
        {
            ActivateDocument(_chainDocument);
            return;
        }

        if (!ReplaceChain())
            return;
        Chain.Open(new LibraryChain());
        ActivateDocument(_chainDocument ??= AddDocument(new QueryDocument(chain: true)));
        RefreshChrome();
    }

    /// <returns>false — пользователь решил остаться в открытой цепочке.</returns>
    private bool ReplaceChain()
    {
        if (_chainDocument is null || !Chain.IsModified)
            return true;
        ActivateDocument(_chainDocument);
        return AskSave(_chainDocument, "Открыть другую цепочку");
    }

    // ---------- переключение ----------

    private void ActivateDocument(QueryDocument doc)
    {
        if (_active is { IsChain: false } previous && previous != doc)
            previous.CaretOffset = Editor.CaretOffset;
        _active = doc;
        ShowArea(doc.IsChain);

        if (!doc.IsChain)
        {
            _switching = true;
            try
            {
                Editor.Document = doc.Document;
                Editor.CaretOffset = Math.Min(doc.CaretOffset, doc.Document.TextLength);
                ResultsHost.Content = doc.Results;
            }
            finally
            {
                _switching = false;
            }

            ShowConnection(doc);
            ShowBusy(doc.IsRunning);
            CheckScript();
        }

        SelectInLibrary();
        // Дерево подключений показывает контекст вкладки, но саму вкладку не меняет.
        Explorer.SetActiveConnection(doc.IsChain || doc.Regional ? null : doc.ConnectionId, doc.IsChain ? null : EffectiveDatabase(doc));
        RefreshChrome();
        ScheduleSessionSave();
        if (!doc.IsChain && IsLoaded)
            Dispatcher.BeginInvoke(() => Editor.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Выделение в библиотеке показывает то, что открыто в активной вкладке.</summary>
    private void SelectInLibrary() =>
        Library.Select(_active is null ? null : _active.IsChain ? Chain.ChainId : _active.ScriptId);

    /// <summary>Следующая/предыдущая вкладка (Ctrl+Tab / Ctrl+Shift+Tab).</summary>
    public void CycleTab(bool back)
    {
        if (_active is null || _documents.Count < 2)
            return;
        var index = _documents.IndexOf(_active) + (back ? -1 : 1);
        ActivateDocument(_documents[(index + _documents.Count) % _documents.Count]);
    }

    // ---------- закрытие ----------

    /// <summary>Закрыть активную вкладку (Ctrl+W).</summary>
    public void CloseActiveTab()
    {
        if (_active is not null)
            CloseDocument(_active);
    }

    /// <returns>false — вкладка осталась открытой.</returns>
    private bool CloseDocument(QueryDocument doc)
    {
        if (doc.IsChain && Chain.IsBusy)
        {
            ActivateDocument(doc);
            MessageBox.Show(Window.GetWindow(this), "Цепочка выполняется. Сначала остановите её.", "Запросы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (doc.IsRunning)
        {
            ActivateDocument(doc);
            var stop = MessageBox.Show(Window.GetWindow(this), $"В «{TitleOf(doc)}» выполняется запрос. Отменить его и закрыть вкладку?",
                "Запросы", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (stop != MessageBoxResult.Yes)
                return false;
            doc.Running?.Cancel();
        }

        if (IsModified(doc))
        {
            ActivateDocument(doc);
            if (!AskSave(doc, "Закрыть вкладку"))
                return false;
        }

        var index = _documents.IndexOf(doc);
        _documents.Remove(doc);
        TabStrip.Children.Remove(doc.Tab);
        OpenList.Children.Remove(doc.ExplorerRow);
        UpdateOpenEditorsTitle();
        foreach (var row in doc.Rows.Values)
            ParametersHost.Children.Remove(row.Element);
        if (doc == _chainDocument)
            _chainDocument = null;

        if (_active == doc)
        {
            // Пустым раздел не оставляем: всегда есть куда писать. Подключение берём у закрытой вкладки.
            var next = _documents.Count == 0
                ? CreateDraft(string.Empty, string.Empty)
                : _documents[Math.Clamp(index - 1, 0, _documents.Count - 1)];
            _active = null;
            ActivateDocument(next);
        }
        else
        {
            RefreshChrome();
            ScheduleSessionSave();
        }

        return true;
    }

    private void CloseDocuments(IEnumerable<QueryDocument> docs)
    {
        foreach (var doc in docs.ToList())
        {
            if (_documents.Contains(doc) && !CloseDocument(doc))
                return;
        }
    }

    /// <summary>Сохранить изменения перед действием. Активной должна быть эта вкладка.</summary>
    /// <returns>false — действие отменено.</returns>
    private bool AskSave(QueryDocument doc, string action)
    {
        var what = doc.IsChain ? "цепочки" : "скрипта";
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"{action}: сохранить изменения {what} «{TitleOf(doc)}» в библиотеке?", "Запросы",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveScript(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    // ---------- вкладки и строки «Открытых» ----------

    private void CreateChrome(QueryDocument doc)
    {
        // Вкладка над редактором.
        var tabDirty = Mark("●", 9, "FgInfoBrush", new Thickness(0, 0, 6, 0));
        var tabTitle = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var tabRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tabRow.Children.Add(tabDirty);
        tabRow.Children.Add(tabTitle);
        tabRow.Children.Add(CloseMark(doc));
        var tab = new Border
        {
            Child = tabRow,
            Height = 32,
            Padding = new Thickness(10, 0, 6, 0),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 2),
            SnapsToDevicePixels = true,
            ContextMenu = TabMenu(doc)
        };
        Clickable(tab, doc);
        TabStrip.Children.Insert(_documents.IndexOf(doc), tab);

        // Строка в «Открытых».
        var rowDirty = Mark("●", 9, "FgInfoBrush", new Thickness(0, 0, 6, 0));
        var rowTitle = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var rowHint = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
        rowHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        var titleLine = new DockPanel();
        DockPanel.SetDock(rowDirty, Dock.Left);
        titleLine.Children.Add(rowDirty);
        titleLine.Children.Add(rowTitle);
        var text = new StackPanel();
        text.Children.Add(titleLine);
        text.Children.Add(rowHint);
        var close = CloseMark(doc);
        DockPanel.SetDock(close, Dock.Right);
        var line = new DockPanel();
        line.Children.Add(close);
        line.Children.Add(text);
        var row = new Border
        {
            Child = line,
            Padding = new Thickness(6, 3, 4, 3),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ContextMenu = TabMenu(doc)
        };
        Clickable(row, doc);
        OpenList.Children.Insert(_documents.IndexOf(doc), row);

        doc.Tab = tab;
        doc.TabTitle = tabTitle;
        doc.TabDirty = tabDirty;
        doc.ExplorerRow = row;
        doc.ExplorerTitle = rowTitle;
        doc.ExplorerDirty = rowDirty;
        doc.ExplorerHint = rowHint;
    }

    private void Clickable(Border element, QueryDocument doc)
    {
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_documents.Contains(doc) && doc != _active)
                ActivateDocument(doc);
        };
        element.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle)
                return;
            e.Handled = true;
            CloseDocument(doc);
        };
        element.MouseEnter += (_, _) => RefreshChrome(doc);
        element.MouseLeave += (_, _) => RefreshChrome(doc);
    }

    private static TextBlock Mark(string text, double size, string brush, Thickness margin)
    {
        var mark = new TextBlock { Text = text, FontSize = size, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return mark;
    }

    private TextBlock CloseMark(QueryDocument doc)
    {
        var close = Mark("×", 14, "FgMutedBrush", new Thickness(8, 0, 2, 0));
        close.Cursor = Cursors.Hand;
        close.ToolTip = "Закрыть (Ctrl+W)";
        close.MouseEnter += (_, _) => close.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        close.MouseLeave += (_, _) => close.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        // Закрываем по отпусканию, иначе отпускание достанется вкладке под крестиком.
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseDocument(doc);
        };
        return close;
    }

    private ContextMenu TabMenu(QueryDocument doc)
    {
        var close = new MenuItem { Header = "Закрыть", InputGestureText = "Ctrl+W" };
        var closeOthers = new MenuItem { Header = "Закрыть остальные" };
        var closeSaved = new MenuItem { Header = "Закрыть без изменений" };
        var closeAll = new MenuItem { Header = "Закрыть все" };
        var showInLibrary = new MenuItem { Header = "Показать в библиотеке" };
        close.Click += (_, _) => CloseDocument(doc);
        closeOthers.Click += (_, _) => CloseDocuments(_documents.Where(d => d != doc));
        closeSaved.Click += (_, _) => CloseDocuments(_documents.Where(d => !IsModified(d) && !d.IsRunning && !(d.IsChain && Chain.IsBusy)));
        closeAll.Click += (_, _) => CloseDocuments(_documents);
        showInLibrary.Click += (_, _) => ActivateDocument(doc);

        var menu = new ContextMenu();
        menu.Items.Add(close);
        menu.Items.Add(closeOthers);
        menu.Items.Add(closeSaved);
        menu.Items.Add(closeAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(showInLibrary);
        menu.Opened += (_, _) =>
        {
            closeOthers.IsEnabled = _documents.Count > 1;
            showInLibrary.IsEnabled = doc.IsChain ? App.Library.FindChain(Chain.ChainId) is not null : doc.Saved is not null;
        };
        return menu;
    }

    private void RefreshChrome()
    {
        foreach (var doc in _documents)
            RefreshChrome(doc);
    }

    private void RefreshChrome(QueryDocument doc)
    {
        if (doc.Tab is null)
            return;

        var title = TitleOf(doc);
        var modified = IsModified(doc);
        var running = doc.IsRunning || doc.IsChain && Chain.IsBusy;
        var active = doc == _active;
        var label = (running ? "⏳ " : string.Empty) + (doc.IsChain ? "🔗 " : string.Empty) + title;
        var tip = (doc.IsChain ? "Цепочка" : doc.Saved is null ? "Не сохранён в библиотеке" : "Библиотека"
                      + (doc.Saved.Group.Length > 0 ? " / " + doc.Saved.Group : string.Empty))
                  + (modified ? " · есть несохранённые изменения" : string.Empty)
                  + (running ? " · выполняется" : string.Empty);

        doc.TabTitle!.Text = label;
        doc.TabTitle.FontStyle = doc.Saved is null && !doc.IsChain ? FontStyles.Italic : FontStyles.Normal;
        doc.TabTitle.SetResourceReference(TextBlock.ForegroundProperty, active ? "FgBrush" : "FgMutedBrush");
        doc.TabDirty!.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
        doc.Tab.ToolTip = tip;
        doc.Tab.SetResourceReference(Border.BackgroundProperty,
            active ? "BgBrush" : doc.Tab.IsMouseOver ? "HoverBrush" : "PanelAltBrush");
        doc.Tab.SetResourceReference(Border.BorderBrushProperty, active ? "StatusBrush" : "BorderBrush");

        doc.ExplorerTitle!.Text = label;
        doc.ExplorerTitle.FontStyle = doc.TabTitle.FontStyle;
        doc.ExplorerTitle.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        doc.ExplorerTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        doc.ExplorerDirty!.Visibility = doc.TabDirty.Visibility;
        doc.ExplorerHint!.Text = HintOf(doc);
        doc.ExplorerRow!.ToolTip = tip;
        doc.ExplorerRow.SetResourceReference(Border.BackgroundProperty,
            active ? "SelectionBrush" : doc.ExplorerRow.IsMouseOver ? "HoverBrush" : "PanelBrush");
        doc.ExplorerRow.SetResourceReference(Border.BorderBrushProperty, active ? "AccentBrush" : "PanelBrush");
        if (active)
        {
            doc.ExplorerTitle.SetResourceReference(TextBlock.ForegroundProperty, "OnSelectionBrush");
            doc.ExplorerHint.SetResourceReference(TextBlock.ForegroundProperty, "OnSelectionBrush");
        }
        else
        {
            doc.ExplorerHint.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        }
    }

    // ---------- восстановление после перезапуска ----------

    private void RestoreSession()
    {
        _restoring = true;
        var state = App.OpenTabs.Load();
        var connections = App.Connections.GetAll();
        QueryDocument? active = null;
        try
        {
            for (var i = 0; i < state.Tabs.Count; i++)
            {
                var tab = state.Tabs[i];
                var doc = RestoreTab(tab, connections);
                if (doc is not null && (i == state.Active || active is null))
                    active = doc;
            }
        }
        finally
        {
            _restoring = false;
        }

        ActivateDocument(active ?? CreateDraft(string.Empty, string.Empty));
    }

    private QueryDocument? RestoreTab(OpenTabState tab, IReadOnlyList<DbConnectionProfile> connections)
    {
        if (tab.ChainId is { } chainId)
        {
            if (_chainDocument is not null || App.Library.FindChain(chainId) is not { } chain)
                return null;
            Chain.Open(chain);
            return _chainDocument = AddDocument(new QueryDocument(chain: true));
        }

        var doc = new QueryDocument();
        if (tab.ScriptId is { } id && App.Library.Find(id) is { } script)
        {
            if (_documents.Any(d => d.ScriptId == id))
                return null;
            doc.Saved = script;
        }
        else if (tab.Text is null)
        {
            // Скрипт удалён из библиотеки, а своего текста у вкладки не было.
            return null;
        }
        else
        {
            doc.DraftName = tab.Name;
        }

        SetText(doc, tab.Text ?? doc.Saved!.Sql);
        doc.Regional = tab.Regional;
        doc.RegionDatabase = tab.RegionDatabase;
        doc.RegionCodes = tab.Regions.ToList();
        doc.ConnectionId = ScriptConnection.Resolve(tab.ConnectionId, tab.ConnectionName, connections)?.Id;
        doc.Database = doc.ConnectionId is null ? string.Empty : tab.Database;
        return AddDocument(doc);
    }

    private OpenTabsState Snapshot()
    {
        var connections = App.Connections.GetAll();
        var tabs = new List<OpenTabState>();
        var active = 0;
        foreach (var doc in _documents)
        {
            if (doc == _active)
                active = tabs.Count;
            if (doc.IsChain)
            {
                // Несохранённую цепочку восстановить нечем: её правки живут только в редакторе.
                if (App.Library.FindChain(Chain.ChainId) is not null)
                    tabs.Add(new OpenTabState { ChainId = Chain.ChainId });
                continue;
            }

            var text = doc.Document.Text;
            tabs.Add(new OpenTabState
            {
                ScriptId = doc.ScriptId,
                Name = doc.Saved is null ? TitleOf(doc) : string.Empty,
                // Текст храним только если он отличается от библиотеки: иначе берём из библиотеки.
                Text = doc.Saved is not null && text == doc.Saved.Sql ? null : text,
                ConnectionId = doc.ConnectionId,
                ConnectionName = connections.FirstOrDefault(p => p.Id == doc.ConnectionId)?.Name ?? string.Empty,
                Database = doc.Database,
                Regional = doc.Regional,
                RegionDatabase = doc.RegionDatabase,
                Regions = doc.RegionCodes.ToList()
            });
        }

        return new OpenTabsState { Tabs = tabs, Active = active };
    }

    private void ScheduleSessionSave()
    {
        if (_restoring || !_initialized)
            return;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    /// <summary>Пишет открытые вкладки на диск. Значения параметров и результаты не сохраняются.</summary>
    public void SaveSession()
    {
        _sessionTimer.Stop();
        if (!_initialized || _restoring)
            return;
        if (_active is { IsChain: false } active)
            active.CaretOffset = Editor.CaretOffset;
        try
        {
            App.OpenTabs.Save(Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_sessionWarned)
                App.Log.Warn("Не удалось сохранить список открытых вкладок.");
            _sessionWarned = true;
        }
    }

    /// <summary>
    /// Перед закрытием окна. Тексты вкладок восстановятся при следующем запуске, поэтому спрашиваем
    /// только про цепочку и выполняющиеся запросы.
    /// </summary>
    /// <returns>false — окно не закрываем.</returns>
    public bool ConfirmClose()
    {
        if (!_initialized)
            return true;

        if (_chainDocument is not null && Chain.IsModified)
        {
            ActivateDocument(_chainDocument);
            if (!AskSave(_chainDocument, "Выход"))
                return false;
        }

        var running = _documents.Count(d => d.IsRunning) + (Chain.IsBusy ? 1 : 0);
        if (running > 0)
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                $"Выполняется запросов: {running}. Отменить их и выйти?", "Запросы",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return false;
        }

        SaveSession();
        SaveExplorerState();
        return true;
    }
}
