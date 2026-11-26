using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Explorer;

namespace AutoProdScripts.Views.Queries;

/// <summary>Контекстные меню дерева: подключения, группы и объекты БД. Только чтение: изменяющий SQL — лишь текстом.</summary>
public partial class DbExplorerPanel
{
    private Window? Owner => Window.GetWindow(this);

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        Menu.Items.Clear();
        if (SelectedNode is not { } node || node.Type == NodeType.Message)
        {
            // На пустом месте — только создание подключения.
            Menu.Items.Add(Item("Новое подключение…", () => NewConnection(null)));
            return;
        }

        switch (node.Type)
        {
            case NodeType.Group:
                Menu.Items.Add(Item("Новое подключение в группе…", () => NewConnection(node.Group)));
                Menu.Items.Add(Item("Переименовать группу…", () => RenameGroup(node.Group!)));
                break;
            case NodeType.Connection:
                ConnectionMenu(node);
                break;
            case NodeType.Databases:
            case NodeType.Folder:
            case NodeType.ChildFolder:
            case NodeType.Security:
                Menu.Items.Add(Item("Обновить", () => _ = RefreshNodeAsync(node), "F5"));
                break;
            case NodeType.Database:
                Menu.Items.Add(Item("Новый SQL-редактор", () => OpenEditorRequested?.Invoke(node.ConnectionId, node.Database)));
                Menu.Items.Add(Item("Копировать имя", () => CopyText(NameOf(node)!), "Ctrl+C"));
                Menu.Items.Add(new Separator());
                Menu.Items.Add(Item("Обновить", () => _ = RefreshNodeAsync(node), "F5"));
                break;
            case NodeType.Object:
                ObjectMenu(node);
                break;
            case NodeType.Child:
                Menu.Items.Add(Item("Копировать имя", () => CopyText(NameOf(node)!), "Ctrl+C"));
                if (node.Item!.Kind == DbObjectKind.TableTrigger)
                    Menu.Items.Add(Item("Просмотр DDL", () => _ = ShowDefinitionAsync(node)));
                break;
        }
    }

    private static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------- подключения ----------

    private void ConnectionMenu(ExplorerNode node)
    {
        var id = node.ConnectionId;
        var state = State(id);
        var busy = state is ConnectionState.Connecting or ConnectionState.Reconnecting;
        Menu.Items.Add(Item("Подключиться", () => ConnectAndExpand(node), enabled: state != ConnectionState.Connected && !busy));
        Menu.Items.Add(Item("Отключиться", () => Disconnect(id), enabled: state == ConnectionState.Connected));
        Menu.Items.Add(Item("Переподключиться", () => _ = ReconnectAsync(id), enabled: !busy));
        Menu.Items.Add(Item("Проверить подключение", () => _ = TestAsync(id), enabled: !busy));
        Menu.Items.Add(new Separator());
        Menu.Items.Add(Item("Новый SQL-редактор", () => OpenEditorRequested?.Invoke(id, null)));
        Menu.Items.Add(Item("Обновить структуру", () => _ = RefreshNodeAsync(node), "F5", enabled: state == ConnectionState.Connected));
        Menu.Items.Add(new Separator());
        Menu.Items.Add(Item("Настройки подключения…", () => EditConnection(id)));
        Menu.Items.Add(Item("Создать на основе текущего…", () => CopyConnection(id)));
        Menu.Items.Add(Item("Переименовать…", () => RenameConnection(id)));
        Menu.Items.Add(Item("Переместить в группу…", () => MoveConnection(id)));
        Menu.Items.Add(new Separator());
        Menu.Items.Add(Item("Удалить подключение…", () => DeleteConnection(id)));
    }

    private void ConnectAndExpand(ExplorerNode node)
    {
        if (node.View.IsExpanded)
            _ = ReconnectAsync(node.ConnectionId);
        else
            node.View.IsExpanded = true;
    }

    private async Task TestAsync(Guid id)
    {
        var name = Profile(id)?.Name;
        try
        {
            var connectionString = ConnectionString(id, null);
            var result = await Task.Run(() => ConnectionTester.TestAsync(connectionString, default));
            if (State(id) != ConnectionState.Connected)
                SetState(id, ConnectionState.Connected);
            MessageBox.Show(Owner,
                $"Подключение «{name}» работает: сервер {result.Server}, база {result.Database}."
                + (result.CanWrite ? "\n\nВнимание: у логина есть права на запись." : string.Empty),
                "Проверка подключения", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetState(id, ConnectionState.Failed, ex.Message);
            MessageBox.Show(Owner, $"Не удалось подключиться к «{name}»:\n{ex.Message}", "Проверка подключения",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Мастер нового подключения; group — в какую группу положить.</summary>
    public void NewConnection(string? group)
    {
        var dialog = new ConnectionDialog(new DbConnectionProfile { Group = group }, isNew: true) { Owner = Owner };
        if (dialog.ShowDialog() == true && dialog.Result is { } saved)
            Select(ConnectionKey(saved.Id));
    }

    private void EditConnection(Guid id)
    {
        if (Profile(id) is not { } profile)
            return;
        var dialog = new ConnectionDialog(profile, isNew: false) { Owner = Owner };
        if (dialog.ShowDialog() != true)
            return;
        // Сервер или вход могли измениться — прежняя структура уже не верна.
        if (State(id) == ConnectionState.Connected)
            _ = ReconnectAsync(id);
        else
            ForgetCache(ConnectionKey(id));
    }

    private void CopyConnection(Guid id)
    {
        if (Profile(id) is not { } source)
            return;
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = App.Connections.MakeUniqueName(source.Name);
        var dialog = new ConnectionDialog(copy, isNew: true, copyPasswordFrom: id) { Owner = Owner };
        if (dialog.ShowDialog() == true && dialog.Result is { } saved)
            Select(ConnectionKey(saved.Id));
    }

    private void RenameConnection(Guid id)
    {
        if (Profile(id) is not { } profile)
            return;
        var dialog = new InputDialog("Переименовать", "Имя подключения:", profile.Name) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Value == profile.Name)
            return;
        if (TryChange(() => App.Connections.Rename(id, dialog.Value)))
            ShowRenameNote(profile.Name);
    }

    /// <summary>Скрипты библиотеки находят подключение по Id, а на другом ПК — по имени.</summary>
    private void ShowRenameNote(string oldName)
    {
        var used = App.Library.GetAll().Count(s => s.ConnectionId is null
                                                   && string.Equals(s.ConnectionName, oldName, StringComparison.OrdinalIgnoreCase));
        if (used > 0)
            MessageBox.Show(Owner, $"Скриптов без привязки по Id с именем «{oldName}»: {used}. Им нужно выбрать подключение заново в «Свойствах».",
                "Подключения", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MoveConnection(Guid id)
    {
        if (Profile(id) is not { } profile)
            return;
        var dialog = new InputDialog("Переместить в группу", "Группа (вложенность через «/», пусто — без группы):",
            ConnectionStore.NormalizeGroup(profile.Group), App.Connections.Groups()) { Owner = Owner };
        if (dialog.ShowDialog() == true && TryChange(() => App.Connections.MoveToGroup(id, dialog.Value)))
            Select(ConnectionKey(id));
    }

    private void RenameGroup(string group)
    {
        var dialog = new InputDialog("Переименовать группу", "Новое имя группы (пусто — убрать группу):", group) { Owner = Owner };
        if (dialog.ShowDialog() == true)
            TryChange(() => App.Connections.RenameGroup(group, dialog.Value));
    }

    private void DeleteConnection(Guid id)
    {
        if (Profile(id) is not { } profile)
            return;
        var answer = MessageBox.Show(Owner,
            $"Удалить подключение «{profile.Name}» из приложения?\n\nУдалятся только настройки и сохранённый пароль. "
            + "Сама база данных не затрагивается.",
            "Удаление подключения", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;
        ForgetCache(ConnectionKey(id));
        _expanded.RemoveWhere(k => k.StartsWith(ConnectionKey(id), StringComparison.Ordinal));
        _states.Remove(id);
        TryChange(() => App.Connections.Delete(id));
    }

    private bool TryChange(Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Owner, ex.Message, "Подключения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Select(string key)
    {
        if (!_views.TryGetValue(key, out var view))
            return;
        view.IsSelected = true;
        view.BringIntoView();
    }

    // ---------- объекты БД ----------

    private void ObjectMenu(ExplorerNode node)
    {
        var item = node.Item!;
        var name = NameOf(node)!;
        var data = item.Kind is DbObjectKind.Table or DbObjectKind.View;

        if (data)
        {
            var view = Item("Просмотр данных", () => OpenData(node, export: false));
            var structure = Item("Просмотр структуры", () => Open(node, "Структура " + item.DisplayName,
                SqlGenerator.Structure(item.Schema, item.Name), run: true));
            Menu.Items.Add(view);
            Menu.Items.Add(structure);
            var ddl = Item("Просмотр DDL", () => _ = ShowDefinitionAsync(node));
            Menu.Items.Add(ddl);
            var generate = new MenuItem { Header = "Генерация SQL" };
            generate.Items.Add(Item("SELECT", () => _ = GenerateAsync(node, "SELECT", c => SqlGenerator.Select(item.Schema, item.Name, c))));
            if (item.Kind == DbObjectKind.Table)
            {
                generate.Items.Add(Item("INSERT", () => _ = GenerateAsync(node, "INSERT", c => SqlGenerator.Insert(item.Schema, item.Name, c))));
                generate.Items.Add(Item("UPDATE", () => _ = GenerateAsync(node, "UPDATE", c => SqlGenerator.Update(item.Schema, item.Name, c))));
                generate.Items.Add(Item("DELETE", () => _ = GenerateAsync(node, "DELETE", c => SqlGenerator.Delete(item.Schema, item.Name, c))));
            }

            generate.ToolTip = "Текст в новой вкладке. INSERT/UPDATE/DELETE не выполняются: раздел только для чтения.";
            Menu.Items.Add(generate);
            Menu.Items.Add(Item("Копировать полное имя", () => CopyText(name), "Ctrl+C"));
            Menu.Items.Add(new Separator());
            Menu.Items.Add(Item("Обновить", () => _ = RefreshNodeAsync(node), "F5"));
            var export = Item("Экспорт данных в CSV…", () => OpenData(node, export: true));
            Menu.Items.Add(export);
            _ = ApplyPermissionsAsync(node, view, export, ddl);
            return;
        }

        switch (item.Kind)
        {
            case DbObjectKind.Procedure:
                Menu.Items.Add(Item("Просмотр DDL", () => _ = ShowDefinitionAsync(node)));
                Menu.Items.Add(Item("Генерация EXEC", () => _ = GenerateAsync(node, "EXEC", c => SqlGenerator.Execute(item.Schema, item.Name, c))));
                break;
            case DbObjectKind.Function:
                Menu.Items.Add(Item("Просмотр DDL", () => _ = ShowDefinitionAsync(node)));
                Menu.Items.Add(Item("Генерация SELECT", () => _ = GenerateAsync(node, "SELECT",
                    c => SqlGenerator.CallFunction(item.Schema, item.Name, c, tableValued: item.Detail?.StartsWith("табличная") == true))));
                break;
            case DbObjectKind.Synonym or DbObjectKind.Sequence or DbObjectKind.Trigger or DbObjectKind.Schema:
                Menu.Items.Add(Item("Просмотр DDL", () => _ = ShowDefinitionAsync(node)));
                break;
        }

        Menu.Items.Add(Item(item.Schema is null ? "Копировать имя" : "Копировать полное имя", () => CopyText(name), "Ctrl+C"));
    }

    /// <summary>Пункты, для которых у логина нет прав, выключаются (проверка — по открытию меню).</summary>
    private async Task ApplyPermissionsAsync(ExplorerNode node, MenuItem view, MenuItem export, MenuItem ddl)
    {
        if (node.Permissions is null)
        {
            try
            {
                var provider = DbMetadata.For(Profile(node.ConnectionId)?.Provider ?? DbProvider.SqlServer);
                var connectionString = ConnectionString(node.ConnectionId, node.Database);
                node.Permissions = await Task.Run(() => provider.GetPermissionsAsync(connectionString, node.Item!, default));
            }
            catch (Exception)
            {
                // Права не узнали — оставляем пункты включёнными: ошибка покажется при выполнении.
                return;
            }
        }

        var permissions = node.Permissions;
        view.IsEnabled = export.IsEnabled = permissions.CanSelect;
        ddl.IsEnabled = permissions.CanViewDefinition;
        if (!permissions.CanSelect)
            view.ToolTip = export.ToolTip = "Нет права SELECT";
        if (!permissions.CanViewDefinition)
            ddl.ToolTip = "Нет права VIEW DEFINITION";
    }

    private void OpenData(ExplorerNode node, bool export)
    {
        var item = node.Item!;
        // Для экспорта — все строки (в пределах лимита вкладки), для просмотра — первые 1000.
        var sql = SqlGenerator.Select(item.Schema, item.Name, [], top: export ? null : SqlGenerator.PreviewRows);
        Open(node, (export ? "Экспорт " : "Данные ") + item.DisplayName, sql, run: true, exportAfter: export);
    }

    private void Open(ExplorerNode node, string title, string sql, bool run = false, bool exportAfter = false) =>
        OpenSqlRequested?.Invoke(new OpenSqlRequest(node.ConnectionId, node.Database, title, sql, run, exportAfter));

    private async Task ShowDefinitionAsync(ExplorerNode node)
    {
        try
        {
            var provider = DbMetadata.For(Profile(node.ConnectionId)?.Provider ?? DbProvider.SqlServer);
            var connectionString = ConnectionString(node.ConnectionId, node.Database);
            var item = node.Item!;
            var ddl = await Task.Run(() => provider.GetDefinitionAsync(connectionString, item, default));
            Open(node, "DDL " + item.DisplayName, "-- DDL только для просмотра: раздел выполняет лишь чтение.\n" + ddl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Owner, "Не удалось получить DDL: " + ex.Message, "Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task GenerateAsync(ExplorerNode node, string what, Func<IReadOnlyList<DbColumnInfo>, string> generate)
    {
        try
        {
            var provider = DbMetadata.For(Profile(node.ConnectionId)?.Provider ?? DbProvider.SqlServer);
            var connectionString = ConnectionString(node.ConnectionId, node.Database);
            var item = node.Item!;
            var columns = await Task.Run(() => provider.GetColumnsAsync(connectionString, item, default));
            Open(node, $"{what} {item.DisplayName}", generate(columns));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Owner, "Не удалось сформировать SQL: " + ex.Message, "Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
