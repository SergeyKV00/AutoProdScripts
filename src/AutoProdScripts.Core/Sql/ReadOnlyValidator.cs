using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AutoProdScripts.Core.Sql;

public sealed record SqlViolation(int Line, string Message)
{
    public override string ToString() => $"Строка {Line}: {Message}";
}

/// <summary>Один пакет (часть скрипта между GO) и переменные, которые он берёт извне.</summary>
public sealed record ScriptBatch(string Text, int StartLine, IReadOnlyList<string> Parameters);

public sealed record SqlAnalysis(IReadOnlyList<SqlViolation> Violations, IReadOnlyList<ScriptBatch> Batches)
{
    public bool IsAllowed => Violations.Count == 0;

    public IReadOnlyList<string> Parameters =>
        Batches.SelectMany(b => b.Parameters).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Проверка «только чтение» по синтаксическому дереву. Разрешено только явно перечисленное:
/// всё незнакомое отклоняется.
/// </summary>
public static class ReadOnlyValidator
{
    public static SqlAnalysis Analyze(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);

        if (errors.Count > 0)
        {
            var parseErrors = errors
                .Take(5)
                .Select(e => new SqlViolation(e.Line, $"не удалось разобрать SQL ({e.Message})"))
                .ToList();
            return new SqlAnalysis(parseErrors, []);
        }

        var violations = new List<SqlViolation>();
        var batches = new List<ScriptBatch>();

        foreach (var batch in ((TSqlScript)fragment).Batches)
        {
            var guard = new GuardVisitor(violations);
            batch.Accept(guard);

            var variables = new VariableCollector();
            batch.Accept(variables);

            var text = sql.Substring(batch.StartOffset, batch.FragmentLength);
            batches.Add(new ScriptBatch(text, batch.StartLine, variables.External()));
        }

        if (batches.Count == 0)
            violations.Add(new SqlViolation(1, "скрипт пуст"));

        return new SqlAnalysis(violations.Distinct().OrderBy(v => v.Line).ToList(), batches);
    }

    private sealed class VariableCollector : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _declared = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _used = new();

        public IReadOnlyList<string> External() =>
            _used.Where(v => !_declared.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public override void Visit(DeclareVariableElement node) => _declared.Add(node.VariableName.Value);

        public override void Visit(DeclareTableVariableBody node)
        {
            if (node.VariableName is not null)
                _declared.Add(node.VariableName.Value);
        }

        public override void Visit(VariableReference node) => _used.Add(node.Name);
    }

    private sealed class AliasCollector : TSqlFragmentVisitor
    {
        public List<TableReferenceWithAlias> Items { get; } = new();

        public override void Visit(TableReference node)
        {
            if (node is TableReferenceWithAlias aliased)
                Items.Add(aliased);
        }
    }

    private sealed class GuardVisitor(List<SqlViolation> violations) : TSqlFragmentVisitor
    {
        private void Deny(TSqlFragment node, string message) => violations.Add(new SqlViolation(node.StartLine, message));

        // Вызывается для каждой инструкции, включая вложенные в IF/WHILE/TRY.
        public override void Visit(TSqlStatement node)
        {
            switch (node)
            {
                case SelectStatement select:
                    if (select.Into is not null && !IsTempTable(select.Into))
                        Deny(node, "SELECT INTO разрешён только во временную таблицу #…");
                    break;

                case InsertStatement insert:
                    CheckTarget(node, insert.InsertSpecification, null, insert.WithCtesAndXmlNamespaces);
                    break;
                case UpdateStatement update:
                    CheckTarget(node, update.UpdateSpecification, update.UpdateSpecification.FromClause, update.WithCtesAndXmlNamespaces);
                    break;
                case DeleteStatement delete:
                    CheckTarget(node, delete.DeleteSpecification, delete.DeleteSpecification.FromClause, delete.WithCtesAndXmlNamespaces);
                    break;
                case MergeStatement merge:
                    CheckTarget(node, merge.MergeSpecification, null, merge.WithCtesAndXmlNamespaces);
                    break;

                case CreateTableStatement create:
                    RequireTemp(node, create.SchemaObjectName, "CREATE TABLE");
                    break;
                case DropTableStatement drop:
                    foreach (var name in drop.Objects)
                        RequireTemp(node, name, "DROP TABLE");
                    break;
                case TruncateTableStatement truncate:
                    RequireTemp(node, truncate.TableName, "TRUNCATE TABLE");
                    break;
                case CreateIndexStatement index:
                    RequireTemp(node, index.OnName, "CREATE INDEX");
                    break;
                case AlterTableAddTableElementStatement add:
                    RequireTemp(node, add.SchemaObjectName, "ALTER TABLE");
                    break;
                case AlterTableAlterColumnStatement alter:
                    RequireTemp(node, alter.SchemaObjectName, "ALTER TABLE");
                    break;
                case AlterTableDropTableElementStatement dropElement:
                    RequireTemp(node, dropElement.SchemaObjectName, "ALTER TABLE");
                    break;

                case SetVariableStatement set:
                    if (set.CursorDefinition is not null)
                        Deny(node, "курсоры запрещены");
                    break;
                case SetTransactionIsolationLevelStatement isolation:
                    if (isolation.Level is not (IsolationLevel.ReadCommitted or IsolationLevel.ReadUncommitted or IsolationLevel.Snapshot))
                        Deny(node, "уровни изоляции REPEATABLE READ и SERIALIZABLE удерживают блокировки и запрещены");
                    break;
                case RaiseErrorStatement raise:
                    if (raise.RaiseErrorOptions.HasFlag(RaiseErrorOptions.Log))
                        Deny(node, "RAISERROR WITH LOG пишет в журнал сервера и запрещён");
                    break;

                case DeclareVariableStatement:
                case DeclareTableVariableStatement:
                case PredicateSetStatement:
                case SetRowCountStatement:
                case SetCommandStatement:
                case SetStatisticsStatement:
                case SetTextSizeStatement:
                case IfStatement:
                case WhileStatement:
                case BreakStatement:
                case ContinueStatement:
                case TryCatchStatement:
                case ReturnStatement:
                case PrintStatement:
                case ThrowStatement:
                case GoToStatement:
                case LabelStatement:
                case UseStatement:
                    break;

                case BeginEndBlockStatement block when block is not BeginEndAtomicBlockStatement:
                    break;

                case ExecuteStatement:
                case ExecuteAsStatement:
                    Deny(node, "EXEC (процедуры и динамический SQL) запрещён: по вызову не видно, что он меняет");
                    break;
                case BeginTransactionStatement:
                case CommitTransactionStatement:
                case RollbackTransactionStatement:
                case SaveTransactionStatement:
                    Deny(node, "управление транзакциями запрещено: приложение само откатывает транзакцию");
                    break;

                default:
                    Deny(node, $"инструкция {Describe(node)} не разрешена в режиме «только чтение»");
                    break;
            }
        }

