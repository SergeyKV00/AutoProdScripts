using System.Data;
using System.Diagnostics;
using AutoProdScripts.Core.Sql;
using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Execution;

public sealed class QueryRequest
{
    public required string ConnectionString { get; init; }
    public required string Sql { get; init; }
    public IReadOnlyList<SqlParameterValue> Parameters { get; init; } = [];
    public int MaxRows { get; init; } = 10_000;
    public int CommandTimeoutSeconds { get; init; } = 60;
}

public sealed record QueryResultSet(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated);

public sealed record QueryResult(
    IReadOnlyList<QueryResultSet> ResultSets,
    IReadOnlyList<string> Messages,
    string? Error,
    TimeSpan Elapsed)
{
    public bool Success => Error is null;
}

public sealed class ReadOnlyViolationException(IReadOnlyList<SqlViolation> violations)
    : Exception("Скрипт не прошёл проверку «только чтение».")
{
    public IReadOnlyList<SqlViolation> Violations { get; } = violations;
}

/// <summary>
/// Выполняет скрипт в транзакции, которая всегда откатывается. Проверку «только чтение»
/// делает сам, чтобы её нельзя было обойти со стороны вызывающего кода.
/// </summary>
public static class QueryRunner
{
    public static async Task<QueryResult> RunAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var analysis = ReadOnlyValidator.Analyze(request.Sql);
        if (!analysis.IsAllowed)
            throw new ReadOnlyViolationException(analysis.Violations);

        var values = request.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var missing = analysis.Parameters.Where(p => !values.ContainsKey(p)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Не заданы параметры: {string.Join(", ", missing)}.");

        // Преобразуем значения до подключения, чтобы ошибка ввода не стоила похода на сервер.
        foreach (var value in values.Values)
            value.ToSqlParameter();

        var resultSets = new List<QueryResultSet>();
        var messages = new List<string>();
        string? error = null;
        var watch = Stopwatch.StartNew();

        // Пул выключен: временные таблицы и настройки сессии не должны переживать запуск.
        var connectionString = new SqlConnectionStringBuilder(request.ConnectionString) { Pooling = false }.ConnectionString;

        try
        {
            await using var connection = new SqlConnection(connectionString);
            connection.InfoMessage += (_, e) => messages.Add(e.Message);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                foreach (var batch in analysis.Batches)
                {
                    var truncated = await RunBatchAsync(
                        connection, transaction, batch, values, request, resultSets, cancellationToken);
                    if (truncated)
                    {
                        messages.Add($"Достигнут лимит {request.MaxRows} строк: выполнение остановлено, остальные результаты не получены.");
                        break;
                    }
                }
            }
            finally
            {
                await RollbackAsync(transaction);
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is SqlException or InvalidOperationException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SqlException ex)
        {
            error = ex.Number == -2
                ? $"Превышен таймаут ({request.CommandTimeoutSeconds} с)."
                : $"Ошибка SQL {ex.Number}, строка {ex.LineNumber}: {ex.Message}";
        }

        return new QueryResult(resultSets, messages, error, watch.Elapsed);
    }

    private static async Task<bool> RunBatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ScriptBatch batch,
        IReadOnlyDictionary<string, SqlParameterValue> values,
        QueryRequest request,
        List<QueryResultSet> resultSets,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(batch.Text, connection, transaction)
        {
            CommandTimeout = request.CommandTimeoutSeconds
        };
        // Только используемые пакетом: с параметрами он идёт через sp_executesql,
        // и созданные в нём #таблицы не видны следующим пакетам.
        foreach (var name in batch.Parameters)
            command.Parameters.Add(values[name].ToSqlParameter());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            if (reader.FieldCount == 0)
                continue;

            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => string.IsNullOrEmpty(reader.GetName(i)) ? $"(столбец {i + 1})" : reader.GetName(i))
                .ToList();
            var rows = new List<object?[]>();
            var truncated = false;

            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= request.MaxRows)
                {
                    truncated = true;
                    break;
                }

                rows.Add(ReadRow(reader));
            }

            resultSets.Add(new QueryResultSet(columns, rows, truncated));
            if (truncated)
            {
                // Без отмены закрытие reader дочитало бы всю выборку с сервера.
                command.Cancel();
                return true;
            }
        } while (await reader.NextResultAsync(cancellationToken));

        return false;
    }

    private static object?[] ReadRow(SqlDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            try
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            catch (Exception ex) when (ex is not SqlException and not OperationCanceledException)
            {
                // Например geography/hierarchyid без своих сборок или decimal вне диапазона .NET.
                row[i] = $"<не удалось прочитать {reader.GetDataTypeName(i)}>";
            }
        }

        return row;
    }

    private static async Task RollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Сервер уже откатил транзакцию (XACT_ABORT, обрыв связи); закрытие соединения откатит остаток.
        }
    }
}
