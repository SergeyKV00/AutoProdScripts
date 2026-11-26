using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Core.Regions;

/// <param name="Server">По нему ограничивается число одновременных запросов к одному кластеру.</param>
/// <param name="Error">Регион нельзя выполнить (нет кластера, пароля) — запроса не будет.</param>
public sealed record RegionTarget(
    string Code, string ConnectionString, string Server, int CommandTimeoutSeconds, string? Error = null);

public sealed record RegionOutcome(string Code, QueryResult? Result, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Общая таблица по регионам; код региона каждой строки — в <see cref="QueryResultSet.RowGroups"/>.</summary>
/// <param name="Number">Номер набора результатов в скрипте с единицы.</param>
public sealed record MergedSet(int Number, QueryResultSet Set, IReadOnlyList<string> Regions, IReadOnlyList<string> Truncated);

/// <summary>Один и тот же скрипт на каждом регионе. Ошибка в одном регионе не останавливает остальные.</summary>
public static class RegionRunner
{
    public static async Task<IReadOnlyList<RegionOutcome>> RunAsync(
        IReadOnlyList<RegionTarget> targets,
        string sql,
        IReadOnlyList<SqlParameterValue> parameters,
        int maxRows,
        IProgress<RegionOutcome>? progress,
        CancellationToken cancellationToken,
        int perServer = 2,
        int total = 8)
    {
        // Общие ошибки скрипта и значений показываем один раз и без обращения к серверам.
        var analysis = ReadOnlyValidator.Analyze(sql);
        if (!analysis.IsAllowed)
            throw new ReadOnlyViolationException(analysis.Violations);
        foreach (var parameter in parameters)
            parameter.ToSqlParameter();

        using var all = new SemaphoreSlim(total);
        var servers = targets.Select(t => t.Server).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(s => s, _ => new SemaphoreSlim(perServer), StringComparer.OrdinalIgnoreCase);

        async Task<RegionOutcome> RunOne(RegionTarget target)
        {
            var outcome = target.Error is not null
                ? new RegionOutcome(target.Code, null, target.Error)
                : await Execute(target);
            progress?.Report(outcome);
            return outcome;
        }

        async Task<RegionOutcome> Execute(RegionTarget target)
        {
            var server = servers[target.Server];
            await server.WaitAsync(cancellationToken);
            try
            {
                await all.WaitAsync(cancellationToken);
                try
                {
                    var result = await Task.Run(() => QueryRunner.RunAsync(new QueryRequest
                    {
                        ConnectionString = target.ConnectionString,
                        CommandTimeoutSeconds = target.CommandTimeoutSeconds,
                        Sql = sql,
                        Parameters = parameters,
                        MaxRows = maxRows
                    }, cancellationToken), cancellationToken);
                    return new RegionOutcome(target.Code, result, result.Error);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new RegionOutcome(target.Code, null, ex.Message);
                }
                finally
                {
                    all.Release();
                }
            }
            finally
            {
                server.Release();
            }
        }

        try
        {
            return await Task.WhenAll(targets.Select(RunOne));
        }
        finally
        {
            foreach (var semaphore in servers.Values)
                semaphore.Dispose();
        }
    }

    /// <summary>
    /// Объединяет наборы с одинаковым номером и колонками. Если у какого-то региона колонки другие,
    /// его строки идут отдельной таблицей — не подставляются в чужие столбцы.
    /// Регионы с ошибкой в таблицы не попадают, даже если успели вернуть часть данных.
    /// </summary>
    public static IReadOnlyList<MergedSet> Merge(IReadOnlyList<RegionOutcome> outcomes) => outcomes
        .Where(o => o.Success && o.Result is not null)
        .SelectMany(o => o.Result!.ResultSets.Select((set, index) => (o.Code, Set: set, Index: index)))
        .GroupBy(x => (x.Index, Columns: string.Join('\u001f', x.Set.Columns)))
        .OrderBy(g => g.Key.Index)
        .Select(group =>
        {
            // Колонки — ровно как в скрипте; регион хранится отдельно от данных.
            var columns = group.First().Set.Columns;
            var rows = group.SelectMany(x => x.Set.Rows).ToList();
            var regions = group.SelectMany(x => Enumerable.Repeat(x.Code, x.Set.Rows.Count)).ToList();
            var truncated = group.Where(x => x.Set.Truncated).Select(x => x.Code).ToList();
            return new MergedSet(
                group.Key.Index + 1,
                new QueryResultSet(columns, rows, truncated.Count > 0, group.First().Set.Types, regions),
                group.Select(x => x.Code).ToList(),
                truncated);
        })
        .ToList();
}
