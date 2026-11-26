using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
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

    /// <summary>Временные таблицы, которые нужно создать и заполнить перед скриптом.</summary>
    public IReadOnlyList<InputTable> InputTables { get; init; } = [];
}

public sealed record InputTable(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<SqlColumnType?> Types,
    IReadOnlyList<object?[]> Rows);

/// <param name="RowGroups">
/// Служебная метка каждой строки (например регион). Не колонка: в таблицу, копирование и CSV не попадает,
/// по ней можно только сгруппировать.
/// </param>
public sealed record QueryResultSet(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    IReadOnlyList<SqlColumnType?>? Types = null,
    IReadOnlyList<string>? RowGroups = null);

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
public static partial class QueryRunner
{
    [GeneratedRegex(@"^#[\p{L}_][\p{L}\p{N}_]{0,100}$")]
    private static partial Regex TableNamePattern();

    public static bool IsValidTableName(string name) => TableNamePattern().IsMatch(name);
    public static async Task<QueryResult> RunAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var analysis = ReadOnlyValidator.Analyze(request.Sql);
        if (!analysis.IsAllowed)
            throw new ReadOnlyViolationException(analysis.Violations);

        var values = request.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var missing = analysis.Parameters.Where(p => !values.ContainsKey(p)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Не заданы параметры: {string.Join(", ", missing)}.");

        foreach (var table in request.InputTables)
            CheckInputTable(table);

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
                foreach (var table in request.InputTables)
                    await LoadTableAsync(connection, transaction, table, request.CommandTimeoutSeconds, cancellationToken);

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

    private static void CheckInputTable(InputTable table)
    {
        if (!IsValidTableName(table.Name))
            throw new InvalidOperationException($"Недопустимое имя временной таблицы: {table.Name}.");
        if (table.Columns.Count == 0)
            throw new InvalidOperationException($"{table.Name}: нет колонок.");
        if (table.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != table.Columns.Count)
            throw new InvalidOperationException($"{table.Name}: в результате есть колонки с одинаковыми именами.");
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (i >= table.Types.Count || table.Types[i] is null)
                throw new InvalidOperationException($"{table.Name}: колонку {table.Columns[i]} этого типа передать нельзя.");
        }
    }

    // Пишет только во временную таблицу своей сессии: имя проверено, транзакция потом откатывается.
    private static async Task LoadTableAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InputTable table,
        int timeout,
        CancellationToken cancellationToken)
    {
        var columns = table.Columns.Select((name, i) => $"[{name.Replace("]", "]]")}] {table.Types[i]!.Declaration} NULL");
        await using (var create = new SqlCommand(
                         $"CREATE TABLE {table.Name} ({string.Join(", ", columns)})", connection, transaction))
        {
            create.CommandTimeout = timeout;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        using var data = new DataTable();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var type = table.Rows.Select(r => r[i]).FirstOrDefault(v => v is not null)?.GetType() ?? typeof(string);
            data.Columns.Add($"c{i}", type);
        }

        foreach (var row in table.Rows)
            data.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());

        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = table.Name,
            BulkCopyTimeout = timeout
        };
        for (var i = 0; i < table.Columns.Count; i++)
            copy.ColumnMappings.Add(i, i);
        await copy.WriteToServerAsync(data, cancellationToken);
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
            var types = reader.GetColumnSchema().Select(SqlColumnType.From).ToList();
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

            resultSets.Add(new QueryResultSet(columns, rows, truncated, types));
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
