using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace AutoProdScripts.Views;

/// <summary>
/// Telegram Desktop ignores HTML on paste. It keeps a custom caption link only when
/// the clipboard has application/x-td-field-text and application/x-td-field-tags.
/// </summary>
public static class TelegramMessageClipboard
{
    private const string TextFormat = "application/x-td-field-text";
    private const string TagsFormat = "application/x-td-field-tags";

    public static bool TryCopy(FlowDocument document)
    {
        var data = Create(document);
        if (data is null)
            return false;

        Clipboard.SetDataObject(data, copy: true);
        return true;
    }

    public static DataObject? Create(FlowDocument document)
    {
        var text = new StringBuilder();
        var tags = new List<LinkTag>();
        foreach (var block in document.Blocks)
            AppendBlock(block, text, tags);

        var raw = text.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        var start = 0;
        while (start < raw.Length && char.IsWhiteSpace(raw[start]))
            start++;
        var end = raw.Length;
        while (end > start && char.IsWhiteSpace(raw[end - 1]))
            end--;
        if (end <= start)
            return null;

        var plain = raw[start..end];
        var shifted = new List<LinkTag>();
        foreach (var tag in tags)
        {
            var offset = tag.Offset - start;
            if (offset < 0 || tag.Length <= 0 || offset + tag.Length > plain.Length)
                continue;
            shifted.Add(new LinkTag(offset, tag.Length, tag.Url));
        }

        var data = new DataObject();
        data.SetText(plain);
        if (shifted.Count == 0)
            return data;

        data.SetData(TextFormat, new MemoryStream(Encoding.UTF8.GetBytes(plain)));
        data.SetData(TagsFormat, new MemoryStream(SerializeTags(shifted)));
        data.SetData(DataFormats.Html, HtmlClipboard(plain, shifted));
        return data;
    }

    private static void AppendBlock(Block block, StringBuilder text, List<LinkTag> tags)
    {
        if (text.Length > 0)
            text.Append('\n');
        if (block is Paragraph paragraph)
        {
            foreach (var inline in paragraph.Inlines)
                AppendInline(inline, text, tags);
        }
        else if (block is Section section)
        {
            foreach (var child in section.Blocks)
                AppendBlock(child, text, tags);
        }
    }

    private static void AppendInline(Inline inline, StringBuilder text, List<LinkTag> tags)
    {
        switch (inline)
        {
            case Hyperlink link:
                var offset = text.Length;
                foreach (var child in link.Inlines)
                    AppendInline(child, text, tags);
                var url = link.NavigateUri?.AbsoluteUri;
                var length = text.Length - offset;
                if (length > 0 && IsTelegramLink(url))
                    tags.Add(new LinkTag(offset, length, url!));
                break;
            case Run run:
                text.Append(run.Text.Replace("\r\n", "\n").Replace('\r', '\n'));
                break;
            case LineBreak:
                text.Append('\n');
                break;
            case Span span:
                foreach (var child in span.Inlines)
                    AppendInline(child, text, tags);
                break;
        }
    }

    private static bool IsTelegramLink(string? url) =>
        !string.IsNullOrWhiteSpace(url) && (url.Contains('.') || url.Contains(':'));

    private static byte[] SerializeTags(IReadOnlyList<LinkTag> tags)
    {
        using var stream = new MemoryStream();
        WriteInt32(stream, tags.Count);
        foreach (var tag in tags)
        {
            WriteInt32(stream, tag.Offset);
            WriteInt32(stream, tag.Length);
            WriteQtString(stream, tag.Url);
        }

        return stream.ToArray();
    }

    private static void WriteQtString(Stream stream, string value)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(value);
        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static string HtmlClipboard(string plain, IReadOnlyList<LinkTag> tags)
    {
        var html = new StringBuilder();
        var index = 0;
        foreach (var tag in tags.OrderBy(item => item.Offset))
        {
            if (tag.Offset < index || tag.Offset + tag.Length > plain.Length)
                continue;
            html.Append(HtmlText(plain[index..tag.Offset]));
            html.Append("<a href=\"");
            html.Append(WebUtility.HtmlEncode(tag.Url));
            html.Append("\">");
            html.Append(HtmlText(plain.Substring(tag.Offset, tag.Length)));
            html.Append("</a>");
            index = tag.Offset + tag.Length;
        }

        html.Append(HtmlText(plain[index..]));
        return WrapClipboardHtml(html.ToString());
    }

    private static string HtmlText(string value) =>
        WebUtility.HtmlEncode(value).Replace("\n", "<br>", StringComparison.Ordinal);

    private static string WrapClipboardHtml(string fragment)
    {
        const string header =
            "Version:0.9\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        var prefix = string.Format(header, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(prefix);
        const string before = "<html>\r\n<body>\r\n<!--StartFragment-->";
        const string after = "<!--EndFragment-->\r\n</body>\r\n</html>";
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return string.Format(header, startHtml, endHtml, startFragment, endFragment)
               + before
               + fragment
               + after;
    }

    private readonly record struct LinkTag(int Offset, int Length, string Url);
}
