using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Core.Tests;

public sealed class ExchangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aps-exchange-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ScriptLibrary Library(string name) => new(Path.Combine(_root, name));

    private static (LibraryScript Script, LibraryChain Chain) Seed(ScriptLibrary library)
    {
        var script = library.Save(new LibraryScript
        {
            Name = "Поиск", Group = "СУР", Sql = "SELECT @iin", ConnectionId = Guid.NewGuid(), ConnectionName = "SUR",
            Parameters = [new ScriptParameter { Name = "@iin", Label = "ИИН", DefaultValue = "secret-default" }]
        });
        library.SetFavorite(script.Id, true);
        var chain = library.SaveChain(new LibraryChain
        {
            Name = "Проверка",
            Parameters = [new ScriptParameter { Name = "@iin", DefaultValue = "secret-chain" }],
            Steps =
            [
                new ChainStep { ScriptId = script.Id },
                new ChainStep
                {
                    ConnectionId = Guid.NewGuid(), ConnectionName = "MD",
                    Inline = new LibraryScript
                    {
                        Sql = "SELECT 2",
                        Parameters = [new ScriptParameter { Name = "@x", DefaultValue = "secret-inline" }]
                    }
                }
            ]
        });
        return (script, chain);
    }

    private static Dictionary<Guid, ImportAction> All(LibraryPackage package, ImportAction action) =>
        package.Items.ToDictionary(i => i.Id, _ => action);

    private static MappedConnection Same(string name) => new(null, name);

    [Fact]
    public void ChainBringsItsScriptsWithoutPrivateData()
    {
        var library = Library("a");
        var (script, chain) = Seed(library);

        var export = LibraryExchange.Export(library, [chain.Id]);
        var json = LibraryExchange.Serialize(export.Package);

        Assert.Equal(script.Id, Assert.Single(export.Package.Scripts).Id);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain(script.ConnectionId.ToString()!, json);
        Assert.DoesNotContain(chain.Steps[1].ConnectionId.ToString()!, json);
        Assert.DoesNotContain("Favorite", json);
        Assert.Equal(["MD", "SUR"], export.Package.ConnectionNames());
        Assert.Empty(export.Warnings);
        // Экспорт не меняет саму библиотеку.
        Assert.Equal("secret-default", library.Find(script.Id)!.Parameters[0].DefaultValue);
    }

    [Fact]
    public void WarnsAboutNumbersLikeIin()
    {
        var library = Library("a");
        var id = library.Save(new LibraryScript { Name = "С номером", Sql = "SELECT 1 WHERE x = '000000000000'" }).Id;

        Assert.Contains("С номером", Assert.Single(LibraryExchange.Export(library, [id]).Warnings));
    }

    [Fact]
    public void ImportsIntoEmptyLibraryWithMappedConnections()
    {
        var source = Library("a");
        var (script, chain) = Seed(source);
        var json = LibraryExchange.Serialize(LibraryExchange.Export(source, [chain.Id]).Package);
        var target = Library("b");
        var mine = Guid.NewGuid();

        var package = LibraryExchange.Parse(json);
        Assert.All(LibraryExchange.Preview(package, target), c => Assert.Equal(ImportState.New, c.State));
        var result = LibraryExchange.Import(package, target, All(package, ImportAction.Add),
            name => name == "SUR" ? new MappedConnection(mine, "My_SUR") : new MappedConnection(null, name));

        Assert.Equal((2, 0, 0), (result.Added, result.Updated, result.Skipped));
        Assert.Equal((mine, "My_SUR"), (target.Find(script.Id)!.ConnectionId!.Value, target.Find(script.Id)!.ConnectionName));
        Assert.Equal("MD", target.FindChain(chain.Id)!.Steps[1].ConnectionName);
        Assert.All(LibraryExchange.Preview(package, target), c => Assert.Equal(ImportState.Identical, c.State));
    }

    [Fact]
    public void UpdateKeepsLocalDefaultsAndCopyRelinksChain()
    {
        var library = Library("a");
        var (script, chain) = Seed(library);
        var package = LibraryExchange.Parse(LibraryExchange.Serialize(LibraryExchange.Export(library, [chain.Id]).Package));
        package.Scripts[0].Sql = "SELECT @iin, 1";
        Assert.Equal(ImportState.Different,
            LibraryExchange.Preview(package, library).Single(c => c.Item is LibraryScript).State);

        LibraryExchange.Import(package, library, new Dictionary<Guid, ImportAction> { [script.Id] = ImportAction.Update }, Same);

        Assert.Equal("SELECT @iin, 1", library.Find(script.Id)!.Sql);
        Assert.Equal("secret-default", library.Find(script.Id)!.Parameters[0].DefaultValue);

        var copied = LibraryExchange.Import(package, library, All(package, ImportAction.Copy), Same);

        Assert.Equal(2, copied.Added);
        var newScript = library.GetAll().Single(s => s.Id != script.Id);
        var newChain = library.GetChains().Single(c => c.Id != chain.Id);
        Assert.Equal("Поиск (2)", newScript.Name);
        Assert.Equal("Проверка (2)", newChain.Name);
        Assert.Equal(newScript.Id, newChain.Steps[0].ScriptId);
        Assert.Equal(script.Id, library.FindChain(chain.Id)!.Steps[0].ScriptId);
    }

    [Fact]
    public void ChainWithChangedScriptCountsAsDifferent()
    {
        var library = Library("a");
        var (_, chain) = Seed(library);
        var package = LibraryExchange.Export(library, [chain.Id]).Package;
        package.Scripts[0].Sql = "SELECT 3";

        Assert.All(LibraryExchange.Preview(package, library), c => Assert.Equal(ImportState.Different, c.State));
    }

    [Fact]
    public void SkippedByDefault()
    {
        var library = Library("a");
        var (_, chain) = Seed(library);
        var package = LibraryExchange.Export(library, [chain.Id]).Package;
        var target = Library("b");

        var result = LibraryExchange.Import(package, target, new Dictionary<Guid, ImportAction>(), Same);

        Assert.Equal(2, result.Skipped);
        Assert.Empty(target.GetAll());
    }

    [Fact]
    public void MarksScriptsThatCannotRunAndStripsForeignDefaults()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            {"Format":"AutoProdScripts.Library","Version":1,"Chains":[],"Scripts":[
              {"Id":"{{id}}","Name":"Опасный","Sql":"DELETE FROM dbo.X","ConnectionId":"{{Guid.NewGuid()}}",
               "Parameters":[{"Name":"@a","DefaultValue":"чужое"}]}]}
            """;

        var package = LibraryExchange.Parse(json);

        Assert.NotNull(Assert.Single(LibraryExchange.Preview(package, Library("a"))).Problem);
        Assert.Null(package.Scripts[0].Parameters[0].DefaultValue);
        Assert.Null(package.Scripts[0].ConnectionId);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{}")]
    [InlineData("""{"Format":"other","Scripts":[],"Chains":[]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Version":99,"Scripts":[],"Chains":[]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Scripts":null,"Chains":[]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Scripts":[null],"Chains":[]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Scripts":[{"Name":null}],"Chains":[]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Scripts":[{"Tags":[null]}],"Chains":[]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Scripts":[],"Chains":[{"Steps":[null]}]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Scripts":[],"Chains":[{"Steps":[{"Inline":{"Parameters":[null]}}]}]}""")]
    [InlineData("""{"Format":"AutoProdScripts.Library","Scripts":[{"Id":"11111111-1111-1111-1111-111111111111"},{"Id":"11111111-1111-1111-1111-111111111111"}],"Chains":[]}""")]
    public void RejectsForeignOrBrokenFiles(string json) =>
        Assert.Throws<InvalidDataException>(() => LibraryExchange.Parse(json));
}
