using System.Text.RegularExpressions;

namespace AutoProdScripts.Core.Regions;

/// <summary>Регион и кластер, на котором лежат все его базы.</summary>
public sealed class Region
{
    /// <summary>Подставляется в имя базы вместо {region}: «ast» → KMIS_ast.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public Guid? ConnectionId { get; set; }
    public string ConnectionName { get; set; } = string.Empty;

    public Region Clone() => (Region)MemberwiseClone();

    public string Title => Name.Length > 0 ? $"{Code} — {Name}" : Code;
}

/// <summary>
/// Имя базы региона: «KMIS_{region}», или просто «KMIS» — тогда код добавляется в конец.
/// </summary>
public static partial class RegionDatabase
{
    public const string Placeholder = "{region}";

    [GeneratedRegex(@"^[\p{L}\p{N}_\-]{1,128}$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,32}$")]
    private static partial Regex SafeCode();

    public static bool IsValidCode(string code) => SafeCode().IsMatch(code);

    /// <returns>null — шаблон годится, иначе что в нём не так.</returns>
    public static string? Validate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "Укажите имя базы, например KMIS.";
        return SafeName().IsMatch(Resolve(template, "x"))
            ? null
            : "В имени базы допустимы только буквы, цифры, «_» и «-».";
    }

    public static string Resolve(string template, string code)
    {
        var name = template.Trim();
        if (name.Contains(Placeholder, StringComparison.OrdinalIgnoreCase))
            return name.Replace(Placeholder, code, StringComparison.OrdinalIgnoreCase);

        // «KMIS» и «KMIS_» дают одно и то же: KMIS_ast.
        return name.EndsWith('_') || name.EndsWith('-') ? name + code : $"{name}_{code}";
    }
}