        public override void Visit(ExecuteSpecification node) =>
            Deny(node, "EXEC (процедуры и динамический SQL) запрещён: по вызову не видно, что он меняет");

        public override void Visit(DataModificationTableReference node) =>
            Deny(node, "изменение данных внутри FROM запрещено");

        public override void Visit(OpenQueryTableReference node) => Deny(node, "OPENQUERY запрещён");

        public override void Visit(OpenRowsetTableReference node) => Deny(node, "OPENROWSET запрещён");

        public override void Visit(AdHocTableReference node) => Deny(node, "OPENDATASOURCE запрещён");

        public override void Visit(BulkOpenRowset node) => Deny(node, "OPENROWSET(BULK) запрещён");

        public override void Visit(InternalOpenRowset node) => Deny(node, "OPENROWSET запрещён");

        public override void Visit(NextValueForExpression node) => Deny(node, "NEXT VALUE FOR изменяет последовательность и запрещён");

        public override void Visit(NamedTableReference node)
        {
            if (IsTempTable(node.SchemaObject))
                return;

            foreach (var hint in node.TableHints)
            {
                if (hint.HintKind is TableHintKind.XLock or TableHintKind.UpdLock or TableHintKind.TabLockX
                    or TableHintKind.TabLock or TableHintKind.HoldLock or TableHintKind.Serializable
                    or TableHintKind.RepeatableRead or TableHintKind.PagLock)
                {
                    Deny(node, $"блокирующая подсказка {hint.HintKind} запрещена");
                }
            }
        }

        private void CheckTarget(
            TSqlStatement statement,
            DataModificationSpecification specification,
            FromClause? from,
            WithCtesAndXmlNamespaces? with)
        {
            if (specification.OutputIntoClause is { } output && !IsLocalTarget(output.IntoTable, null, null))
                Deny(statement, "OUTPUT INTO разрешён только во временную таблицу или табличную переменную");

            if (!IsLocalTarget(specification.Target, from, with))
                Deny(statement, "изменять данные можно только во временных таблицах #… и табличных переменных");
        }

        private static bool IsLocalTarget(TableReference? target, FromClause? from, WithCtesAndXmlNamespaces? with)
        {
            if (target is VariableTableReference variable && from is null)
                return variable.Variable is not null;

            var name = target switch
            {
                NamedTableReference named => named.SchemaObject,
                _ => null
            };

            // UPDATE x … FROM … AS x: решает не само имя, а то, что стоит за псевдонимом.
            var single = name is { Identifiers.Count: 1 } ? name.BaseIdentifier.Value : null;
            var variableName = (target as VariableTableReference)?.Variable.Name;
            var exposed = single ?? variableName;

            if (exposed is not null && from is not null)
            {
                var references = new AliasCollector();
                from.Accept(references);
                var aliased = references.Items
                    .Where(r => string.Equals(r.Alias?.Value, exposed, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (aliased.Count > 0)
                    return aliased.All(r => r is VariableTableReference
                                            || r is NamedTableReference n && IsTempTable(n.SchemaObject) && !IsCte(n.SchemaObject, with));
            }

            if (variableName is not null)
                return true;

            return name is not null && IsTempTable(name) && !IsCte(name, with);
        }

        private static bool IsCte(SchemaObjectName name, WithCtesAndXmlNamespaces? with) =>
            name.Identifiers.Count == 1 && with is not null && with.CommonTableExpressions.Any(c =>
                string.Equals(c.ExpressionName.Value, name.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase));

        private void RequireTemp(TSqlStatement statement, SchemaObjectName name, string operation)
        {
            if (!IsTempTable(name))
                Deny(statement, $"{operation} разрешён только для временных таблиц #…");
        }

        private static bool IsTempTable(SchemaObjectName? name)
        {
            if (name?.BaseIdentifier?.Value is not { } table)
                return false;
            if (!table.StartsWith('#') || table.StartsWith("##", StringComparison.Ordinal))
                return false;
            if (name.ServerIdentifier is not null)
                return false;
            return name.DatabaseIdentifier is null
                   || string.Equals(name.DatabaseIdentifier.Value, "tempdb", StringComparison.OrdinalIgnoreCase);
        }

        private static string Describe(TSqlStatement node) =>
            node.GetType().Name.Replace("Statement", string.Empty);
    }
}
