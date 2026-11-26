using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Explorer;
using AutoProdScripts.Core.Workspace;

namespace AutoProdScripts.Views.Queries;

/// <summary>Состояние подключения в дереве.</summary>
internal enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

internal enum NodeType
{
    Group,
    Connection,
    Databases,
    Database,
    Security,
    Folder,
    Object,
    ChildFolder,
    Child,
    Message
}

/// <summary>Узел дерева. Ключ стабилен между запусками — по нему восстанавливается раскрытие.</summary>
internal sealed class ExplorerNode
{
    public required NodeType Type { get; init; }
    public required string Key { get; init; }
    public Guid ConnectionId { get; init; }
    public string? Group { get; init; }
    public string? Database { get; init; }
    public DbFolder? Folder { get; init; }
    public DbObjectInfo? Item { get; init; }

    /// <summary>Таблица или представление — для вложенных папок и их элементов.</summary>
    public DbObjectInfo? Parent { get; init; }

    public string Text { get; init; } = string.Empty;
    public bool Loaded { get; set; }
    public TreeViewItem View { get; set; } = null!;
    public TextBlock? Title { get; set; }
    public TextBlock? Status { get; set; }
    public DbObjectPermissions? Permissions { get; set; }
}

/// <summary>Запрос открыть SQL в новой вкладке с подключением из дерева.</summary>
internal sealed record OpenSqlRequest(Guid ConnectionId, string? Database, string Title, string Sql, bool Run = false, bool ExportAfterRun = false);

/// <summary>
/// Навигатор подключений: группы, подключения и объекты БД. Структура грузится лениво — по раскрытию узла.
/// Выбор в дереве не меняет подключения уже открытых вкладок.
/// </summary>
public partial class DbExplorerPanel : UserControl
{
    private readonly Dictionary<string, TreeViewItem> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<DbObjectInfo>> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _loading = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (ConnectionState State, string? Message)> _states = new();
    private readonly HashSet<DbObjectKind> _hiddenKinds = new();
    private bool _hideSystem = true;
    private bool _restoringExpansion;
    private Guid? _activeConnection;
    private string? _activeDatabase;
    private Point? _dragStart;
    private ExplorerNode? _dragNode;

    public DbExplorerPanel()
    {
        InitializeComponent();
    }

    /// <summary>Раскрытие, фильтры или подключения изменились — состояние стоит сохранить.</summary>
    public event Action? StateChanged;

    internal event Action<Guid, string?>? OpenEditorRequested;
    internal event Action<OpenSqlRequest>? OpenSqlRequested;

    public bool HideSystemObjects
    {
        get => _hideSystem;
        set
        {
            if (_hideSystem == value)
                return;
            _hideSystem = value;
            Rebuild();
            StateChanged?.Invoke();
        }
    }

    public bool IsKindHidden(DbObjectKind kind) => _hiddenKinds.Contains(kind);

    public void SetKindHidden(DbObjectKind kind, bool hidden)
    {
        if (hidden ? !_hiddenKinds.Add(kind) : !_hiddenKinds.Remove(kind))
            return;
        Rebuild();
        StateChanged?.Invoke();
    }

    public void Initialize(ExplorerState state)
    {
        _hideSystem = state.HideSystemObjects;
        _hiddenKinds.UnionWith(state.HiddenKinds);
        _expanded.UnionWith(state.Expanded);
        App.Connections.Changed += Rebuild;
        Rebuild();

        // Подключения, открытые при выходе, подключаем снова (без ожидания: сервер может быть недоступен).
        var known = App.Connections.GetAll().Select(p => p.Id).ToHashSet();
        foreach (var id in state.Connected.Where(known.Contains))
            _ = ConnectAsync(id, reconnect: false);
    }

    public void ExportState(ExplorerState state)
    {
        state.HideSystemObjects = _hideSystem;
        state.HiddenKinds = _hiddenKinds.ToList();
        state.Expanded = _expanded.ToList();
        state.Connected = _states.Where(s => s.Value.State == ConnectionState.Connected).Select(s => s.Key).ToList();
    }

    // ---------- построение дерева ----------

