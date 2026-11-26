using AutoProdScripts.Core.Execution;
using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Tests;

/// <summary>Проверки на настоящем SQL Server. Выполняются, только если доступен LocalDB.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public const string ConnectionString =
        @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=tempdb;Integrated Security=True;Encrypt=False;Connect Timeout=30";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            return true;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    });

    public LocalDbFactAttribute()
    {
        if (!Available.Value)
            Skip = "LocalDB недоступен";
    }
}

public class QueryRunnerTests
{
    private static Task<QueryResult> Run(
        string sql,
        int maxRows = 100,
        int timeout = 30,
        CancellationToken token = default,
        params SqlParameterValue[] parameters) =>
        QueryRunner.RunAsync(new QueryRequest
        {
            ConnectionString = LocalDbFactAttribute.ConnectionString,
            Sql = sql,
            MaxRows = maxRows,
            CommandTimeoutSeconds = timeout,
            Parameters = parameters
        }, token);

    private const string Slow =
        "SELECT COUNT_BIG(*) FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c";

    [Fact]
    public async Task RefusesWritesBeforeConnecting() =>
        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => QueryRunner.RunAsync(
            new QueryRequest { ConnectionString = "Data Source=nowhere.invalid", Sql = "DELETE FROM dbo.X" },
            default));

    [Fact]
    public async Task RequiresAllParameters() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("SELECT @a"));

    [LocalDbFact]
    public async Task PassesTypedParameters()
    {
        var result = await Run("SELECT @n + 1 AS n, @s AS s, @d AS d, @empty AS e", parameters:
        [
            new("@n", SqlParameterKind.Int, "41"),
            new("@s", SqlParameterKind.NVarChar, "Привет'; DROP TABLE x --"),
            new("@d", SqlParameterKind.Date, "31.01.2026"),
            new("@empty", SqlParameterKind.Decimal, null)
        ]);

        Assert.True(result.Success, result.Error);
        var row = Assert.Single(Assert.Single(result.ResultSets).Rows);
        Assert.Equal(42, row[0]);
        Assert.Equal("Привет'; DROP TABLE x --", row[1]);
        Assert.Equal(new DateTime(2026, 1, 31), row[2]);
        Assert.Null(row[3]);
    }

    [LocalDbFact]
    public async Task TempTablesSurviveGoAndSeveralResultSetsAreReturned()
    {
        var result = await Run("""
            CREATE TABLE #t (Id int);
            INSERT #t VALUES (1), (2);
            GO
            SELECT Id FROM #t WHERE Id >= @min;
            SELECT COUNT(*) AS Total FROM #t;
            PRINT N'готово';
            """, parameters: [new("@min", SqlParameterKind.Int, "2")]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.ResultSets.Count);
        Assert.Equal(2, result.ResultSets[0].Rows.Single()[0]);
        Assert.Contains("готово", result.Messages);
    }

    [LocalDbFact]
    public async Task LimitsRows()
    {
        var result = await Run("SELECT TOP (5000) a.object_id FROM sys.all_columns a CROSS JOIN sys.all_columns b", maxRows: 10);

        Assert.True(result.Success, result.Error);
        var set = Assert.Single(result.ResultSets);
        Assert.Equal(10, set.Rows.Count);
        Assert.True(set.Truncated);
    }

    [LocalDbFact]
    public async Task ExactLimitIsNotTruncated()
    {
        var result = await Run("SELECT 1 AS a UNION ALL SELECT 2", maxRows: 2);

        Assert.False(Assert.Single(result.ResultSets).Truncated);
    }

    [LocalDbFact]
    public async Task ReportsServerErrorAndKeepsEarlierResults()
    {
        var result = await Run("SELECT 1 AS a; SELECT 1/0 AS b");

        Assert.False(result.Success);
        Assert.Contains("8134", result.Error);
        Assert.NotEmpty(result.ResultSets);
    }

    [LocalDbFact]
    public async Task ReportsTimeout()
    {
        var result = await Run(Slow, timeout: 1);

        Assert.Contains("таймаут", result.Error);
    }

    [LocalDbFact]
    public async Task CanBeCancelled()
    {
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Slow, timeout: 120, token: source.Token));
    }
}

public class ResultFormatterTests
{
    [Fact]
    public void CsvEscapesAndNeutralizesFormulas()
    {
        var csv = ResultFormatter.ToCsv(new QueryResultSet(
            ["a", "b", "c"],
            [["=1+1", "x;\"y\"", null], ["-5", -5, "@cmd"]],
            false));

        Assert.Equal("a;b;c\r\n'=1+1;\"x;\"\"y\"\"\";\r\n-5;-5;'@cmd\r\n", csv);
    }
}
