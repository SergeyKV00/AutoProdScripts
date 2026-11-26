using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Core.Tests;

public class DatabaseChoiceTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Employees")]
    [InlineData("SELECT * FROM Employees e JOIN Common.dbo.Types t ON t.Id = e.TypeId")]
    [InlineData("WITH c AS (SELECT * FROM dbo.Employees) SELECT * FROM c")]
    public void ReportsTablesWithoutDatabase(string sql) => Assert.True(DatabaseChoice.IsMissing(sql));

    [Theory]
    [InlineData("USE Common; SELECT * FROM dbo.Employees")]
    [InlineData("SELECT * FROM Common.dbo.Employees")]
    [InlineData("SELECT * FROM Common..Employees")]
    [InlineData("SELECT * FROM #people")]
    [InlineData("SELECT name FROM sys.databases")]
    [InlineData("WITH c AS (SELECT * FROM Common.dbo.Employees) SELECT * FROM c")]
    [InlineData("SELECT 1")]
    [InlineData("не разбирается")]
    public void AcceptsScriptsThatChooseDatabase(string sql) => Assert.False(DatabaseChoice.IsMissing(sql));

    [Fact]
    public void ConnectionWithoutDatabaseIsClusterLevel()
    {
        var cluster = ConnectionStringParser.Parse("Data Source=srv;Integrated Security=True").Connections[0].Profile;
        var database = ConnectionStringParser.Parse("Data Source=srv;Initial Catalog=db;Integrated Security=True").Connections[0].Profile;

        Assert.True(cluster.IsClusterLevel);
        Assert.False(database.IsClusterLevel);
    }
}
