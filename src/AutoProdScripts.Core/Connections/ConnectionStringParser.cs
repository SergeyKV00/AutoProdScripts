using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Connections;

public sealed record ParsedConnection(DbConnectionProfile Profile, string? Password);

/// <summary>Errors не содержат исходный текст, чтобы пароль не попал на экран или в лог.</summary>
public sealed record ConnectionParseResult(IReadOnlyList<ParsedConnection> Connections, IReadOnlyList<string> Errors);

/// <summary>
/// Разбирает вставленный текст: чистую строку подключения, обрывок конфига,
/// элементы &lt;add name connectionString/&gt; (в том числе блоком) и пары "Имя": "строка" из appsettings.json.
/// </summary>
public static class ConnectionStringParser
{
    private static readonly Regex XmlComment = new("<!--.*?-->", RegexOptions.Singleline);

    private static readonly Regex AddElement = new(
        """<add\b(?<attrs>(?:[^>"']|"[^"]*"|'[^']*')*)>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex XmlAttribute = new(
        """(?<key>[\w:]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""",
        RegexOptions.Singleline);

    private static readonly Regex JsonPair = new(
        """
        ^\s*"(?<name>[^"]+)"\s*:\s*"(?<value>(?:[^"\\]|\\.)*(?:Data Source|Server|Address|Addr)\s*=(?:[^"\\]|\\.)*)"\s*,?\s*$
        """,
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex LineStart = new(
        """^\s*(?:connectionString\s*=\s*")?\s*(?:Data Source|Server|Address|Addr|Network Address)\s*=""",
        RegexOptions.IgnoreCase);

    // Ключи, которые разложены по отдельным полям и не должны попадать в «прочие параметры».
    private static readonly string[] MappedKeys =
    [
        "Data Source", "Initial Catalog", "Integrated Security", "User ID", "Password", "Encrypt",
        "Trust Server Certificate", "Application Intent", "Connect Timeout", "Persist Security Info",
        "Application Name"
    ];

    public static ConnectionParseResult Parse(string? text)
    {
        var connections = new List<ParsedConnection>();
        var errors = new List<string>();
        var number = 0;

        foreach (var (name, value) in Extract(text ?? string.Empty))
        {
            number++;
            var label = string.IsNullOrWhiteSpace(name) ? $"Строка {number}" : name;
            try
            {
                connections.Add(ParseOne(name, value));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                errors.Add($"{label}: не удалось разобрать строку подключения.");
            }
        }

        if (number == 0)
            errors.Add("В тексте не найдено ни одной строки подключения.");

        return new ConnectionParseResult(connections, errors);
    }

    private static IEnumerable<(string? Name, string Value)> Extract(string text)
    {
        text = XmlComment.Replace(text, string.Empty).Trim();
        if (text.Length == 0)
            yield break;

        var elements = AddElement.Matches(text);
        if (elements.Count > 0)
        {
            foreach (Match element in elements)
            {
                var attributes = XmlAttribute.Matches(element.Groups["attrs"].Value)
                    .GroupBy(m => m.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups["value"].Value),
                        StringComparer.OrdinalIgnoreCase);
                if (attributes.TryGetValue("connectionString", out var value))
                    yield return (attributes.GetValueOrDefault("name"), value);
            }

            yield break;
        }

        var pairs = JsonPair.Matches(text);
        if (pairs.Count > 0)
        {
            foreach (Match pair in pairs)
                yield return (pair.Groups["name"].Value, Regex.Unescape(pair.Groups["value"].Value));
            yield break;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1 && lines.All(l => LineStart.IsMatch(l)))
        {
            foreach (var line in lines)
                yield return (null, TrimFragment(line));
            yield break;
        }

        yield return (null, TrimFragment(string.Concat(lines)));
    }

    /// <summary>Убирает обрамление, оставшееся при копировании куска конфига.</summary>
    private static string TrimFragment(string value)
    {
        value = value.Trim();
        value = Regex.Replace(value, """^connectionString\s*=\s*["']""", string.Empty, RegexOptions.IgnoreCase);

        var provider = Regex.Match(value, """["']\s*providerName\s*=.*$""", RegexOptions.IgnoreCase);
        if (provider.Success)
            value = value[..provider.Index];
        else
            value = Regex.Replace(value, """["']\s*/>\s*$""", string.Empty);

        if (value.Contains("&quot;", StringComparison.OrdinalIgnoreCase))
            value = WebUtility.HtmlDecode(value);

        // Внешние кавычки вокруг всей строки.
        value = value.Trim().TrimEnd(',');
        if (value.Length > 1 && value[0] == '"')
            value = value.Trim('"');
        return value;
    }

    private static ParsedConnection ParseOne(string? name, string value)
    {
        // Строки Entity Framework вкладывают настоящую строку в "provider connection string".
        if (value.Contains("provider connection string", StringComparison.OrdinalIgnoreCase))
        {
            var outer = new DbConnectionStringBuilder { ConnectionString = value };
            value = (string)outer["provider connection string"];
        }

        var builder = new SqlConnectionStringBuilder(value);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
            throw new ArgumentException("Нет сервера.");

        var windows = builder.IntegratedSecurity;
        var profile = new DbConnectionProfile
        {
            Server = builder.DataSource,
            Database = builder.InitialCatalog,
            AuthMode = windows ? DbAuthMode.Windows : DbAuthMode.SqlLogin,
            UserName = windows ? string.Empty : builder.UserID,
            // System.Data.SqlClient по умолчанию не шифровал соединение — сохраняем это поведение.
            Encrypt = builder.ShouldSerialize("Encrypt") && builder.Encrypt != SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = builder.TrustServerCertificate,
            ReadOnlyIntent = builder.ApplicationIntent == ApplicationIntent.ReadOnly
        };

        if (builder.ShouldSerialize("Connect Timeout"))
            profile.ConnectTimeoutSeconds = builder.ConnectTimeout;

        profile.Name = !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : !string.IsNullOrWhiteSpace(profile.Database) ? profile.Database : profile.Server;

        var password = windows || string.IsNullOrEmpty(builder.Password) ? null : builder.Password;

        foreach (var key in MappedKeys)
            builder.Remove(key);
        profile.ExtraParameters = builder.ConnectionString;

        return new ParsedConnection(profile, password);
    }
}
