using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Explorer;
using AutoProdScripts.Core.Sql;
using AutoProdScripts.Core.Workspace;

namespace AutoProdScripts.Core.Tests;

public sealed class SqlGeneratorTests
{
    private static readonly DbColumnInfo[] Columns =
    [
        new("Id", "int", IsNullable: false, IsIdentity: true, IsKey: true),
        new("Name", "nvarchar(50)"),
        new("Total", "decimal(18, 2)", IsComputed: true),
        new("Код ]x", "int")
    ];

    [Theory]
    [InlineData("Users", "[Users]")]
    [InlineData("a]b", "[a]]b]")]
    public void QuotesNames(string name, string expected) => Assert.Equal(expected, SqlGenerator.Quote(name));

    [Fact]
    public void FullNameIncludesSchemaAndDatabase()
    {
        Assert.Equal("[dbo].[Users]", SqlGenerator.FullName("dbo", "Users"));
        Assert.Equal("[Db].[dbo].[Users]", SqlGenerator.FullName("dbo", "Users", "Db"));
        Assert.Equal("[Db]..[Users]", SqlGenerator.FullName(null, "Users", "Db"));
    }

    [Fact]
    public void SelectPassesReadOnlyCheck()
    {
        var sql = SqlGenerator.Select("dbo", "Users", Columns);
        Assert.Contains("TOP (1000)", sql);
        Assert.Contains("[Код ]]x]", sql);
        Assert.True(ReadOnlyValidator.Analyze(sql).IsAllowed);
        Assert.True(ReadOnlyValidator.Analyze(SqlGenerator.Structure("dbo", "Users'; DROP TABLE x; --")).IsAllowed);
    }

    [Fact]
    public void ExportSelectHasNoTop() =>
        Assert.DoesNotContain("TOP", SqlGenerator.Select("dbo", "Users", [], top: null));

    [Fact]
    public void ModifyingStatementsAreTextOnlyAndBlocked()
    {
        var insert = SqlGenerator.Insert("dbo", "Users", Columns);
        Assert.DoesNotContain("[Id]", insert);
        Assert.DoesNotContain("[Total]", insert);
        Assert.Contains("@Name", insert);
        Assert.Contains("@Код__x", insert);

        var update = SqlGenerator.Update("dbo", "Users", Columns);
        Assert.Contains("WHERE [Id] = @Id", update);
        Assert.DoesNotContain("[Id] = @Id,", update);

        var delete = SqlGenerator.Delete("dbo", "Users", Columns);
        Assert.Contains("WHERE [Id] = @Id", delete);

        foreach (var sql in new[] { insert, update, delete })
            Assert.False(ReadOnlyValidator.Analyze(sql).IsAllowed);
    }

    [Fact]
    public void DeleteWithoutKeyMatchesNothing() =>
        Assert.Contains("1 = 0", SqlGenerator.Delete("dbo", "Log", [new DbColumnInfo("Text", "nvarchar(max)")]));

    [Fact]
    public void ExecuteListsParameters()
    {
        var sql = SqlGenerator.Execute("dbo", "Report", [new("@from", "date"), new("@count", "int", IsOutput: true)]);
        Assert.Contains("@from = @from", sql);
        Assert.Contains("@count = @count OUTPUT", sql);
    }

    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 20, 0, 0, "varchar(20)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18, 2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void FormatsTypes(string type, int length, int precision, int scale, string expected) =>
        Assert.Equal(expected, SqlGenerator.FormatType(type, length, precision, scale));

    [Theory]
    [InlineData("Users", new[] { "Users" })]
    [InlineData("dbo.Users", new[] { "dbo", "Users" })]
    [InlineData("[My Db].[dbo].[a]]b]", new[] { "My Db", "dbo", "a]b" })]
    [InlineData("Db..Users", new[] { "Db", "", "Users" })]
    [InlineData("srv.Db.dbo.Users", new[] { "Db", "dbo", "Users" })]
    public void ParsesNames(string text, string[] expected) => Assert.Equal(expected, SqlGenerator.ParseName(text));

