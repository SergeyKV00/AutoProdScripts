using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Connections;

public sealed record ConnectionTestResult(string Server, string Database, bool CanWrite);

public static class ConnectionTester
{
    // Грубая оценка по ролям: отдельные GRANT на таблицы она не видит.
    private const string Query = """
        SELECT @@SERVERNAME, DB_NAME(),
               CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1
                      OR IS_ROLEMEMBER('db_owner') = 1
                      OR IS_ROLEMEMBER('db_datawriter') = 1 THEN 1 ELSE 0 END
        """;

    public static async Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(Query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ConnectionTestResult(
            reader.IsDBNull(0) ? builder.DataSource : reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2) == 1);
    }
}
