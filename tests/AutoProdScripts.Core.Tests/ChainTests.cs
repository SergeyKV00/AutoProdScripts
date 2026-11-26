using AutoProdScripts.Core.Chains;
using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Core.Tests;

public class ChainTests
{
    private readonly Dictionary<Guid, LibraryScript> _scripts = new();

    private ChainStep Step(string sql, bool stopWhenEmpty = true, string output = "", string empty = "",
        params ScriptParameter[] parameters)
    {
        var script = new LibraryScript { Name = "s" + _scripts.Count, Sql = sql, Parameters = parameters.ToList() };
        _scripts[script.Id] = script;
        return new ChainStep
        {
            ScriptId = script.Id,
            StopWhenEmpty = stopWhenEmpty,
            OutputTable = output,
            EmptyMessage = empty
        };
    }

    private static LibraryChain Chain(params ChainStep[] steps) => new() { Name = "c", Steps = steps.ToList() };

    private static ChainBinding From(ChainStep step, string parameter, string column) =>
        new() { Parameter = parameter, FromStep = step.Id, Source = column };

    private ChainPlan Plan(LibraryChain chain) => ChainPlan.Build(chain, id => _scripts.GetValueOrDefault(id));

    private Task<IReadOnlyList<ChainStepResult>> Run(
        LibraryChain chain,
        Dictionary<string, string?>? values = null,
        int maxRows = 100,
        Func<ChoiceRequest, Task<int?>>? choose = null) =>
        ChainRunner.RunAsync(new ChainRunRequest
        {
            Chain = chain,
            FindScript = id => _scripts.GetValueOrDefault(id),
            Connect = (_, _) => new StepConnection("local", LocalDbFactAttribute.ConnectionString, 30),
            Values = values ?? new(),
            MaxRows = maxRows,
            Choose = choose
        }, null, default);

    [Fact]
    public void SharedParametersAreAskedOnce()
    {
        var chain = Chain(
            Step("SELECT @MoId, @Iin", parameters: new ScriptParameter { Name = "@Iin", Kind = SqlParameterKind.VarChar }),
            Step("SELECT @moid, @iin, @extra"));

        var plan = Plan(chain);

        Assert.True(plan.IsValid, string.Join("; ", plan.Problems));
        Assert.Equal(["@MoId", "@Iin", "@extra"], plan.CommonParameters.Select(p => p.Name));
        Assert.Equal(SqlParameterKind.VarChar, plan.CommonParameters[1].Kind);
    }

    [Fact]
    public void BoundParametersAreNotAsked()
    {
        var first = Step("SELECT 1 AS Id", output: "#people");
        var second = Step("SELECT * FROM #people WHERE Id = @id AND Code = @other");
        second.Bindings = [From(first, "@id", "Id"), new ChainBinding { Parameter = "@other", Source = "@code" }];

        var plan = Plan(Chain(first, second));

        Assert.True(plan.IsValid, string.Join("; ", plan.Problems));
        Assert.Equal(["@code"], plan.CommonParameters.Select(p => p.Name));
        Assert.Equal([0], plan.Steps[1].TableSources);
    }

    [Fact]
    public void InlineScriptNeedsNoLibraryAndIsChecked()
    {
        var own = new ChainStep { Inline = new LibraryScript { Sql = "SELECT @a" } };
        var writes = new ChainStep { Inline = new LibraryScript { Name = "x", Sql = "DELETE FROM dbo.X" } };
        var empty = new ChainStep { Inline = new LibraryScript() };

        var plan = Plan(Chain(own));
        var bad = Plan(Chain(writes, empty));

        Assert.True(plan.IsValid, string.Join("; ", plan.Problems));
        Assert.Equal("Скрипт шага 1", plan.Steps[0].Script.Name);
        Assert.Equal(["@a"], plan.CommonParameters.Select(p => p.Name));
        Assert.Equal("", own.Inline!.Name);
        Assert.Equal(2, bad.Problems.Count);
    }