    [Theory]
    [InlineData("@id")]
    [InlineData("a b")]
    [InlineData("[unclosed")]
    [InlineData("dbo.")]
    public void RejectsNonNames(string text) => Assert.Null(SqlGenerator.ParseName(text));
}

public sealed class ExplorerStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aps-explorer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void DefaultsToConnectionsView() => Assert.Equal(ExplorerView.Connections, new ExplorerStateStore(_directory).Load().View);

    [Fact]
    public void SurvivesReload()
    {
        var id = Guid.NewGuid();
        new ExplorerStateStore(_directory).Save(new ExplorerState
        {
            View = ExplorerView.Library,
            HideSystemObjects = false,
            HiddenKinds = [DbObjectKind.User, DbObjectKind.User],
            Expanded = ["c:1", "c:1", " "],
            Connected = [id, Guid.Empty]
        });

        var loaded = new ExplorerStateStore(_directory).Load();
        Assert.Equal(ExplorerView.Library, loaded.View);
        Assert.False(loaded.HideSystemObjects);
        Assert.Equal([DbObjectKind.User], loaded.HiddenKinds);
        Assert.Equal(["c:1"], loaded.Expanded);
        Assert.Equal([id], loaded.Connected);
    }

    [Fact]
    public void BrokenFileGivesDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "explorer.json"), "[не json");
        Assert.True(new ExplorerStateStore(_directory).Load().HideSystemObjects);
    }
}

public sealed class ConnectionGroupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aps-groups-" + Guid.NewGuid().ToString("N"));

    private sealed class NoSecrets : ISecretStore
    {
        public bool TrySave(string key, string secret) => true;
        public string? Load(string key) => null;
        public void Delete(string key) { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void OldEnvironmentBecomesGroup()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "connections.json"), """
            [
              { "Name": "a", "Server": "s1", "Environment": "Test" },
              { "Name": "b", "Server": "s2", "Environment": "Prod" },
              { "Name": "c", "Server": "s3", "Environment": "Dev", "Group": "Своя" },
              { "Name": "d", "Server": "s4" }
            ]
            """);

        var store = new ConnectionStore(_directory, new NoSecrets());

        var groups = store.GetAll().ToDictionary(p => p.Name, p => p.Group);
        Assert.Equal("TEST", groups["a"]);
        Assert.Equal("PROD", groups["b"]);
        Assert.Equal("Своя", groups["c"]);
        Assert.True(string.IsNullOrEmpty(groups["d"]));
        Assert.DoesNotContain("Environment", File.ReadAllText(Path.Combine(_directory, "connections.json")));
    }

    [Fact]
    public void RenamesAndMovesGroups()
    {
        var store = new ConnectionStore(_directory, new NoSecrets());
        var a = new DbConnectionProfile { Name = "a", Server = "s", Group = @" Регионы \ Астана " };
        var b = new DbConnectionProfile { Name = "b", Server = "s", Group = "Регионы" };
        store.Save(a, null);
        store.Save(b, null);
        Assert.Equal(["Регионы", "Регионы/Астана"], store.Groups());

        store.RenameGroup("Регионы", "Кластеры");
        Assert.Equal(["Кластеры", "Кластеры/Астана"], store.Groups());

        store.MoveToGroup(a.Id, "");
        store.Rename(b.Id, "b2");
        Assert.Equal(["Кластеры"], store.Groups());
        Assert.Contains(store.GetAll(), p => p.Name == "b2");
    }

    [Fact]
    public void PortGoesIntoDataSource()
    {
        Assert.Equal("host,1500", ConnectionStringFactory.DataSource(new DbConnectionProfile { Server = "host", Port = 1500 }));
        Assert.Equal("host,1433", ConnectionStringFactory.DataSource(new DbConnectionProfile { Server = "host,1433", Port = 1500 }));
        Assert.Throws<InvalidOperationException>(() =>
            new ConnectionStore(_directory, new NoSecrets()).Save(new DbConnectionProfile { Name = "x", Server = "s", Port = 70000 }, null));
    }
}
