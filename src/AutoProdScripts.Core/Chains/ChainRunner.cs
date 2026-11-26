using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Core.Chains;

public sealed record StepConnection(string Name, string ConnectionString, int CommandTimeoutSeconds);

public enum ChainStepStatus
{
    Succeeded,
    Empty,
    Failed,
    Skipped
}

public sealed record ChainStepResult(
    int Index,
    string Title,
    string ConnectionName,
    ChainStepStatus Status,
    string Message,
    QueryResult? Result);

/// <summary>
/// Шаг-источник вернул несколько разных строк — нужно выбрать одну. Выбор делается один раз:
/// из этой строки берутся значения всех параметров всех следующих шагов.
/// </summary>
/// <param name="Uses">Что будет взято из строки: параметр, колонка и номер шага-получателя.</param>
public sealed record ChoiceRequest(
    int SourceStepIndex,
    IReadOnlyList<RowUse> Uses,
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows);

public sealed record RowUse(int StepIndex, string Parameter, string Column);

public sealed class ChainRunRequest
{
    public required LibraryChain Chain { get; init; }
    public required Func<Guid, LibraryScript?> FindScript { get; init; }

    /// <summary>Бросает <see cref="InvalidOperationException"/>, если подключения нет.</summary>
    public required Func<ChainStep, LibraryScript, StepConnection> Connect { get; init; }

    /// <summary>Значения общих параметров по именам; null — это NULL.</summary>
    public IReadOnlyDictionary<string, string?> Values { get; init; } = new Dictionary<string, string?>();

    public int MaxRows { get; init; } = 10_000;

    /// <summary>Номер выбранной строки или null — остановиться.</summary>
    public Func<ChoiceRequest, Task<int?>>? Choose { get; init; }
}

/// <summary>
/// Выполняет шаги по очереди. Каждый шаг — отдельное подключение (возможно, на другом кластере)
/// со своей откатываемой транзакцией; данные между ними переносит приложение.
/// </summary>
public static class ChainRunner
{
    private sealed class StopStep(string message) : Exception(message);

    /// <exception cref="InvalidOperationException">Цепочка не прошла проверку; серверы не затронуты.</exception>
    public static async Task<IReadOnlyList<ChainStepResult>> RunAsync(
        ChainRunRequest request,
        IProgress<ChainStepResult>? progress,
        CancellationToken cancellationToken)
    {
        var plan = ChainPlan.Build(request.Chain, request.FindScript);
        if (!plan.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Problems));

        // Подключения и введённые значения проверяем до первого запроса, а не на середине цепочки.
        var connections = plan.Steps.Select(s => request.Connect(s.Step, s.Script)).ToList();
        var values = new Dictionary<string, string?>(request.Values, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            foreach (var input in plan.Steps[i].Inputs.Where(x => x.FromStep is null))
                CommonValue(plan, plan.Steps[i], input, values).ToSqlParameter();
        }

        var results = new List<ChainStepResult>();
        // Строка, выбранная из результата шага-источника (номер шага -> строка).
        var chosenRows = new Dictionary<int, object?[]>();
        var stopped = false;
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var result = stopped
                ? Report(ChainStepStatus.Skipped, "Не выполнялся.", null)
                : await RunStepAsync();
            results.Add(result);
            progress?.Report(result);
            stopped = stopped
                      || result.Status == ChainStepStatus.Failed
                      || result.Status == ChainStepStatus.Empty && step.Step.StopWhenEmpty;

            ChainStepResult Report(ChainStepStatus status, string message, QueryResult? query) =>
                new(index, step.Script.Name, connections[index].Name, status, message, query);

