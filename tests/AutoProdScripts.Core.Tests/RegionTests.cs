using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Regions;
using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Tests;

public sealed class RegionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aps-reg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("KMIS_{region}", true)]
    [InlineData("kmis_{REGION}", true)]
    [InlineData("KMIS", true)]
    [InlineData("  ", false)]
    [InlineData("KMIS ast", false)]
    [InlineData("KMIS_{region}; DROP", false)]
    [InlineData("a]b_{region}", false)]
    public void ValidatesDatabaseTemplate(string template, bool valid) =>
        Assert.Equal(valid, RegionDatabase.Validate(template) is null);

    [Fact]
    public void ResolvesDatabaseName() => Assert.Equal("KMIS_vko", RegionDatabase.Resolve(" KMIS_{region} ", "vko"));

    [Theory]
    [InlineData("KMIS", "KMIS_vko")]
    [InlineData("KMIS_", "KMIS_vko")]
    [InlineData("KMIS-", "KMIS-vko")]
    [InlineData("{region}_KMIS", "vko_KMIS")]
    public void AppendsRegionWhenPlaceholderIsOmitted(string template, string database) =>
        Assert.Equal(database, RegionDatabase.Resolve(template, "vko"));

    [Fact]
    public void ParsesPastedList()
    {
        var result = RegionStore.ParseList("ast; Астана; Кластер A\r\n\r\nvko\tКластер B\nплохая строка\n");

        Assert.Equal(["ast", "vko"], result.Regions.Select(r => r.Code));
        Assert.Equal("Астана", result.Regions[0].Name);
        Assert.Equal("Кластер B", result.Regions[1].ConnectionName);
        Assert.Contains("Строка 4", Assert.Single(result.Errors));
    }

    [Fact]
    public void ParsesListGroupedByCluster()
    {
        var result = RegionStore.ParseList("2 кластер:\r\n  • akt\r\n  • atr; Атырау\r\n\r\n3 кластер:\r\n- ala\r\nzko\r\nне код\r\n");

        Assert.Equal(["akt", "atr", "ala", "zko"], result.Regions.Select(r => r.Code));
        Assert.Equal(["2 кластер", "2 кластер", "3 кластер", "3 кластер"], result.Regions.Select(r => r.ConnectionName));
        Assert.Equal("Атырау", result.Regions[1].Name);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void StoreSurvivesReloadAndRejectsBadData()
    {
        var store = new RegionStore(_directory);
        store.Save([new Region { Code = "ast", Name = "Астана", ConnectionName = "A" }]);

        Assert.Equal("Астана", Assert.Single(new RegionStore(_directory).GetAll()).Name);
        Assert.Throws<InvalidOperationException>(() => store.Save(
            [new Region { Code = "ast", ConnectionName = "A" }, new Region { Code = "AST", ConnectionName = "B" }]));
        Assert.Throws<InvalidOperationException>(() => store.Save([new Region { Code = "а б", ConnectionName = "A" }]));
        Assert.Throws<InvalidOperationException>(() => store.Save([new Region { Code = "ast" }]));
        Assert.Single(store.GetAll());
    }

    private static QueryResult Result(string column, params object?[] values) =>
        new([new QueryResultSet([column], values.Select(v => new[] { v }).ToList(), false)], [], null, TimeSpan.Zero);

    [Fact]
    public void MergeKeepsDifferentColumnsApartAndSkipsFailedRegions()
    {
        var merged = RegionRunner.Merge(
        [
            new RegionOutcome("a", Result("Id", 1, 2), null),
            new RegionOutcome("b", Result("Id", 3), null),
            new RegionOutcome("c", Result("Other", 9), null),
            new RegionOutcome("d", Result("Id", 100), "обрыв на середине")
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(["Id"], merged[0].Set.Columns);
        Assert.Equal([[1], [2], [3]], merged[0].Set.Rows);
        Assert.Equal(["a", "a", "b"], merged[0].Set.RowGroups);
        // Служебный регион не попадает в выгрузку.
        Assert.Equal("Id\r\n1\r\n2\r\n3\r\n", ResultFormatter.ToCsv(merged[0].Set));
        Assert.Equal(["c"], merged[1].Regions);
    }

    private static RegionTarget Target(string code, string database) => new(
        code,
        new SqlConnectionStringBuilder(LocalDbFactAttribute.ConnectionString) { InitialCatalog = database, ConnectTimeout = 15 }.ConnectionString,
        "localdb",
        30);

    [LocalDbFact]
    public async Task RunsEveryRegionAndKeepsGoingAfterFailure()
    {
        var outcomes = await RegionRunner.RunAsync(
            [Target("one", "tempdb"), Target("bad", "no_such_database_x"), Target("two", "master"),
                new RegionTarget("nocluster", "", "", 30, "Нет кластера.")],
            "SELECT DB_NAME() AS Db, @x AS X", [new SqlParameterValue("@x", SqlParameterKind.Int, "5")], 100, null, default);

        Assert.Equal(["one", "bad", "two", "nocluster"], outcomes.Select(o => o.Code));
        Assert.Equal([true, false, true, false], outcomes.Select(o => o.Success));
        var merged = Assert.Single(RegionRunner.Merge(outcomes));
        Assert.Equal([["tempdb", 5], ["master", 5]], merged.Set.Rows);
        Assert.Equal(["one", "two"], merged.Set.RowGroups);
    }

    [LocalDbFact]
    public async Task LimitsRowsPerRegion()
    {
        var outcomes = await RegionRunner.RunAsync(
            [Target("one", "tempdb"), Target("two", "master")],
            "SELECT TOP (10) object_id FROM sys.all_objects", [], 3, null, default);

        var merged = Assert.Single(RegionRunner.Merge(outcomes));
        Assert.Equal(6, merged.Set.Rows.Count);
        Assert.Equal(["one", "two"], merged.Truncated);
    }

    [Fact]
    public async Task RefusesWritesBeforeAnyRegion() =>
        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => RegionRunner.RunAsync(
            [new RegionTarget("a", "Data Source=nowhere.invalid", "s", 5)], "DELETE FROM dbo.X", [], 10, null, default));
}
