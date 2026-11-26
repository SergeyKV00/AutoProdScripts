namespace AutoProdScripts.Core.Connections;

public enum DbAuthMode
{
    Windows = 0,
    SqlLogin = 1
}

/// <summary>Тип СУБД. Пока поддерживается только MS SQL Server; остальное — задел под провайдеры.</summary>
public enum DbProvider
{
    SqlServer = 0
}

/// <summary>
/// Настройки одного подключения. Пароль здесь не хранится — только в хранилище секретов.
/// </summary>
public sealed class DbConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DbProvider Provider { get; set; } = DbProvider.SqlServer;

    /// <summary>Имя, по которому на подключение ссылаются скрипты.</summary>
    public string Name { get; set; } = string.Empty;

    public string Server { get; set; } = string.Empty;

    /// <summary>Порт; null — по умолчанию (или указан в самом адресе как «host,port»).</summary>
    public int? Port { get; set; }

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

    /// <summary>
    /// Среда из старых версий (Prod/Test/Dev). Только для чтения старого файла: при загрузке
    /// переносится в <see cref="Group"/> и больше не записывается.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("Environment")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEnvironment { get; set; }

    /// <summary>Остальные параметры строки подключения (MultiSubnetFailover и т.п.).</summary>
    public string ExtraParameters { get; set; } = string.Empty;

    /// <summary>Группа в дереве подключений, вложенность через «/» (например «PROD» или «Регионы/Астана»). Пусто — без группы.</summary>
    public string? Group { get; set; }

    /// <summary>Задел под запуск по регионам, пока не используется.</summary>
    public string? Region { get; set; }

    public DbConnectionProfile Clone() => (DbConnectionProfile)MemberwiseClone();
}
