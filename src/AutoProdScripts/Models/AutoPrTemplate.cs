namespace AutoProdScripts.Models;

public static class AutoPrTemplate
{
    public const string DefaultBranchTemplate = "{base}_{yyyyMMddHHmm}";
    public const string DefaultTelegramTemplate =
        "Доброе утро. Выполните пожалуйста скрипты {link}\n{tags}";

    public static bool IsReady(AppSettings settings) =>
        settings.AutoPrTemplateConfigured
        && !string.IsNullOrWhiteSpace(settings.AutoBaseBranch)
        && !string.IsNullOrWhiteSpace(settings.AutoBranchNameTemplate);

    public static string ExpandBranchName(string? template, string baseBranch, DateTime moment)
    {
        var raw = string.IsNullOrWhiteSpace(template) ? DefaultBranchTemplate : template.Trim();
        raw = raw
            .Replace("{yyyyMMddHHmm}", moment.ToString("yyyyMMddHHmm"), StringComparison.Ordinal)
            .Replace("{yyyy}", moment.ToString("yyyy"), StringComparison.Ordinal)
            .Replace("{MM}", moment.ToString("MM"), StringComparison.Ordinal)
            .Replace("{dd}", moment.ToString("dd"), StringComparison.Ordinal)
            .Replace("{HH}", moment.ToString("HH"), StringComparison.Ordinal)
            .Replace("{mm}", moment.ToString("mm"), StringComparison.Ordinal)
            .Replace("{base}", baseBranch.Trim(), StringComparison.Ordinal);
        return SanitizeBranchName(raw);
    }

    public static string SuggestTitle(IEnumerable<string> relativePaths)
    {
        var names = relativePaths
            .Select(path => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            return "скрипты";
        if (names.Count <= 3)
            return string.Join(", ", names);
        return string.Join(", ", names.Take(3)) + "…";
    }

    public static string SanitizeBranchName(string name)
    {
        var buffer = name.Trim().Select(ch => ch is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\' or '\t'
            ? '_'
            : ch).ToArray();
        var value = new string(buffer);
        while (value.Contains("..", StringComparison.Ordinal))
            value = value.Replace("..", ".", StringComparison.Ordinal);
        value = value.Trim('/', '.');
        if (value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            value = value[..^5].TrimEnd('.');
        return value;
    }
}

public static class TelegramMessage
{
    public static string FormatTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var tags = raw
            .Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.StartsWith('@') ? part : "@" + part);
        return string.Join(' ', tags);
    }

    public static string ExpandCustom(
        string? template,
        IEnumerable<TelegramCustomTemplate>? custom)
    {
        var body = string.IsNullOrWhiteSpace(template) ? AutoPrTemplate.DefaultTelegramTemplate : template;
        var parts = (custom ?? Array.Empty<TelegramCustomTemplate>())
            .Where(part => !string.IsNullOrWhiteSpace(part.Name))
            .ToList();
        for (var pass = 0; pass < parts.Count; pass++)
        {
            var changed = false;
            foreach (var part in parts)
            {
                var token = "{" + part.Name.Trim() + "}";
                if (!body.Contains(token, StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = part.Value ?? string.Empty;
                if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
                    continue;
                body = ReplaceToken(body, part.Name.Trim(), value);
                changed = true;
            }

            if (!changed)
                break;
        }

        return body;
    }

    public static string Build(
        string? template,
        string text,
        string linkCaption,
        string? tags,
        IEnumerable<TelegramCustomTemplate>? custom = null)
    {
        var body = ExpandCustom(template, custom);
        return ExpandBase(body, text, linkCaption, tags).Trim();
    }

    public static string ExpandBase(string body, string text, string linkCaption, string? tags) =>
        body
            .Replace("{text}", text, StringComparison.Ordinal)
            .Replace("{title}", text, StringComparison.Ordinal)
            .Replace("{link}", linkCaption, StringComparison.Ordinal)
            .Replace("{tags}", FormatTags(tags), StringComparison.Ordinal);

    private static string ReplaceToken(string body, string name, string value) =>
        body.Replace("{" + name + "}", value, StringComparison.OrdinalIgnoreCase);
}