            async Task<ChainStepResult> RunStepAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                QueryResult query;
                try
                {
                    var parameters = new List<SqlParameterValue>();
                    foreach (var input in step.Inputs)
                    {
                        parameters.Add(input.FromStep is { } source
                            ? ValueFromRow(await RowOfStepAsync(request, plan, source, results, chosenRows), input, source, results)
                            : CommonValue(plan, step, input, values));
                    }

                    query = await QueryRunner.RunAsync(new QueryRequest
                    {
                        ConnectionString = connections[index].ConnectionString,
                        CommandTimeoutSeconds = connections[index].CommandTimeoutSeconds,
                        Sql = step.Script.Sql,
                        Parameters = parameters,
                        InputTables = step.TableSources.Select(s => TableFromStep(plan, s, results)).ToList(),
                        MaxRows = request.MaxRows
                    }, cancellationToken);
                }
                catch (StopStep ex)
                {
                    return Report(ChainStepStatus.Failed, ex.Message, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Сбой подключения или передачи данных — это результат шага, а не всей программы.
                    return Report(ChainStepStatus.Failed, ex.Message, null);
                }

                if (!query.Success)
                    return Report(ChainStepStatus.Failed, query.Error!, query);

                var rows = query.ResultSets.Count > 0 ? query.ResultSets[0].Rows.Count : 0;
                if (rows > 0)
                {
                    var more = query.ResultSets[0].Truncated ? " (достигнут лимит)" : string.Empty;
                    return Report(ChainStepStatus.Succeeded, $"Найдено строк: {rows}{more}.", query);
                }

                var empty = step.Step.EmptyMessage.Trim();
                return Report(ChainStepStatus.Empty, empty.Length > 0 ? empty : "Строк не найдено.", query);
            }
        }

        return results;
    }

    private static SqlParameterValue CommonValue(
        ChainPlan plan, PlannedStep step, StepInput input, IReadOnlyDictionary<string, string?> values)
    {
        if (!values.TryGetValue(input.Source, out var text))
            throw new InvalidOperationException($"Не задан общий параметр {input.Source}.");

        // Тип из скрипта точнее общего: одна и та же колонка в базах бывает varchar и nvarchar.
        var kind = step.Script.Parameters.FirstOrDefault(p => Same(p.Name, input.Parameter))?.Kind
                   ?? plan.CommonParameters.First(p => Same(p.Name, input.Source)).Kind;
        return new SqlParameterValue(input.Parameter, kind, text);
    }

    private static QueryResultSet Output(IReadOnlyList<ChainStepResult> results, int source)
    {
        var query = results[source].Result;
        if (query is null || query.ResultSets.Count == 0 || query.ResultSets[0].Rows.Count == 0)
            throw new StopStep($"Шаг {source + 1} не вернул строк — передавать нечего.");
        // Часть данных опаснее, чем никаких: выводы по ней будут неверными.
        if (query.ResultSets[0].Truncated)
            throw new StopStep($"Результат шага {source + 1} обрезан по лимиту строк — передавать его нельзя.");
        return query.ResultSets[0];
    }

    private static async Task<object?[]> RowOfStepAsync(
        ChainRunRequest request,
        ChainPlan plan,
        int source,
        IReadOnlyList<ChainStepResult> results,
        Dictionary<int, object?[]> chosenRows)
    {
        if (chosenRows.TryGetValue(source, out var chosen))
            return chosen;

        var set = Output(results, source);
        var uses = plan.Steps
            .SelectMany((step, i) => step.Inputs
                .Where(x => x.FromStep == source)
                .Select(x => new RowUse(i, x.Parameter, x.Source)))
            .ToList();
        // Строки различаются только тем, что из них действительно берут. Отсутствующая колонка
        // станет ошибкой того шага, которому она нужна.
        var used = uses.Select(u => ColumnIndex(set, u.Column)).Where(i => i >= 0).Distinct().ToList();
        var rows = set.Rows.DistinctBy(row => RowKey(row, used)).ToList();

        var selected = 0;
        if (rows.Count > 1)
        {
            var choice = request.Choose is null
                ? null
                : await request.Choose(new ChoiceRequest(source, uses, set.Columns, rows));
            selected = choice ?? throw new StopStep($"Шаг {source + 1} вернул несколько строк — строка не выбрана.");
        }

        return chosenRows[source] = rows[selected];
    }

    private static SqlParameterValue ValueFromRow(
        object?[] row, StepInput input, int source, IReadOnlyList<ChainStepResult> results)
    {
        var set = Output(results, source);
        var column = ColumnIndex(set, input.Source);
        if (column < 0)
            throw new StopStep($"В результате шага {source + 1} нет единственной колонки «{input.Source}».");

        var type = set.Types?[column]
                   ?? throw new StopStep($"Значение колонки «{input.Source}» этого типа передать нельзя.");
        return SqlParameterValue.FromResult(input.Parameter, row[column], type);
    }

    /// <returns>-1 — колонки с таким именем нет или их несколько.</returns>
    private static int ColumnIndex(QueryResultSet set, string name)
    {
        var found = set.Columns.Select((column, i) => (column, i)).Where(c => Same(c.column, name)).ToList();
        return found.Count == 1 ? found[0].i : -1;
    }

    private static string RowKey(object?[] row, IReadOnlyList<int> columns) => string.Concat(columns.Select(i =>
    {
        var text = row[i] switch
        {
            null => null,
            byte[] bytes => Convert.ToHexString(bytes),
            DateTime date => date.Ticks.ToString(),
            DateTimeOffset date => date.ToString("O"),
            var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)
        };
        // Длина впереди, чтобы соседние значения не склеивались в одинаковый ключ.
        return text is null ? "~;" : $"{text.Length}:{text};";
    }));

    private static InputTable TableFromStep(ChainPlan plan, int source, IReadOnlyList<ChainStepResult> results)
    {
        var set = Output(results, source);
        return new InputTable(plan.Steps[source].Step.OutputTable.Trim(), set.Columns, set.Types ?? [], set.Rows);
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
