using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoProdScripts.Models;

public static class AutoPrTemplate
{
    public const string DefaultBranchTemplate = "{base}_{yyyyMMddHHmm}";
    public const string DefaultTelegramTemplate =
        "Доброе утро. Выполните, пожалуйста, скрипты {link}\n{tags}";
    public const string DefaultLinkCaption = "Открыть PR";
    public const int TelegramMaxLength = 4096;

    public static string LinkCaptionOrDefault(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultLinkCaption : value.Trim();

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
        IEnumerable<TelegramCustomTemplate>? custom = null,
        IEnumerable<TelegramTimeTextRule>? timeTexts = null,
        string? timeZoneId = null,
        DateTimeOffset? now = null) =>
        string.Concat(Parts(template, text, linkCaption, tags, custom, timeTexts, timeZoneId, now).Select(part => part.Value)).Trim();

    public static IReadOnlyList<TelegramPart> Parts(
        string? template,
        string text,
        string? linkCaption,
        string? tags,
        IEnumerable<TelegramCustomTemplate>? custom = null,
        IEnumerable<TelegramTimeTextRule>? timeTexts = null,
        string? timeZoneId = null,
        DateTimeOffset? now = null)
    {
        var timeText = ResolveTimeText(timeTexts, timeZoneId, now);
        return Tokenize(ExpandCustom(template, custom), text, linkCaption, tags, timeText);
    }

    public static string ResolveTimeText(
        IEnumerable<TelegramTimeTextRule>? rules,
        string? timeZoneId,
        DateTimeOffset? now = null)
    {
        var moment = now ?? DateTimeOffset.Now;
        var local = TimeZoneInfo.ConvertTime(moment, ResolveTimeZone(timeZoneId));
        var clock = TimeOnly.FromDateTime(local.DateTime);
        foreach (var rule in rules ?? Array.Empty<TelegramTimeTextRule>())
        {
            if (!TryParseClock(rule.From, out var from) || !TryParseClock(rule.To, out var to))
                continue;
            if (IsInsidePeriod(from, to, clock))
                return rule.Text ?? string.Empty;
        }

        return string.Empty;
    }

    public static bool TryParseClock(string? raw, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return TimeOnly.TryParseExact(
            raw.Trim(),
            new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" },
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);
    }

    public static bool IsInsidePeriod(TimeOnly from, TimeOnly to, TimeOnly value)
    {
        if (from == to)
            return false;
        if (from < to)
            return value >= from && value < to;
        return value >= from || value < to;
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }

    public static string ExpandBase(string body, string text, string linkCaption, string? tags) =>
        string.Concat(Tokenize(body, text, linkCaption, tags, string.Empty).Select(part => part.Value));

    private static IReadOnlyList<TelegramPart> Tokenize(
        string body,
        string text,
        string? linkCaption,
        string? tags,
        string timeText)
    {
        var caption = AutoPrTemplate.LinkCaptionOrDefault(linkCaption);
        var formattedTags = FormatTags(tags);
        var pattern = new Regex(@"\{([^{}]+)\}");
        var parts = new List<TelegramPart>();
        var index = 0;
        foreach (Match match in pattern.Matches(body))
        {
            if (match.Index > index)
                parts.Add(new TelegramPart(TelegramPartKind.Text, body[index..match.Index]));

            var key = match.Groups[1].Value.Trim();
            if (key.Equals("link", StringComparison.OrdinalIgnoreCase))
                parts.Add(new TelegramPart(TelegramPartKind.Link, caption));
            else if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
                parts.Add(new TelegramPart(TelegramPartKind.Tags, formattedTags));
            else if (key.Equals("timeText", StringComparison.OrdinalIgnoreCase))
                parts.Add(new TelegramPart(TelegramPartKind.Text, timeText ?? string.Empty));
            else if (key.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("title", StringComparison.OrdinalIgnoreCase))
                parts.Add(new TelegramPart(TelegramPartKind.Text, text ?? string.Empty));
            else
                parts.Add(new TelegramPart(TelegramPartKind.Text, match.Value));

            index = match.Index + match.Length;
        }

        if (index < body.Length)
            parts.Add(new TelegramPart(TelegramPartKind.Text, body[index..]));
        return parts;
    }

    private static string ReplaceToken(string body, string name, string value)
    {
        var pattern = new Regex(@"\{" + Regex.Escape(name) + @"\}", RegexOptions.IgnoreCase);
        return pattern.Replace(body, _ => value);
    }
}

public enum TelegramPartKind
{
    Text,
    Link,
    Tags
}

public readonly record struct TelegramPart(TelegramPartKind Kind, string Value);
