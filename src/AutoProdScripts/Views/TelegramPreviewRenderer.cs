using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AutoProdScripts.Models;

namespace AutoProdScripts.Views;

public static class TelegramPreviewRenderer
{
    public const string DemoText = "Скрипты по заявкам";
    public const string DemoUrl = "https://dev.azure.com/preview/pullrequest";

    public static Run? Render(
        Paragraph paragraph,
        string? template,
        string text,
        string? linkCaption,
        string? linkUrl,
        string? tags,
        IEnumerable<TelegramCustomTemplate>? custom,
        Brush linkBrush,
        Action<string>? openLink,
        IEnumerable<TelegramTimeTextRule>? timeTexts = null,
        string? timeZoneId = null,
        DateTimeOffset? now = null)
    {
        Run? tagsRun = null;
        foreach (var part in TelegramMessage.Parts(template, text, linkCaption, tags, custom, timeTexts, timeZoneId, now))
        {
            switch (part.Kind)
            {
                case TelegramPartKind.Link:
                    paragraph.Inlines.Add(CreateLink(part.Value, linkUrl, linkBrush, openLink));
                    break;
                case TelegramPartKind.Tags:
                    tagsRun = new Run(part.Value);
                    paragraph.Inlines.Add(tagsRun);
                    break;
                default:
                    AddText(paragraph, part.Value);
                    break;
            }
        }

        return tagsRun;
    }

    private static void AddText(Paragraph paragraph, string value)
    {
        var lines = value.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0)
                paragraph.Inlines.Add(new Run(line));
            if (i < lines.Length - 1)
                paragraph.Inlines.Add(new LineBreak());
        }
    }

    private static Hyperlink CreateLink(string caption, string? url, Brush linkBrush, Action<string>? openLink)
    {
        var link = new Hyperlink(new Run(string.IsNullOrWhiteSpace(caption) ? AutoPrTemplate.DefaultLinkCaption : caption))
        {
            Foreground = linkBrush,
            TextDecorations = TextDecorations.Underline
        };
        if (openLink is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            link.NavigateUri = uri;
            link.Click += (_, _) => openLink(uri.ToString());
        }

        return link;
    }
}
