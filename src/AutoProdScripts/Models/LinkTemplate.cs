using System.Text;
using System.Text.RegularExpressions;

namespace AutoProdScripts.Models;

public sealed class LinkTemplateRule
{
    public string Name { get; set; } = string.Empty;
    public string SampleUrl { get; set; } = string.Empty;
    public string Format { get; set; } = "{header}HD: {/header}{value}";
    /// <summary>
    /// Если true — все ссылки этого шаблона в сообщении идут одной строкой через запятую.
    /// </summary>
    public bool CombineLinks { get; set; }
    public List<LinkTemplateMarker> Markers { get; set; } = new();

    public LinkTemplateRule Copy() => new()
    {
        Name = Name,
        SampleUrl = SampleUrl,
        Format = Format,
        CombineLinks = CombineLinks,
        Markers = Markers.Select(m => m.Copy()).ToList()
    };
}

public enum LinkMarkerKind
{
    Anchor = 0,
    Capture = 1
}

public sealed class LinkTemplateMarker
{
    public int Start { get; set; }
    public int Length { get; set; }
    public string Text { get; set; } = string.Empty;
    public LinkMarkerKind Kind { get; set; }
    public string CaptureName { get; set; } = string.Empty;

    public LinkTemplateMarker Copy() => new()
    {
        Start = Start,
        Length = Length,
        Text = Text,
        Kind = Kind,
        CaptureName = CaptureName
    };
}

public enum LinkFormatPartKind
{
    /// <summary>Заголовок списка: при группировке выводится один раз.</summary>
    Header = 0,
    /// <summary>Обычный текст, не ссылка.</summary>
    Text = 1,
    /// <summary>Кликабельная часть (обычно значение).</summary>
    Link = 2
}

public sealed record LinkFormatPart(LinkFormatPartKind Kind, string Value);

public sealed record FormattedLink(
    string Url,
    string Caption,
    string? TemplateName,
    bool Matched,
    IReadOnlyList<LinkFormatPart> Parts);

public static class LinkTemplateEngine
{
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""'\]]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FormatBlockRegex = new(
        @"\{(?<open>header|text)\}(?<body>.*?)\{/\k<open>\}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<string> ExtractUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in UrlRegex.Matches(text))
        {
            var url = TrimTrailingPunctuation(match.Value);
            if (url.Length == 0 || !seen.Add(url))
                continue;
            urls.Add(url);
        }

