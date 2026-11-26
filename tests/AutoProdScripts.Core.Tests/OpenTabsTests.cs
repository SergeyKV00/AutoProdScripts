using AutoProdScripts.Core.Workspace;

namespace AutoProdScripts.Core.Tests;

public sealed class OpenTabsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aps-tabs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SurvivesReload()
    {
        var store = new OpenTabsStore(_directory);
        var scriptId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        store.Save(new OpenTabsState
        {
            Active = 1,
            Tabs =
            [
                new OpenTabState { ScriptId = scriptId, ConnectionId = connectionId, ConnectionName = "Тест" },
                new OpenTabState { Name = "Черновик", Text = "SELECT 1", Regional = true, RegionDatabase = "KMIS", Regions = ["ast", "AST", " "] }
            ]
        });

        var loaded = new OpenTabsStore(_directory).Load();

        Assert.Equal(1, loaded.Active);
        Assert.Equal(2, loaded.Tabs.Count);
        Assert.Equal(scriptId, loaded.Tabs[0].ScriptId);
        Assert.Null(loaded.Tabs[0].Text);
        Assert.Equal(connectionId, loaded.Tabs[0].ConnectionId);
        Assert.Equal("Черновик", loaded.Tabs[1].Name);
        Assert.Equal("SELECT 1", loaded.Tabs[1].Text);
        Assert.True(loaded.Tabs[1].Regional);
        Assert.Equal(["ast"], loaded.Tabs[1].Regions);
    }

    [Fact]
    public void MissingOrBrokenFileGivesNoTabs()
    {
        Assert.Empty(new OpenTabsStore(_directory).Load().Tabs);

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "open-tabs.json"), "{ не json");
        Assert.Empty(new OpenTabsStore(_directory).Load().Tabs);
    }

    [Fact]
    public void NewerVersionIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "open-tabs.json"),
            """{ "Version": 99, "Tabs": [ { "Name": "x", "Text": "SELECT 1" } ] }""");

        Assert.Empty(new OpenTabsStore(_directory).Load().Tabs);
    }

    [Fact]
    public void NormalizeDropsEmptyDraftsAndExtraChains()
    {
        var first = Guid.NewGuid();
        var state = OpenTabsStore.Normalize(new OpenTabsState
        {
            Active = 10,
            Tabs =
            [
                new OpenTabState { Name = "Пустой черновик" },
                new OpenTabState { ChainId = first, ScriptId = Guid.NewGuid(), Text = "SELECT 1" },
                new OpenTabState { ChainId = Guid.NewGuid() },
                new OpenTabState { Text = "SELECT 2", Regions = null! }
            ]
        });

        Assert.Equal(2, state.Tabs.Count);
        Assert.Equal(first, state.Tabs[0].ChainId);
        Assert.Null(state.Tabs[0].ScriptId);
        Assert.Null(state.Tabs[0].Text);
        Assert.Equal("SELECT 2", state.Tabs[1].Text);
        Assert.Empty(state.Tabs[1].Regions);
        Assert.Equal(1, state.Active);
    }
}