    [LocalDbFact]
    public async Task RunsInlineScripts()
    {
        var results = await Run(Chain(new ChainStep { Inline = new LibraryScript { Sql = "SELECT @a AS a" } }),
            new() { ["@a"] = "7" });

        Assert.Equal("7", results[0].Result!.ResultSets[0].Rows[0][0]);
    }

    [Fact]
    public void ReportsProblemsBeforeRunning()
    {
        var late = Step("SELECT 1 AS Id");
        var early = Step("SELECT @id");
        early.Bindings = [From(late, "@id", "Id")];
        var missing = new ChainStep { ScriptId = Guid.NewGuid() };
        var writes = Step("DELETE FROM dbo.X");
        var badName = Step("SELECT 1", output: "people");
        var output = Step("SELECT 1 AS a", output: "#t");
        var clash = Step("CREATE TABLE #t (a int)");

        var plan = Plan(Chain(early, late, missing, writes, badName, output, clash));

        Assert.Equal(5, plan.Problems.Count);
        Assert.False(Plan(Chain()).IsValid);
    }

    [Fact]
    public async Task RequiresCommonValuesBeforeConnecting() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Chain(Step("SELECT @a"))));

    [LocalDbFact]
    public async Task StopsWhenStepFindsNothing()
    {
        var chain = Chain(
            Step("SELECT 1 AS a WHERE @iin = '1'", empty: "Сотрудника нет в СУР"),
            Step("SELECT @iin AS b", empty: "Сотрудника нет в МД"));

        var found = await Run(chain, new() { ["@iin"] = "1" });
        var absent = await Run(chain, new() { ["@iin"] = "2" });

        Assert.All(found, r => Assert.Equal(ChainStepStatus.Succeeded, r.Status));
        Assert.Equal([ChainStepStatus.Empty, ChainStepStatus.Skipped], absent.Select(r => r.Status));
        Assert.Equal("Сотрудника нет в СУР", absent[0].Message);
    }

    [LocalDbFact]
    public async Task ContinuesAfterEmptyStepWhenAllowed()
    {
        var results = await Run(Chain(Step("SELECT 1 AS a WHERE 1 = 0", stopWhenEmpty: false), Step("SELECT 2 AS b")));

        Assert.Equal([ChainStepStatus.Empty, ChainStepStatus.Succeeded], results.Select(r => r.Status));
    }

    [LocalDbFact]
    public async Task PassesTypedValuesAndTables()
    {
        var first = Step("""
            SELECT CAST(5 AS bigint) AS Id, CAST('abc' AS varchar(10)) AS Code, CAST(12.50 AS decimal(10,2)) AS Sum,
                   CAST('2026-01-31 10:20:30.1234567' AS datetime2(7)) AS At, NEWID() AS Uid, CAST(0x0102 AS varbinary(8)) AS Bin,
                   CAST(NULL AS int) AS Nothing, N'Строка]' AS [имя]]колонки]
            """, output: "#source");
        var second = Step("""
            SELECT SQL_VARIANT_PROPERTY(@id, 'BaseType') AS IdType, SQL_VARIANT_PROPERTY(@code, 'BaseType') AS CodeType,
                   @id AS Id, @uid AS Uid;
            SELECT * FROM #source WHERE Code = 'abc' AND Id = @id;
            """);
        second.Bindings = [From(first, "@id", "Id"), From(first, "@code", "code"), From(first, "@uid", "Uid")];

        var results = await Run(Chain(first, second));

        Assert.True(results[1].Status == ChainStepStatus.Succeeded, results[1].Message);
        var source = results[0].Result!.ResultSets[0].Rows[0];
        var values = results[1].Result!.ResultSets[0].Rows[0];
        Assert.Equal("bigint", values[0]);
        Assert.Equal("varchar", values[1]);
        Assert.Equal(source[4], values[3]);
        Assert.Equal(source, results[1].Result!.ResultSets[1].Rows.Single());
    }

    [LocalDbFact]
    public async Task TransfersManyRowsIntoParameterizedScript()
    {
        var first = Step("SELECT TOP (500) object_id AS Id, name AS Name FROM sys.all_objects ORDER BY object_id", output: "#ids");
        var second = Step("SELECT COUNT(*) AS Total FROM #ids i JOIN sys.all_objects o ON o.object_id = i.Id AND o.name = i.Name WHERE @x = 1");

        var results = await Run(Chain(first, second), new() { ["@x"] = "1" }, maxRows: 1000);

        Assert.True(results[1].Status == ChainStepStatus.Succeeded, results[1].Message);
        Assert.Equal(500, results[1].Result!.ResultSets[0].Rows[0][0]);
    }

    [LocalDbFact]
    public async Task AsksToChooseOneOfSeveralValues()
    {
        var first = Step("SELECT v AS Id FROM (VALUES (10), (20), (20)) t(v) ORDER BY v");
        var second = Step("SELECT @id AS Id");
        second.Bindings = [From(first, "@id", "Id")];
        var chain = Chain(first, second);
        var offered = 0;

        var chosen = await Run(chain, choose: request =>
        {
            offered = request.Values.Count;
            return Task.FromResult<int?>(1);
        });
        var refused = await Run(chain);

        Assert.Equal(2, offered);
        Assert.Equal(20, chosen[1].Result!.ResultSets[0].Rows[0][0]);
        Assert.Equal(ChainStepStatus.Failed, refused[1].Status);
    }

    [LocalDbFact]
    public async Task DoesNotPassTruncatedData()
    {
        var results = await Run(Chain(
            Step("SELECT object_id FROM sys.all_objects", output: "#t"),
            Step("SELECT * FROM #t")), maxRows: 3);

        Assert.Equal(ChainStepStatus.Failed, results[1].Status);
        Assert.Contains("обрезан", results[1].Message);
    }

    [LocalDbFact]
    public async Task ServerErrorStopsChain()
    {
        var results = await Run(Chain(Step("SELECT 1/0 AS a"), Step("SELECT 1")));

        Assert.Equal([ChainStepStatus.Failed, ChainStepStatus.Skipped], results.Select(r => r.Status));
    }
}