        return urls;
    }

    public static IReadOnlyList<FormattedLink> FormatLinks(
        string? sourceText,
        IEnumerable<LinkTemplateRule>? rules)
    {
        var result = new List<FormattedLink>();
        foreach (var url in ExtractUrls(sourceText))
            result.Add(FormatUrl(url, rules));
        return result;
    }

    /// <summary>
    /// Строки для сообщения: при CombineLinks ссылки одного шаблона в одной строке.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FormattedLink>> BuildDisplayLines(
        IEnumerable<string> urls,
        IEnumerable<LinkTemplateRule>? rules)
    {
        var ruleList = (rules ?? Array.Empty<LinkTemplateRule>()).Where(IsUsable).ToList();
        var items = new List<(FormattedLink Link, LinkTemplateRule? Rule)>();
        foreach (var url in urls)
        {
            var (link, rule) = FormatUrlWithRule(url, ruleList);
            items.Add((link, rule));
        }

        var used = new bool[items.Count];
        var lines = new List<IReadOnlyList<FormattedLink>>();
        for (var i = 0; i < items.Count; i++)
        {
            if (used[i])
                continue;

            var (link, rule) = items[i];
            if (!link.Matched || rule is null || !rule.CombineLinks)
            {
                lines.Add(new[] { link });
                used[i] = true;
                continue;
            }

            var group = new List<FormattedLink>();
            for (var j = i; j < items.Count; j++)
            {
                if (used[j])
                    continue;
                if (!ReferenceEquals(items[j].Rule, rule))
                    continue;
                group.Add(items[j].Link);
                used[j] = true;
            }

            lines.Add(group);
        }

        return lines;
    }

    public static IReadOnlyList<IReadOnlyList<FormattedLink>> BuildDisplayLinesFromText(
        string? sourceText,
        IEnumerable<LinkTemplateRule>? rules) =>
        BuildDisplayLines(ExtractUrls(sourceText), rules);

    public static FormattedLink FormatUrl(string url, IEnumerable<LinkTemplateRule>? rules) =>
        FormatUrlWithRule(url, rules).Link;

    public static (FormattedLink Link, LinkTemplateRule? Rule) FormatUrlWithRule(
        string url,
        IEnumerable<LinkTemplateRule>? rules)
    {
        var trimmed = (url ?? string.Empty).Trim();
        foreach (var rule in (rules ?? Array.Empty<LinkTemplateRule>()).Where(IsUsable))
        {
            var parts = TryBuildParts(trimmed, rule);
            if (parts is null)
                continue;
            var name = string.IsNullOrWhiteSpace(rule.Name) ? null : rule.Name.Trim();
            var caption = string.Concat(parts.Select(p => p.Value));
            return (new FormattedLink(trimmed, caption, name, Matched: true, parts), rule);
        }

        var plain = new LinkFormatPart(LinkFormatPartKind.Link, trimmed);
        return (new FormattedLink(trimmed, trimmed, null, Matched: false, new[] { plain }), null);
    }

    public static string? TryFormatOne(string url, LinkTemplateRule rule)
    {
        var parts = TryBuildParts(url, rule);
        return parts is null ? null : string.Concat(parts.Select(p => p.Value));
    }

    public static string? TryPreview(LinkTemplateRule rule)
    {
        var parts = TryBuildParts(rule.SampleUrl, rule);
        if (parts is null)
            return null;

        var single = string.Concat(parts.Select(p => p.Value));
        if (!rule.CombineLinks)
            return single;

        var linkChunks = parts.Where(p => p.Kind == LinkFormatPartKind.Link).Select(p => p.Value).ToList();
        if (linkChunks.Count == 0)
            return single;

        var linkText = string.Concat(linkChunks);
        var hasHeader = parts.Any(p => p.Kind == LinkFormatPartKind.Header);
        if (!hasHeader)
            return single + ", " + single + ", " + single;

        // Заголовок один раз, значения через запятую.
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Kind == LinkFormatPartKind.Header || part.Kind == LinkFormatPartKind.Text)
                sb.Append(part.Value);
            else if (part.Kind == LinkFormatPartKind.Link)
                sb.Append(part.Value);
        }

        sb.Append(", ").Append(linkText).Append(", ").Append(linkText);
        return sb.ToString();
    }

    public static IReadOnlyList<LinkFormatPart>? TryBuildParts(string url, LinkTemplateRule rule)
    {
        if (string.IsNullOrWhiteSpace(url) || rule.Markers.Count == 0)
            return null;

        var pattern = BuildRegex(rule);
        if (pattern is null)
            return null;
        var match = pattern.Match(url.Trim());
        if (!match.Success)
            return null;

        var segments = ParseFormatTemplate(rule.Format);
        var parts = new List<LinkFormatPart>();
        foreach (var segment in segments)
        {
            var value = SubstituteTokens(segment.Content, match, url.Trim());
            if (value.Length == 0 && segment.Kind != LinkFormatPartKind.Link)
                continue;
            parts.Add(new LinkFormatPart(segment.Kind, value));
        }

        if (parts.Count == 0)
            parts.Add(new LinkFormatPart(LinkFormatPartKind.Link, url.Trim()));
        return parts;
    }

    /// <summary>
    /// Разбор шаблона: {header}…{/header}, {text}…{/text}, остальное — кликабельная часть.
    /// Без маркеров весь шаблон — ссылка (совместимость: «HD: {value}»).
    /// </summary>
    public static IReadOnlyList<(LinkFormatPartKind Kind, string Content)> ParseFormatTemplate(string? format)
    {
        var raw = string.IsNullOrWhiteSpace(format) ? "{value}" : format;
        var result = new List<(LinkFormatPartKind Kind, string Content)>();
        var index = 0;
        foreach (Match match in FormatBlockRegex.Matches(raw))
        {
            if (match.Index > index)
                result.Add((LinkFormatPartKind.Link, raw[index..match.Index]));

            var open = match.Groups["open"].Value;
            var kind = open.Equals("header", StringComparison.OrdinalIgnoreCase)
                ? LinkFormatPartKind.Header
                : LinkFormatPartKind.Text;
            result.Add((kind, match.Groups["body"].Value));
            index = match.Index + match.Length;
        }

        if (index < raw.Length)
            result.Add((LinkFormatPartKind.Link, raw[index..]));

        if (result.Count == 0)
            result.Add((LinkFormatPartKind.Link, raw));
        return result;
    }

    public static string? ValidateFormatMarkers(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var openHeader = Regex.Matches(format, @"\{header\}", RegexOptions.IgnoreCase).Count;
        var closeHeader = Regex.Matches(format, @"\{/header\}", RegexOptions.IgnoreCase).Count;
        if (openHeader != closeHeader)
            return "Маркер заголовка: число «{header}» и «{/header}» должно совпадать.";

        var openText = Regex.Matches(format, @"\{text\}", RegexOptions.IgnoreCase).Count;
        var closeText = Regex.Matches(format, @"\{/text\}", RegexOptions.IgnoreCase).Count;
        if (openText != closeText)
            return "Маркер текста: число «{text}» и «{/text}» должно совпадать.";

        // Грубая проверка вложенности: после разбора весь формат должен «сходиться».
        var without = FormatBlockRegex.Replace(format, string.Empty);
        if (without.Contains("{header}", StringComparison.OrdinalIgnoreCase)
            || without.Contains("{/header}", StringComparison.OrdinalIgnoreCase)
            || without.Contains("{text}", StringComparison.OrdinalIgnoreCase)
            || without.Contains("{/text}", StringComparison.OrdinalIgnoreCase))
            return "Маркеры {header}/{text} не должны пересекаться или оставаться незакрытыми.";

        return null;
    }

    public static string? ValidateRule(LinkTemplateRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.SampleUrl))
            return "Укажите пример ссылки.";
        if (!LooksLikeUrl(rule.SampleUrl))
            return "Пример должен быть обычной http/https ссылкой.";
        if (rule.Markers.Count == 0)
            return "Выделите в примере фрагменты и добавьте якорь и значение.";
        if (!rule.Markers.Any(m => m.Kind == LinkMarkerKind.Anchor))
            return "Добавьте хотя бы один якорь — постоянный фрагмент ссылки.";
        if (!rule.Markers.Any(m => m.Kind == LinkMarkerKind.Capture))
            return "Добавьте хотя бы одно значение для извлечения.";
        if (string.IsNullOrWhiteSpace(rule.Format))
            return "Укажите формат результата.";

        var markerError = ValidateFormatMarkers(rule.Format);
        if (markerError is not null)
            return markerError;

        var sample = rule.SampleUrl;
        foreach (var marker in rule.Markers)
        {
            if (marker.Start < 0 || marker.Length <= 0 || marker.Start + marker.Length > sample.Length)
                return "Один из фрагментов больше не совпадает с примером. Удалите его и выделите заново.";
            if (!string.Equals(sample.Substring(marker.Start, marker.Length), marker.Text, StringComparison.Ordinal))
                return "Один из фрагментов больше не совпадает с примером. Удалите его и выделите заново.";
        }

        var captures = rule.Markers.Where(m => m.Kind == LinkMarkerKind.Capture).ToList();
        for (var i = 0; i < captures.Count; i++)
        {
            for (var j = i + 1; j < captures.Count; j++)
            {
                if (RangesOverlap(captures[i].Start, captures[i].Length, captures[j].Start, captures[j].Length))
                    return "Два значения пересекаются. Оставьте одно или выделите заново.";
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in captures)
        {
            var name = string.IsNullOrWhiteSpace(marker.CaptureName) ? "value" : marker.CaptureName.Trim();
            if (!IsCaptureName(name))
                return $"Имя значения «{name}» недопустимо. Используйте буквы, цифры и _.";
            if (!names.Add(name))
                return $"Имя значения «{name}» повторяется.";
        }

        if (BuildRegex(rule) is null)
            return "Не удалось собрать правило по выбранным фрагментам.";

        if (TryFormatOne(rule.SampleUrl, rule) is null)
            return "Правило не распознаёт свой же пример ссылки. Проверьте порядок якорей и значений.";

        return null;
    }

    public static bool IsUsable(LinkTemplateRule? rule) =>
        rule is not null && ValidateRule(rule) is null;

    /// <summary>
    /// Regex в порядке списка Markers. Между ними — буквальный фрагмент из примера ссылки,
    /// чтобы после якоря не захватывался первый попавшийся сегмент (ticket вместо filter).
    /// </summary>
    public static Regex? BuildRegex(LinkTemplateRule rule)
    {
        if (!rule.Markers.Any(m => m.Kind == LinkMarkerKind.Capture))
            return null;

        var sample = rule.SampleUrl ?? string.Empty;
        var sb = new StringBuilder();
        sb.Append("^.*?");

        for (var i = 0; i < rule.Markers.Count; i++)
        {
            var marker = rule.Markers[i];
            if (i > 0)
                AppendGap(sb, sample, rule.Markers[i - 1], marker);

            if (marker.Kind == LinkMarkerKind.Anchor)
            {
                sb.Append(Regex.Escape(marker.Text));
                continue;
            }

            var name = string.IsNullOrWhiteSpace(marker.CaptureName) ? "value" : marker.CaptureName.Trim();
            sb.Append("(?<").Append(name).Append('>').Append(CapturePattern(marker.Text)).Append(')');
        }

        sb.Append(".*?$");
        try
        {
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void AppendGap(
        StringBuilder sb,
        string sample,
        LinkTemplateMarker previous,
        LinkTemplateMarker next)
    {
        var gapStart = previous.Start + previous.Length;
        var gapEnd = next.Start;
        if (gapEnd >= gapStart
            && gapStart >= 0
            && gapEnd <= sample.Length
            && previous.Start >= 0
            && next.Start + next.Length <= sample.Length)
        {
            // Фиксированный кусок примера между якорем и значением: /ticket/list/
            sb.Append(Regex.Escape(sample[gapStart..gapEnd]));
            return;
        }

        sb.Append(".*?");
    }

    public static bool RangesOverlap(int startA, int lengthA, int startB, int lengthB)
    {
        var endA = startA + lengthA;
        var endB = startB + lengthB;
        return startA < endB && startB < endA;
    }

    public static string NextCaptureName(IEnumerable<LinkTemplateMarker> markers)
    {
        var used = new HashSet<string>(
            markers
                .Where(m => m.Kind == LinkMarkerKind.Capture)
                .Select(m => string.IsNullOrWhiteSpace(m.CaptureName) ? "value" : m.CaptureName.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (used.Add("value"))
            return "value";
        for (var i = 2; i < 100; i++)
        {
            var name = "value" + i;
            if (used.Add(name))
                return name;
        }

        return "value" + Guid.NewGuid().ToString("N")[..4];
    }

    public static bool IsCaptureName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && char.IsLetter(name[0])
        && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    public static bool LooksLikeUrl(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Шаблон по структуре выделенного фрагмента:
    /// 97313 → \d+
    /// filter → filter (тот же текст)
    /// ff/text2023 → ff/text\d+
    /// startText2304endText → startText\d+endText
    /// </summary>
    private static string CapturePattern(string sample)
    {
        if (string.IsNullOrEmpty(sample))
            return @"[^/\s?#]+";

        // Только цифры.
        if (sample.All(char.IsDigit))
            return @"\d+";

        var sb = new StringBuilder();
        var i = 0;
        while (i < sample.Length)
        {
            if (char.IsDigit(sample[i]))
            {
                while (i < sample.Length && char.IsDigit(sample[i]))
                    i++;
                sb.Append(@"\d+");
                continue;
            }

            var start = i;
            while (i < sample.Length && !char.IsDigit(sample[i]))
                i++;
            sb.Append(Regex.Escape(sample[start..i]));
        }

        return sb.ToString();
    }

    private static string SubstituteTokens(string? format, Match match, string url)
    {
        var body = format ?? string.Empty;
        body = ReplaceToken(body, "url", url);
        foreach (Group group in match.Groups)
        {
            if (group.Name is "0" || !IsCaptureName(group.Name))
                continue;
            body = ReplaceToken(body, group.Name, group.Value);
        }

        return body;
    }

    private static string ReplaceToken(string body, string name, string value)
    {
        var pattern = new Regex(@"\{" + Regex.Escape(name) + @"\}", RegexOptions.IgnoreCase);
        return pattern.Replace(body, _ => value);
    }

    private static string TrimTrailingPunctuation(string url)
    {
        var end = url.Length;
        while (end > 0 && url[end - 1] is '.' or ',' or ';' or ':' or ')' or ']' or '}')
            end--;
        return end == url.Length ? url : url[..end];
    }
}
