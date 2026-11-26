using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;
using AutoProdScripts.Core.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AutoProdScripts.Core.Chains;

/// <summary>Параметр шага и откуда он берётся.</summary>
/// <param name="FromStep">Номер шага-источника с нуля; null — общий параметр.</param>
public sealed record StepInput(string Parameter, int? FromStep, string Source);

public sealed record PlannedStep(
    ChainStep Step,
    LibraryScript Script,
    IReadOnlyList<StepInput> Inputs,
    IReadOnlyList<int> TableSources);

/// <summary>
/// Разбор цепочки до обращения к серверам: всё, что можно проверить заранее, проверяется здесь.
/// </summary>
public sealed record ChainPlan(
    IReadOnlyList<PlannedStep> Steps,
    IReadOnlyList<ScriptParameter> CommonParameters,
    IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;

    public static ChainPlan Build(LibraryChain chain, Func<Guid, LibraryScript?> findScript)
    {
        var steps = new List<PlannedStep>();
        var common = new List<ScriptParameter>();
        var problems = new List<string>();
        var outputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (chain.Steps.Count == 0)
            problems.Add("В цепочке нет шагов.");

        for (var index = 0; index < chain.Steps.Count; index++)
        {
            var step = chain.Steps[index];
            var title = $"Шаг {index + 1}";
            var script = step.Inline?.Clone() ?? findScript(step.ScriptId);
            if (step.Inline is not null && string.IsNullOrWhiteSpace(script!.Name))
                script.Name = $"Скрипт шага {index + 1}";
            if (script is null)
            {
                problems.Add($"{title}: не выбран скрипт или он удалён из библиотеки.");
                continue;
            }

            var analysis = ReadOnlyValidator.Analyze(script.Sql);
            if (!analysis.IsAllowed)
            {
                problems.Add($"{title} «{script.Name}» не прошёл проверку «только чтение»: {analysis.Violations[0]}");
                continue;
            }

            var inputs = new List<StepInput>();
            foreach (var name in analysis.Parameters)
            {
                var binding = step.Bindings.FirstOrDefault(b => Same(b.Parameter, name));
                if (binding?.FromStep is { } sourceId)
                {
                    var source = chain.Steps.FindIndex(s => s.Id == sourceId);
                    if (source < 0 || source >= index)
                        problems.Add($"{title}: параметр {name} берётся из шага, который не выполняется раньше.");
                    else if (binding.Source.Trim().Length == 0)
                        problems.Add($"{title}: для параметра {name} не указана колонка шага {source + 1}.");
                    else
                        inputs.Add(new StepInput(name, source, binding.Source.Trim()));
                    continue;
                }

                var commonName = string.IsNullOrWhiteSpace(binding?.Source) ? name : binding.Source.Trim();
                inputs.Add(new StepInput(name, null, commonName));
                if (common.Any(p => Same(p.Name, commonName)))
                    continue;

                // Подпись и тип — из цепочки, а пока их там нет, из самого скрипта.
                var described = chain.Parameters.FirstOrDefault(p => Same(p.Name, commonName))?.Clone()
                                ?? script.Parameters.FirstOrDefault(p => Same(p.Name, name))?.Clone()
                                ?? new ScriptParameter();
                described.Name = commonName;
                common.Add(described);
            }

            var references = TableNames.Referenced(script.Sql);
            var tables = outputs.Where(o => references.Contains(o.Key)).Select(o => o.Value).ToList();
            var created = TableNames.Created(script.Sql);
            foreach (var table in outputs.Keys.Where(created.Contains))
                problems.Add($"{title}: скрипт сам создаёт {table}, а это имя занято результатом шага {outputs[table] + 1}.");

            var output = step.OutputTable.Trim();
            if (output.Length > 0)
            {
                if (!QueryRunner.IsValidTableName(output))
                    problems.Add($"{title}: имя таблицы должно быть вида #name (буквы, цифры, «_»).");
                else if (!outputs.TryAdd(output, index))
                    problems.Add($"{title}: имя {output} уже занято другим шагом.");
            }

            steps.Add(new PlannedStep(step, script, inputs, tables));
        }

        return new ChainPlan(steps, common, problems);
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class TableNames : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _referenced = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);

        public static HashSet<string> Referenced(string sql) => Collect(sql)._referenced;

        public static HashSet<string> Created(string sql) => Collect(sql)._created;

        private static TableNames Collect(string sql)
        {
            var visitor = new TableNames();
            new TSql170Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out _).Accept(visitor);
            return visitor;
        }

        public override void Visit(NamedTableReference node) => _referenced.Add(node.SchemaObject.BaseIdentifier.Value);

        public override void Visit(CreateTableStatement node) => _created.Add(node.SchemaObjectName.BaseIdentifier.Value);

        public override void Visit(SelectStatement node)
        {
            if (node.Into is not null)
                _created.Add(node.Into.BaseIdentifier.Value);
        }
    }
}