    private static string ConnectionKey(Guid id) => $"c:{id:N}";
    private static string DatabasesKey(Guid id) => ConnectionKey(id) + "/dbs";
    private static string DatabaseKey(Guid id, string database) => ConnectionKey(id) + "/db:" + database;
    private static string FolderKey(string databaseKey, DbObjectKind kind) =>
        databaseKey + (kind is DbObjectKind.User or DbObjectKind.Role ? "/sec" : string.Empty) + "/f:" + kind;

    private static DbConnectionProfile? Profile(Guid id) => App.Connections.GetAll().FirstOrDefault(p => p.Id == id);

    /// <summary>Полностью пересобрать дерево. Загруженные данные берутся из кэша, раскрытие сохраняется.</summary>
    public void Rebuild()
    {
        var selected = (Tree.SelectedItem as TreeViewItem)?.Tag is ExplorerNode node ? node.Key : null;
        _views.Clear();
        Tree.Items.Clear();
        var profiles = App.Connections.GetAll();
        foreach (var id in _states.Keys.Where(id => profiles.All(p => p.Id != id)).ToList())
            _states.Remove(id);

        var groups = new Dictionary<string, TreeViewItem>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var profile in profiles
                     .OrderBy(p => ConnectionStore.NormalizeGroup(p.Group).Length == 0 ? 1 : 0)
                     .ThenBy(p => ConnectionStore.NormalizeGroup(p.Group), StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var group = ConnectionStore.NormalizeGroup(profile.Group);
            var target = group.Length == 0 ? Tree.Items : GroupNode(group, groups).Items;
            target.Add(ConnectionNode(profile).View);
        }

        EmptyText.Text = profiles.Count == 0 ? "Подключений пока нет. Нажмите «＋», чтобы добавить." : string.Empty;
        if (selected is not null && _views.TryGetValue(selected, out var item))
            item.IsSelected = true;
        ApplySearch();
    }

    private TreeViewItem GroupNode(string path, Dictionary<string, TreeViewItem> groups)
    {
        if (groups.TryGetValue(path, out var existing))
            return existing;

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? Tree.Items : GroupNode(path[..slash], groups).Items;
        var node = new ExplorerNode { Type = NodeType.Group, Key = "g:" + path, Group = path, Text = path[(slash + 1)..], Loaded = true };
        var view = CreateView(node, "📁", node.Text, null, expandable: false);
        // Группы по умолчанию раскрыты; запоминаем только свёрнутые.
        _restoringExpansion = true;
        view.IsExpanded = !_expanded.Contains("collapsed:" + node.Key);
        _restoringExpansion = false;
        parent.Add(view);
        return groups[path] = view;
    }

    private ExplorerNode ConnectionNode(DbConnectionProfile profile)
    {
        var node = new ExplorerNode
        {
            Type = NodeType.Connection,
            Key = ConnectionKey(profile.Id),
            ConnectionId = profile.Id,
            Text = profile.Name
        };
        CreateView(node, "🗄", profile.Name, profile.IsClusterLevel ? profile.Server : $"{profile.Database} · {profile.Server}", expandable: true);
        node.View.ToolTip = $"{profile.Server} · {ConnectionUi.Target(profile)}";
        UpdateConnectionChrome(node);
        RestoreExpansion(node);
        return node;
    }

