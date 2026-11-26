using AutoProdScripts.Core.Connections;
using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Tests;

public class ConnectionStringParserTests
{
    private static ParsedConnection Single(string text)
    {
        var result = ConnectionStringParser.Parse(text);
        Assert.Empty(result.Errors);
        return Assert.Single(result.Connections);
    }

    [Fact]
    public void ParsesConfigFragmentWithProviderName()
    {
        var parsed = Single("""Data Source=srv01;Initial Catalog=Common_test;Persist Security Info=True;User ID=reader;Password=p@ss" providerName="System.Data.SqlClient""");

        Assert.Equal("srv01", parsed.Profile.Server);
        Assert.Equal("Common_test", parsed.Profile.Database);
        Assert.Equal("Common_test", parsed.Profile.Name);
        Assert.Equal(DbAuthMode.SqlLogin, parsed.Profile.AuthMode);
        Assert.Equal("reader", parsed.Profile.UserName);
        Assert.Equal("p@ss", parsed.Password);
        Assert.False(parsed.Profile.Encrypt);
        Assert.Equal(string.Empty, parsed.Profile.ExtraParameters);
    }

    [Fact]
    public void ParsesBlockOfAddElementsAndSkipsComments()
    {
        var result = ConnectionStringParser.Parse("""
            <connectionStrings>
              <!-- <add name="Old" connectionString="Data Source=old" /> -->
              <add name="MasterData" connectionString="Data Source=a;Initial Catalog=MasterData;Integrated Security=True" providerName="System.Data.SqlClient" />
              <add name="Lis"
                   connectionString="Data Source=b;Initial Catalog=LIS;User ID=u;Password=&quot;a;b&amp;c&quot;;MultiSubnetFailover=True" />
            </connectionStrings>
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(["MasterData", "Lis"], result.Connections.Select(c => c.Profile.Name));
        Assert.Equal(DbAuthMode.Windows, result.Connections[0].Profile.AuthMode);
        Assert.Null(result.Connections[0].Password);
        Assert.Equal("a;b&c", result.Connections[1].Password);
        Assert.Contains("Multi Subnet Failover=True", result.Connections[1].Profile.ExtraParameters);
        Assert.DoesNotContain("Password", result.Connections[1].Profile.ExtraParameters);
    }

    [Fact]
    public void ParsesJsonPairs()
    {
        var result = ConnectionStringParser.Parse("""
            "ConnectionStrings": {
              "Common": "Server=srv\\inst;Database=Common;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True",
              "Kmis": "Server=srv2,1444;Database=KMIS;User Id=u;Password=x;ApplicationIntent=ReadOnly"
            }
            """);

        Assert.Empty(result.Errors);
        Assert.Equal(["Common", "Kmis"], result.Connections.Select(c => c.Profile.Name));
        Assert.Equal(@"srv\inst", result.Connections[0].Profile.Server);
        Assert.True(result.Connections[0].Profile.Encrypt);
        Assert.True(result.Connections[0].Profile.TrustServerCertificate);
        Assert.True(result.Connections[1].Profile.ReadOnlyIntent);
    }

    [Theory]
    [InlineData("""connectionString="Data Source=srv;Initial Catalog=db;Integrated Security=True" />""")]
    [InlineData("\"Data Source=srv;Initial Catalog=db;Integrated Security=True\",")]
    [InlineData("Data Source=srv;\r\nInitial Catalog=db;\r\nIntegrated Security=True")]
    public void ToleratesCopyLeftovers(string text)
    {
        var parsed = Single(text);

        Assert.Equal("srv", parsed.Profile.Server);
        Assert.Equal("db", parsed.Profile.Database);
    }

    [Theory]
    [InlineData("просто текст Password=secret123")]
    [InlineData("Data Source=srv;Password=secret123;Нечто=1")]
    [InlineData("")]
    public void ErrorsDoNotEchoInput(string text)
    {
        var result = ConnectionStringParser.Parse(text);

        Assert.Empty(result.Connections);
        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain(result.Errors, e => e.Contains("secret123"));
    }

    [Fact]
    public void FactoryRebuildsConnectionString()
    {
        var parsed = Single("Data Source=srv;Initial Catalog=db;User ID=u;Password=p;MultiSubnetFailover=True;Persist Security Info=True");

        var built = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(parsed.Profile, parsed.Password));

        Assert.Equal("p", built.Password);
        Assert.Equal("u", built.UserID);
        Assert.True(built.MultiSubnetFailover);
        Assert.False(built.PersistSecurityInfo);
        Assert.Equal(SqlConnectionEncryptOption.Optional, built.Encrypt);
    }
}

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aps-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecretStore _secrets = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static DbConnectionProfile Profile(string name) =>
        new() { Name = name, Server = "srv", AuthMode = DbAuthMode.SqlLogin, UserName = "u" };

    [Fact]
    public void PasswordIsNeverWrittenToFile()
    {
        var store = new ConnectionStore(_directory, _secrets);
        var profile = Profile("A");
        store.Save(profile, "top-secret-value");

        Assert.DoesNotContain("top-secret-value", File.ReadAllText(Path.Combine(_directory, "connections.json")));
        var reloaded = new ConnectionStore(_directory, _secrets);
        Assert.Equal("A", Assert.Single(reloaded.GetAll()).Name);
        Assert.Equal("top-secret-value", reloaded.LoadPassword(profile.Id));
    }

    [Fact]
    public void DuplicateCopiesPasswordAndDeleteRemovesIt()
    {
        var store = new ConnectionStore(_directory, _secrets);
        var profile = Profile("A");
        store.Save(profile, "p");

        var copy = store.Duplicate(profile.Id);
        store.Delete(profile.Id);

        Assert.Equal("A (2)", copy.Name);
        Assert.Equal("p", store.LoadPassword(copy.Id));
        Assert.Null(store.LoadPassword(profile.Id));
    }

    [Fact]
    public void RejectsDuplicateName()
    {
        var store = new ConnectionStore(_directory, _secrets);
        store.Save(Profile("A"), null);

        Assert.Throws<InvalidOperationException>(() => store.Save(Profile("a"), null));
    }

    [Fact]
    public void KeepsBrokenFileAsBackup()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "connections.json"), "{ broken");

        var store = new ConnectionStore(_directory, _secrets);

        Assert.NotNull(store.LoadError);
        Assert.Single(Directory.GetFiles(_directory, "connections.json.broken-*"));
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();

        public bool TrySave(string key, string secret)
        {
            _values[key] = secret;
            return true;
        }

        public string? Load(string key) => _values.GetValueOrDefault(key);

        public void Delete(string key) => _values.Remove(key);
    }
}
