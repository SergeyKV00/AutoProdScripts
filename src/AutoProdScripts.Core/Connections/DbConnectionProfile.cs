namespace AutoProdScripts.Core.Connections;

public enum DbAuthMode
{
    Windows = 0,
    SqlLogin = 1
}

public enum DbEnvironment
{
    Prod = 0,
    Test = 1,
    Dev = 2
}

/// <summary>
/// Настройки одного подключения. Пароль здесь не хранится — только в хранилище секретов.
/// </summary>
public sealed class DbConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Имя, по которому на подключение ссылаются скрипты.</summary>
    public string Name { get; set; } = string.Empty;

    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;

    /// <summary>База не указана: подключение к кластеру, базу выбирает скрипт (USE или полное имя).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsClusterLevel => string.IsNullOrWhiteSpace(Database);

    public DbAuthMode AuthMode { get; set; } = DbAuthMode.Windows;
    public string UserName { get; set; } = string.Empty;
    public bool Encrypt { get; set; }
    public bool TrustServerCertificate { get; set; }
    public bool ReadOnlyIntent { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>По умолчанию «бой»: незнакомое подключение считаем самым опасным.</summary>
    public DbEnvironment Environment { get; set; } = DbEnvironment.Prod;

    /// <summary>Остальные параметры строки подключения (MultiSubnetFailover и т.п.).</summary>
    public string ExtraParameters { get; set; } = string.Empty;

    /// <summary>Группа и регион — задел под запуск по регионам, пока не используются.</summary>
    public string? Group { get; set; }

    public string? Region { get; set; }

    public DbConnectionProfile Clone() => (DbConnectionProfile)MemberwiseClone();
}