public sealed class ChainStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aps-chain-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ChainsSurviveReloadAndShareFavorites()
    {
        var library = new ScriptLibrary(_directory);
        var script = library.Save(new LibraryScript { Name = "A", Sql = "SELECT 1" });
        var step = new ChainStep { ScriptId = script.Id, EmptyMessage = "нет", Bindings = [new ChainBinding { Parameter = "@a" }] };
        var chain = library.SaveChain(new LibraryChain { Name = "A", Group = "g", Steps = [step] });
        library.SetFavorite(chain.Id, true);
        library.MarkRun(chain.Id);

        var reloaded = new ScriptLibrary(_directory);

        var loaded = Assert.Single(reloaded.GetChains());
        Assert.Equal("нет", Assert.Single(loaded.Steps).EmptyMessage);
        Assert.True(reloaded.IsFavorite(loaded.Id));
        Assert.NotNull(reloaded.LastRun(loaded.Id));
        Assert.Equal(["g"], reloaded.Groups());
        Assert.Equal(chain.Id, Assert.Single(reloaded.ChainsUsing(script.Id)).Id);
        var embedded = reloaded.FindChain(chain.Id)!;
        embedded.Steps[0].Inline = new LibraryScript { Sql = "SELECT 2" };
        reloaded.SaveChain(embedded);
        Assert.Empty(reloaded.ChainsUsing(script.Id));
        Assert.Equal("SELECT 2", new ScriptLibrary(_directory).FindChain(chain.Id)!.Steps[0].Inline!.Sql);

        var copy = reloaded.DuplicateChain(chain.Id);
        reloaded.Delete(chain.Id);
        Assert.Equal("A (2)", copy.Name);
        Assert.Equal(copy.Id, Assert.Single(new ScriptLibrary(_directory).GetChains()).Id);
        Assert.Single(reloaded.GetAll());
    }
}
