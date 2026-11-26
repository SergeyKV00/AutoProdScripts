using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Core.Tests;

public sealed class ScriptLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aps-lib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static LibraryScript Script(string name, string group = "") =>
        new() { Name = name, Group = group, Sql = "SELECT @id" };

    [Fact]
    public void SurvivesReload()
    {
        var library = new ScriptLibrary(_directory);
        var script = Script("  Поиск заявки ", @" Заявки \ Больничные / ");
        script.Description = "Ищет по номеру";
        script.Tags = ["б/л", "Б/Л", " статус "];
        script.Parameters = [new ScriptParameter { Name = "@id", Label = "Номер", Kind = SqlParameterKind.BigInt }];
        var saved = library.Save(script);
        library.SetFavorite(saved.Id, true);
        library.MarkRun(saved.Id);

        var reloaded = new ScriptLibrary(_directory);

        var loaded = Assert.Single(reloaded.GetAll());
        Assert.Equal("Поиск заявки", loaded.Name);
        Assert.Equal("Заявки/Больничные", loaded.Group);
        Assert.Equal(["б/л", "статус"], loaded.Tags);
        Assert.Equal(SqlParameterKind.BigInt, Assert.Single(loaded.Parameters).Kind);
        Assert.True(reloaded.IsFavorite(loaded.Id));
        Assert.NotNull(reloaded.LastRun(loaded.Id));
        var file = File.ReadAllText(Directory.GetFiles(Path.Combine(_directory, "scripts")).Single());
        Assert.Contains("Поиск заявки", file);
        Assert.DoesNotContain("Favorite", file);
    }

    [Fact]
    public void NameIsUniqueInsideGroupOnly()
    {
        var library = new ScriptLibrary(_directory);
        library.Save(Script("A", "g"));

        Assert.Throws<InvalidOperationException>(() => library.Save(Script("a", "G")));
        library.Save(Script("A", "other"));
        Assert.Equal(2, library.GetAll().Count);
    }

    [Fact]
    public void ReturnedScriptsAreCopies()
    {
        var library = new ScriptLibrary(_directory);
        var script = Script("A");
        script.Tags = ["x"];
        var id = library.Save(script).Id;

        library.Find(id)!.Tags.Add("y");

        Assert.Equal(["x"], library.Find(id)!.Tags);
    }

    [Fact]
    public void DuplicateAndDelete()
    {
        var library = new ScriptLibrary(_directory);
        var id = library.Save(Script("A")).Id;
        library.SetFavorite(id, true);

        var copy = library.Duplicate(id);
        library.Delete(id);

        Assert.Equal("A (2)", copy.Name);
        Assert.False(library.IsFavorite(id));
        Assert.Equal(copy.Id, Assert.Single(new ScriptLibrary(_directory).GetAll()).Id);
    }

    [Fact]
    public void BrokenAndNewerFilesAreReportedAndKept()
    {
        var scripts = Directory.CreateDirectory(Path.Combine(_directory, "scripts")).FullName;
        File.WriteAllText(Path.Combine(scripts, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(scripts, "nulls.json"), """{"Name":null}""");
        File.WriteAllText(Path.Combine(scripts, "new.json"), """{"Version":99,"Name":"x"}""");

        var library = new ScriptLibrary(_directory);

        Assert.Empty(library.GetAll());
        Assert.Equal(3, library.LoadErrors.Count);
        Assert.Equal(3, Directory.GetFiles(scripts).Length);
    }
}

public class LibrarySearchTests
{
    private static LibraryEntry Entry(string name, string description = "", string sql = "", string connection = "Lis",
        string database = "LIS_test", params string[] tags) =>
        new(new LibraryScript { Name = name, Description = description, Sql = sql, Tags = tags.ToList() }, connection, database);

    private static readonly LibraryEntry[] Entries =
    [
        Entry("В тексте", sql: "SELECT * FROM dbo.SickList"),
        Entry("В описании", description: "Проверка SickList перед закрытием", tags: "отчёт"),
        Entry("SickList по ИИН", connection: "Hospital", database: "Hospital_test")
    ];

    private static IEnumerable<string> Names(IEnumerable<LibraryEntry> found) => found.Select(e => e.Script.Name);

    [Fact]
    public void RanksNameThenDescriptionThenSql() =>
        Assert.Equal(["SickList по ИИН", "В описании", "В тексте"], Names(LibrarySearch.Find(Entries, "sicklist")));

    [Fact]
    public void RequiresAllWordsAndIgnoresYo()
    {
        Assert.Equal(["В описании"], Names(LibrarySearch.Find(Entries, "закрытием отчет")));
        Assert.Empty(LibrarySearch.Find(Entries, "закрытием ИИН"));
    }

    [Fact]
    public void FiltersByDatabaseConnectionAndTag()
    {
        Assert.Equal(["SickList по ИИН"], Names(LibrarySearch.Find(Entries, "hospital_test")));
        Assert.Equal(["SickList по ИИН"], Names(LibrarySearch.Find(Entries, null, connectionName: "hospital")));
        Assert.Equal(["В описании"], Names(LibrarySearch.Find(Entries, "", tag: "Отчёт")));
        Assert.Equal(3, LibrarySearch.Find(Entries, "  ").Count);
    }
}
