using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AutoProdScripts.Core.Sql;

/// <summary>
/// Для подключения к кластеру (без базы): говорит ли сам скрипт, в какой базе искать таблицы.
/// </summary>
public static class DatabaseChoice
{
    /// <returns>
    /// true — нет USE, а какая-то таблица названа без базы: её будут искать в базе по умолчанию.
    /// </returns>
    public static bool IsMissing(string sql)
    {
        var fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
            return false;

        var visitor = new Visitor();
        fragment.Accept(visitor);
        return !visitor.HasUse && visitor.Unqualified.Any(name => !visitor.CommonTables.Contains(name));
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public bool HasUse { get; private set; }
        public List<string> Unqualified { get; } = new();
        public HashSet<string> CommonTables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(UseStatement node) => HasUse = true;

        public override void Visit(CommonTableExpression node) => CommonTables.Add(node.ExpressionName.Value);

        public override void Visit(NamedTableReference node)
        {
            var name = node.SchemaObject;
            var table = name.BaseIdentifier.Value;
            // Временные таблицы и системные представления есть в любой базе.
            if (name.DatabaseIdentifier is null
                && !table.StartsWith('#')
                && !string.Equals(name.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase))
                Unqualified.Add(table);
        }
    }
}