    private TreeViewItem CreateView(ExplorerNode node, string icon, string text, string? detail, bool expandable)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var iconText = new TextBlock { Text = icon + " ", VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(iconText);
        header.Children.Add(title);
        // Состояние — сразу за именем, чтобы длинная подпись его не обрезала.
        if (node.Type == NodeType.Connection)
        {
            var status = new TextBlock { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(status);
            node.Status = status;
        }

        if (!string.IsNullOrEmpty(detail))
        {
            var detailText = new TextBlock { Text = "  " + detail, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            detailText.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
            header.Children.Add(detailText);
        }

        var view = new TreeViewItem
        {
            Header = header,
            Tag = node
        };
        node.View = view;
        node.Title = title;
        if (node.Item is { IsSystem: true } || node.Item is { IsAvailable: false })
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        if (expandable && !node.Loaded)
            view.Items.Add(Placeholder("…"));

        view.Expanded += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, view))
                return;
            Remember(node, expanded: true);
            if (!node.Loaded)
                _ = LoadAsync(node);
        };
        view.Collapsed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, view))
                Remember(node, expanded: false);
        };
        _views[node.Key] = view;
        return view;
    }

    private void Remember(ExplorerNode node, bool expanded)
    {
        if (_restoringExpansion || _searching)
            return;
        if (node.Type == NodeType.Group)
        {
            if (expanded)
                _expanded.Remove("collapsed:" + node.Key);
            else
                _expanded.Add("collapsed:" + node.Key);
        }
        else if (expanded)
        {
            _expanded.Add(node.Key);
        }
        else
        {
            _expanded.Remove(node.Key);
        }

        StateChanged?.Invoke();
    }

    private void RestoreExpansion(ExplorerNode node)
    {
        if (!_expanded.Contains(node.Key))
            return;
        _restoringExpansion = true;
        try
        {
            node.View.IsExpanded = true;
        }
        finally
        {
            _restoringExpansion = false;
        }

        // Событие Expanded срабатывает и для ещё не показанного элемента, но загрузку надёжнее запустить явно.
        if (!node.Loaded && !_loading.ContainsKey(node.Key))
            _ = LoadAsync(node);
    }

    private static TreeViewItem Placeholder(string text, string brush = "FgMutedBrush")
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return new TreeViewItem
        {
            Header = block,
            Tag = new ExplorerNode { Type = NodeType.Message, Key = string.Empty, Loaded = true },
            Focusable = false
        };
    }

    // ---------- ленивая загрузка ----------

    private Task LoadAsync(ExplorerNode node)
    {
        if (_loading.TryGetValue(node.Key, out var running))
            return running;
        var task = LoadCoreAsync(node);
        // Загрузка без обращения к серверу завершается сразу — тогда и запоминать нечего.
        if (!task.IsCompleted)
            _loading[node.Key] = task;
        return task;
    }

    private async Task LoadCoreAsync(ExplorerNode node)
    {
        try
        {
            node.View.Items.Clear();
            node.View.Items.Add(Placeholder("Загрузка…"));
            var children = await ChildrenAsync(node);
            node.View.Items.Clear();
            foreach (var child in children)
                node.View.Items.Add(child.View);
            if (children.Count == 0)
                node.View.Items.Add(Placeholder("пусто"));
            node.Loaded = true;
        }
        catch (Exception ex)
        {
            node.View.Items.Clear();
            node.View.Items.Add(Placeholder("✖ " + ex.Message, "DangerBrush"));
            // Узел можно раскрыть снова: свернуть и раскрыть — повторная попытка.
            node.Loaded = false;
            if (node.Type == NodeType.Connection)
                SetState(node.ConnectionId, ConnectionState.Failed, ex.Message);
        }
        finally
        {
            _loading.Remove(node.Key);
            ApplySearch();
        }
    }

    private async Task<List<ExplorerNode>> ChildrenAsync(ExplorerNode node)
    {
        var provider = DbMetadata.For(Profile(node.ConnectionId)?.Provider ?? DbProvider.SqlServer);
        switch (node.Type)
        {
            case NodeType.Connection:
                if (State(node.ConnectionId) != ConnectionState.Connected)
                    await ConnectAsync(node.ConnectionId, reconnect: false, rethrow: true);
                return [Build(new ExplorerNode
                {
                    Type = NodeType.Databases,
                    Key = DatabasesKey(node.ConnectionId),
                    ConnectionId = node.ConnectionId,
                    Text = "Базы данных"
                }, "🗂", "Базы данных", null, expandable: true)];

            case NodeType.Databases:
            {
                var databases = await CachedAsync(node.Key, () => provider.GetDatabasesAsync(ConnectionString(node.ConnectionId, null), default));
                var profile = Profile(node.ConnectionId);
                return databases
                    .Where(d => !_hideSystem || !d.IsSystem || string.Equals(d.Name, profile?.Database, StringComparison.OrdinalIgnoreCase))
                    .Select(d => Build(new ExplorerNode
                    {
                        Type = NodeType.Database,
                        Key = DatabaseKey(node.ConnectionId, d.Name),
                        ConnectionId = node.ConnectionId,
                        Database = d.Name,
                        Item = d,
                        Text = d.Name
                    }, "🛢", d.Name, d.Detail, expandable: d.IsAvailable))
                    .ToList();
            }

            case NodeType.Database:
            {
                var result = new List<ExplorerNode>();
                ExplorerNode? security = null;
                foreach (var folder in provider.DatabaseFolders.Where(f => !_hiddenKinds.Contains(f.Kind)))
                {
                    var child = FolderNode(node, folder, FolderKey(node.Key, folder.Kind));
                    if (folder.Kind is DbObjectKind.User or DbObjectKind.Role)
                    {
                        if (security is null)
                        {
                            security = Build(new ExplorerNode
                            {
                                Type = NodeType.Security,
                                Key = node.Key + "/sec",
                                ConnectionId = node.ConnectionId,
                                Database = node.Database,
                                Text = "Безопасность",
                                Loaded = true
                            }, "🔒", "Безопасность", null, expandable: false);
                            result.Add(security);
                        }

                        security.View.Items.Add(child.View);
                    }
                    else
                    {
                        result.Add(child);
                    }
                }

                if (security is not null)
                    RestoreExpansion(security);
                return result;
            }

            case NodeType.Folder:
            {
                var kind = node.Folder!.Kind;
                var items = await CachedAsync(node.Key, () =>
                    provider.GetObjectsAsync(ConnectionString(node.ConnectionId, node.Database), kind, default));
                return items.Where(i => !_hideSystem || !i.IsSystem).Select(i =>
                {
                    var hasChildren = provider.ChildFolders(i.Kind).Count > 0;
                    return Build(new ExplorerNode
                    {
                        Type = NodeType.Object,
                        Key = node.Key + "/o:" + i.DisplayName,
                        ConnectionId = node.ConnectionId,
                        Database = node.Database,
                        Item = i,
                        Text = i.DisplayName
                    }, Icon(i.Kind), i.DisplayName, i.Detail, expandable: hasChildren);
                }).ToList();
            }

            case NodeType.Object:
                return provider.ChildFolders(node.Item!.Kind).Select(folder => Build(new ExplorerNode
                {
                    Type = NodeType.ChildFolder,
                    Key = node.Key + "/f:" + folder.Kind,
                    ConnectionId = node.ConnectionId,
                    Database = node.Database,
                    Folder = folder,
                    Parent = node.Item,
                    Text = folder.Title
                }, "📂", folder.Title, null, expandable: true)).ToList();

            case NodeType.ChildFolder:
            {
                var items = await CachedAsync(node.Key, () =>
                    provider.GetChildrenAsync(ConnectionString(node.ConnectionId, node.Database), node.Parent!, node.Folder!.Kind, default));
                return items.Select(i => Build(new ExplorerNode
                {
                    Type = NodeType.Child,
                    Key = node.Key + "/o:" + i.Name,
                    ConnectionId = node.ConnectionId,
                    Database = node.Database,
                    Item = i,
                    Parent = node.Parent,
                    Text = i.Name,
                    Loaded = true
                }, Icon(i.Kind), i.Name, i.Detail, expandable: false)).ToList();
            }

            default:
                return [];
        }
    }

    private ExplorerNode FolderNode(ExplorerNode database, DbFolder folder, string key) => Build(new ExplorerNode
    {
        Type = NodeType.Folder,
        Key = key,
        ConnectionId = database.ConnectionId,
        Database = database.Database,
        Folder = folder,
        Text = folder.Title
    }, "📂", folder.Title, null, expandable: true);

    private ExplorerNode Build(ExplorerNode node, string icon, string text, string? detail, bool expandable)
    {
        if (!expandable)
            node.Loaded = true;
        CreateView(node, icon, text, detail, expandable);
        if (node.Type == NodeType.Database)
            UpdateActiveMark(node);
        if (expandable)
            RestoreExpansion(node);
        return node;
    }

    private static string Icon(DbObjectKind kind) => kind switch
    {
        DbObjectKind.Table => "▦",
        DbObjectKind.View => "◫",
        DbObjectKind.Procedure => "⚙",
        DbObjectKind.Function => "ƒ",
        DbObjectKind.Synonym => "↪",
        DbObjectKind.Sequence => "#",
        DbObjectKind.Trigger or DbObjectKind.TableTrigger => "⚡",
        DbObjectKind.Schema => "◇",
        DbObjectKind.User => "👤",
        DbObjectKind.Role => "👥",
        DbObjectKind.Column => "▪",
        DbObjectKind.Key => "🔑",
        DbObjectKind.Index => "≣",
        DbObjectKind.Constraint => "✓",
        _ => "•"
    };

    private async Task<IReadOnlyList<DbObjectInfo>> CachedAsync(string key, Func<Task<IReadOnlyList<DbObjectInfo>>> load)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        // Каталожные запросы — в фоне, чтобы окно не замирало на медленном сервере.
        var result = await Task.Run(load);
        _cache[key] = result;
        return result;
    }

    /// <summary>Строка подключения к базе (null — к базе подключения по умолчанию).</summary>
    internal static string ConnectionString(Guid connectionId, string? database)
    {
        var profile = Profile(connectionId) ?? throw new InvalidOperationException("Подключение удалено.");
        if (database is not null)
            profile.Database = database;
        var password = profile.AuthMode == DbAuthMode.SqlLogin ? App.Connections.LoadPassword(profile.Id) : null;
        if (profile.AuthMode == DbAuthMode.SqlLogin && password is null)
            throw new InvalidOperationException("Для этого подключения не сохранён пароль.");
        return ConnectionStringFactory.Build(profile, password);
    }

    // ---------- подключение ----------

    private ConnectionState State(Guid id) => _states.TryGetValue(id, out var s) ? s.State : ConnectionState.Disconnected;

    internal bool IsConnected(Guid id) => State(id) == ConnectionState.Connected;

    private void SetState(Guid id, ConnectionState state, string? message = null)
    {
        _states[id] = (state, message);
        if (_views.TryGetValue(ConnectionKey(id), out var view) && view.Tag is ExplorerNode node)
            UpdateConnectionChrome(node);
        StateChanged?.Invoke();
    }

    private void UpdateConnectionChrome(ExplorerNode node)
    {
        if (node.Status is null)
            return;
        var (state, message) = _states.TryGetValue(node.ConnectionId, out var s) ? s : (ConnectionState.Disconnected, null);
        var (glyph, brush, text) = state switch
        {
            ConnectionState.Connecting => ("◌", "WarnBrush", "подключение…"),
            ConnectionState.Connected => ("●", "SuccessBrush", "подключено"),
            ConnectionState.Reconnecting => ("↻", "WarnBrush", "переподключение…"),
            ConnectionState.Failed => ("✖", "DangerBrush", "ошибка"),
            _ => ("○", "FgMutedBrush", "отключено")
        };
        node.Status.Text = glyph;
        node.Status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        node.Status.ToolTip = message is null ? text : $"{text}: {message}";
        UpdateActiveMark(node);
    }

    /// <param name="rethrow">true — ошибку пробросить (раскрытие узла покажет её внутри).</param>
    internal async Task ConnectAsync(Guid id, bool reconnect, bool rethrow = false)
    {
        var current = State(id);
        if (current is ConnectionState.Connecting or ConnectionState.Reconnecting)
            return;
        SetState(id, reconnect ? ConnectionState.Reconnecting : ConnectionState.Connecting);
        try
        {
            var connectionString = ConnectionString(id, null);
            await Task.Run(() => ConnectionTester.TestAsync(connectionString, default));
            SetState(id, ConnectionState.Connected);
            App.Log.Info($"Explorer: подключено «{Profile(id)?.Name}».");
        }
        catch (Exception ex)
        {
            SetState(id, ConnectionState.Failed, ex.Message);
            App.Log.Warn($"Explorer: не удалось подключиться к «{Profile(id)?.Name}».");
            if (rethrow)
                throw;
        }
    }

    internal void Disconnect(Guid id)
    {
        ForgetCache(ConnectionKey(id));
        SetState(id, ConnectionState.Disconnected);
        if (_views.TryGetValue(ConnectionKey(id), out var view) && view.Tag is ExplorerNode node)
        {
            _expanded.RemoveWhere(k => k.StartsWith(node.Key, StringComparison.Ordinal));
            view.IsExpanded = false;
            node.Loaded = false;
            view.Items.Clear();
            view.Items.Add(Placeholder("…"));
        }
    }

    internal async Task ReconnectAsync(Guid id)
    {
        ForgetCache(ConnectionKey(id));
        await ConnectAsync(id, reconnect: true);
        if (_views.TryGetValue(ConnectionKey(id), out var view) && view.Tag is ExplorerNode node)
            await RefreshNodeAsync(node);
    }

    // ---------- обновление ----------

    private void ForgetCache(string prefix)
    {
        foreach (var key in _cache.Keys.Where(k => k == prefix || k.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList())
            _cache.Remove(key);
    }

    /// <summary>Перечитать узел с сервера. Раскрытые вложенные узлы раскроются снова.</summary>
    private async Task RefreshNodeAsync(ExplorerNode node)
    {
        ForgetCache(node.Key);
        node.Permissions = null;
        if (node.Type is NodeType.Group or NodeType.Message or NodeType.Child)
            return;
        node.Loaded = false;
        if (node.View.IsExpanded)
            await LoadAsync(node);
        else
        {
            node.View.Items.Clear();
            node.View.Items.Add(Placeholder("…"));
        }
    }

    /// <summary>Обновить всё: кэш забывается, раскрытые узлы загружаются заново.</summary>
    public void RefreshAll()
    {
        _cache.Clear();
        Rebuild();
    }

    public void CollapseAll()
    {
        _expanded.RemoveWhere(k => !k.StartsWith("collapsed:", StringComparison.Ordinal));
        foreach (var item in AllViews(Tree.Items))
        {
            if (item.Tag is ExplorerNode { Type: not NodeType.Group })
                item.IsExpanded = false;
        }

        StateChanged?.Invoke();
    }

    private static IEnumerable<TreeViewItem> AllViews(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var child in AllViews(item.Items))
                yield return child;
        }
    }

    // ---------- контекст активной вкладки ----------

    /// <summary>Подсветить подключение (и базу) активной вкладки. Ничего не меняет в вкладках.</summary>
    public void SetActiveConnection(Guid? connectionId, string? database)
    {
        _activeConnection = connectionId;
        _activeDatabase = database;
        foreach (var item in AllViews(Tree.Items))
        {
            if (item.Tag is ExplorerNode { Type: NodeType.Connection or NodeType.Database } node)
                UpdateActiveMark(node);
        }
    }

    private void UpdateActiveMark(ExplorerNode node)
    {
        if (node.Title is null)
            return;
        var active = node.ConnectionId == _activeConnection
                     && (node.Type == NodeType.Connection
                         || string.Equals(node.Database, _activeDatabase, StringComparison.OrdinalIgnoreCase));
        node.Title.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        if (node.Type == NodeType.Connection)
            node.Title.ToolTip = active ? "Подключение активной вкладки" : null;
    }

    // ---------- поиск ----------

    private bool _searching;

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplySearch();
    }

    private void ApplySearch()
    {
        var query = SearchBox.Text.Trim();
        _searching = true;
        try
        {
            foreach (var item in Tree.Items.OfType<TreeViewItem>())
                Filter(item, query);
        }
        finally
        {
            _searching = false;
        }
    }

    /// <returns>true — узел или кто-то из загруженных потомков подходит.</returns>
    private static bool Filter(TreeViewItem item, string query)
    {
        var node = item.Tag as ExplorerNode;
        if (node?.Type == NodeType.Message)
        {
            item.Visibility = Visibility.Visible;
            return false;
        }

        var self = query.Length == 0 || (node?.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
        var child = false;
        foreach (var sub in item.Items.OfType<TreeViewItem>())
            child |= Filter(sub, self ? string.Empty : query);
        var visible = self || child;
        item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (query.Length > 0 && child && !item.IsExpanded)
            item.IsExpanded = true;
        return visible;
    }

    // ---------- переход к объекту ----------

    /// <summary>Раскрыть путь к объекту, выделить его и прокрутить к нему.</summary>
    /// <returns>false — объект в дереве не найден (скрыт фильтром или нет доступа).</returns>
    internal async Task<bool> RevealAsync(Guid connectionId, string database, DbObjectInfo item)
    {
        SearchBox.Text = string.Empty;
        if (Profile(connectionId) is not { } profile)
            return false;

        var group = ConnectionStore.NormalizeGroup(profile.Group);
        var parts = group.Length == 0 ? [] : group.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            if (_views.TryGetValue("g:" + string.Join('/', parts.Take(i)), out var groupView))
                groupView.IsExpanded = true;
        }

        var folderKind = item.Kind;
        var databaseKey = DatabaseKey(connectionId, database);
        var path = new[]
        {
            ConnectionKey(connectionId),
            DatabasesKey(connectionId),
            databaseKey,
            FolderKey(databaseKey, folderKind),
            FolderKey(databaseKey, folderKind) + "/o:" + item.DisplayName
        };

        if (folderKind is DbObjectKind.User or DbObjectKind.Role && _views.TryGetValue(databaseKey + "/sec", out _))
            path = path.Take(3).Append(databaseKey + "/sec").Concat(path.Skip(3)).ToArray();

        TreeViewItem? last = null;
        foreach (var key in path)
        {
            if (!_views.TryGetValue(key, out var view))
            {
                // Могли появиться только что: путь со «Безопасностью» строится после загрузки базы.
                if (key == databaseKey + "/sec")
                    continue;
                return false;
            }

            last = view;
            if (key == path[^1])
                break;
            if (!view.IsExpanded)
                view.IsExpanded = true;
            if (view.Tag is ExplorerNode node && !node.Loaded)
                await LoadAsync(node);
            if (key == databaseKey && _views.TryGetValue(databaseKey + "/sec", out var sec) && folderKind is DbObjectKind.User or DbObjectKind.Role)
                sec.IsExpanded = true;
        }

        if (last is null || last.Tag is not ExplorerNode { Item: not null })
            return false;
        last.IsSelected = true;
        last.BringIntoView();
        last.Focus();
        return true;
    }

    // ---------- мышь и клавиатура ----------

    private static TreeViewItem? ItemAt(object source)
    {
        var element = source as DependencyObject;
        while (element is not null and not TreeViewItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as TreeViewItem;
    }

    private ExplorerNode? SelectedNode => (Tree.SelectedItem as TreeViewItem)?.Tag as ExplorerNode;

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is { } item)
            item.IsSelected = true;
    }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragNode = ItemAt(e.OriginalSource)?.Tag as ExplorerNode;
        _dragStart = e.GetPosition(Tree);
    }

    /// <summary>Перетаскивание в редактор вставляет полное имя объекта.</summary>
    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragNode is not { } node)
            return;
        var delta = e.GetPosition(Tree) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;
        if (NameOf(node) is not { } name)
            return;
        DragDrop.DoDragDrop(Tree, new DataObject(DataFormats.UnicodeText, name), DragDropEffects.Copy);
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource)?.Tag is not ExplorerNode { Type: NodeType.Connection } node)
            return;
        // Двойной щелчок по подключению — новая вкладка с ним (раскрытие оставляем стрелке).
        e.Handled = true;
        OpenEditorRequested?.Invoke(node.ConnectionId, null);
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedNode is not { } node)
            return;
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = RefreshNodeAsync(node);
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && NameOf(node) is { } name)
        {
            e.Handled = true;
            CopyText(name);
        }
    }

    /// <summary>Имя для вставки в SQL: [schema].[name], у столбца — [name], у базы — [db].</summary>
    private static string? NameOf(ExplorerNode node) => node.Type switch
    {
        NodeType.Database => SqlGenerator.Quote(node.Database!),
        NodeType.Object => SqlGenerator.FullName(node.Item!),
        NodeType.Child when node.Item!.Kind == DbObjectKind.TableTrigger => SqlGenerator.FullName(node.Item.Schema, node.Item.Name),
        NodeType.Child => SqlGenerator.Quote(node.Item!.Name),
        _ => null
    };

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Буфер занят другой программой — не критично.
        }
    }
}
