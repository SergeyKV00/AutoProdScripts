using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Models;

namespace AutoProdScripts.Views;

public partial class PullRequestResultDialog : Window
{
    private readonly bool _hasTelegram;
    private readonly IReadOnlyList<LinkTemplateRule> _linkTemplates;
    private readonly List<ManagedLinkEntry> _managedLinks = new();
    private TelegramCompose? _telegram;
    private string _linkCaption = string.Empty;
    private string _linkUrl = string.Empty;

    public PullRequestResultDialog(
        string title,
        string status,
        string? url,
        bool success,
        TelegramCompose? telegram = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        StatusText.Text = status;
        UrlBox.Text = url ?? string.Empty;
        OpenButton.IsEnabled = success && !string.IsNullOrWhiteSpace(url);
        _hasTelegram = telegram is not null;
        _linkTemplates = telegram?.LinkTemplates?.Select(r => r.Copy()).ToList()
                         ?? new List<LinkTemplateRule>();
        if (telegram is null)
            return;

        _telegram = telegram;
        TelegramPanel.Visibility = Visibility.Visible;
        CopyMessageButton.Visibility = Visibility.Visible;
        _linkCaption = AutoPrTemplate.LinkCaptionOrDefault(telegram.LinkCaption);
        _linkUrl = string.IsNullOrWhiteSpace(telegram.LinkUrl) ? url ?? string.Empty : telegram.LinkUrl;
        if (!string.IsNullOrWhiteSpace(_linkUrl))
            UrlBox.Text = _linkUrl;

        RebuildTelegramMessage();
        RenderManagedLinks();
        Width = 760;
        Height = 780;
    }

    private void RebuildTelegramMessage()
    {
        if (_telegram is null)
            return;

        var formatted = BuildFormattedManagedLinks();
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var hasAutoLink = TelegramMessage.TemplateContainsAutoLink(_telegram.Template);
        TelegramPreviewRenderer.Render(
            paragraph,
            _telegram.Template,
            _telegram.Text,
            _linkCaption,
            _linkUrl,
            _telegram.Tags,
            _telegram.Custom,
            (Brush)FindResource("FgInfoBrush"),
            url => OpenUri(url),
            _telegram.TimeTexts,
            _telegram.TimeZoneId,
            linkTemplates: _linkTemplates,
            linkSourceText: string.Empty,
            autoLinkLines: hasAutoLink ? formatted : Array.Empty<IReadOnlyList<FormattedLink>>());

        if (!hasAutoLink && _managedLinks.Count > 0)
            AppendManagedLinksFallback(paragraph, formatted);

        var document = new FlowDocument
        {
            PagePadding = new Thickness(2),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("FgBrush")
        };
        document.Blocks.Add(paragraph);
        TelegramMessageBox.Document = document;
    }

    private IReadOnlyList<IReadOnlyList<FormattedLink>> BuildFormattedManagedLinks()
    {
        var items = new List<(FormattedLink Link, LinkTemplateRule? Rule)>();
        foreach (var entry in _managedLinks)
        {
            if (!string.IsNullOrWhiteSpace(entry.CustomCaption))
            {
                var caption = entry.CustomCaption.Trim();
                var parts = new[] { new LinkFormatPart(LinkFormatPartKind.Link, caption) };
                items.Add((new FormattedLink(entry.Url, caption, "Вручную", Matched: true, parts), null));
                continue;
            }

            var (link, rule) = LinkTemplateEngine.FormatUrlWithRule(entry.Url, _linkTemplates);
            items.Add((link, rule));
        }

        return GroupFormattedItems(items);
    }

    private static IReadOnlyList<IReadOnlyList<FormattedLink>> GroupFormattedItems(
        List<(FormattedLink Link, LinkTemplateRule? Rule)> items)
    {
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

    private void AppendManagedLinksFallback(
        Paragraph paragraph,
        IReadOnlyList<IReadOnlyList<FormattedLink>> lines)
    {
        if (lines.Count == 0)
            return;
        if (paragraph.Inlines.Count > 0)
            paragraph.Inlines.Add(new LineBreak());
        AppendManagedLinkLines(paragraph, lines);
    }

    private void AppendManagedLinkLines(
        Paragraph paragraph,
        IReadOnlyList<IReadOnlyList<FormattedLink>> lines)
    {
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (lineIndex > 0)
                paragraph.Inlines.Add(new LineBreak());

            var line = lines[lineIndex];
            var combined = line.Count > 1;
            for (var i = 0; i < line.Count; i++)
            {
                if (i > 0)
                    paragraph.Inlines.Add(new Run(", "));
                AppendFormattedLinkInlines(paragraph, line[i], includeHeader: !combined || i == 0);
            }
        }
    }

    private void AppendFormattedLinkInlines(Paragraph paragraph, FormattedLink item, bool includeHeader)
    {
        if (item.Parts.Count == 0)
        {
            var caption = item.Matched ? item.Caption : item.Url;
            paragraph.Inlines.Add(CreateMessageLink(caption, item.Url));
            return;
        }

        foreach (var part in item.Parts)
        {
            if (part.Kind == LinkFormatPartKind.Header && !includeHeader)
                continue;
            if (string.IsNullOrEmpty(part.Value))
                continue;

            if (part.Kind == LinkFormatPartKind.Link)
                paragraph.Inlines.Add(CreateMessageLink(part.Value, item.Url));
            else
                paragraph.Inlines.Add(new Run(part.Value));
        }
    }

    private Hyperlink CreateMessageLink(string caption, string url)
    {
        var link = new Hyperlink(new Run(caption))
        {
            Foreground = (Brush)FindResource("FgInfoBrush"),
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline
        };
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            link.NavigateUri = uri;
            link.Click += (_, _) => OpenUri(uri);
        }

        return link;
    }

    private void RenderManagedLinks()
    {
        if (ManagedLinksHost is null || ManagedLinksEmptyText is null)
            return;

        ManagedLinksHost.Children.Clear();
        ManagedLinksEmptyText.Visibility = _managedLinks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in _managedLinks.ToList())
        {
            var formatted = string.IsNullOrWhiteSpace(entry.CustomCaption)
                ? LinkTemplateEngine.FormatUrl(entry.Url, _linkTemplates)
                : null;
            var matched = formatted?.Matched == true || !string.IsNullOrWhiteSpace(entry.CustomCaption);
            var statusText = !string.IsNullOrWhiteSpace(entry.CustomCaption)
                ? "Вручную: " + entry.CustomCaption.Trim()
                : formatted!.Matched
                    ? (string.IsNullOrWhiteSpace(formatted.TemplateName) ? "Шаблон" : formatted.TemplateName!)
                    : "Не удалось сопоставить";

            var urlBox = new TextBox
            {
                Text = entry.Url,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("FgBrush"),
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Cursor = Cursors.IBeam
            };

            var status = new TextBlock
            {
                Text = statusText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = matched
                    ? (Brush)FindResource("FgInfoBrush")
                    : (Brush)FindResource("FgMutedBrush")
            };

            var format = new Button
            {
                Content = "Формат",
                Style = (Style)FindResource("SecondaryButton"),
                Width = 90,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = entry,
                ToolTip = "Задать текст ссылки вручную"
            };
            format.Click += FormatManagedLink_Click;

            var remove = new Button
            {
                Content = "Удалить",
                Style = (Style)FindResource("SecondaryButton"),
                Width = 90,
                Margin = new Thickness(8, 4, 0, 0),
                Tag = entry
            };
            remove.Click += RemoveManagedLink_Click;

            var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(format);
            buttons.Children.Add(remove);

            var texts = new StackPanel();
            texts.Children.Add(urlBox);
            texts.Children.Add(status);

            var row = new Border
            {
                Background = (Brush)FindResource("PanelBrush"),
                BorderBrush = matched
                    ? (Brush)FindResource("AccentBrush")
                    : (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var dock = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            dock.Children.Add(buttons);
            dock.Children.Add(texts);
            row.Child = dock;
            ManagedLinksHost.Children.Add(row);
        }
    }

    private void FormatManagedLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ManagedLinkEntry entry })
            return;

        var currentCaption = !string.IsNullOrWhiteSpace(entry.CustomCaption)
            ? entry.CustomCaption.Trim()
            : LinkTemplateEngine.FormatUrl(entry.Url, _linkTemplates).Caption;
        var dialog = new LinkCaptionDialog(currentCaption, entry.Url) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        entry.Url = dialog.LinkUrl;
        entry.CustomCaption = dialog.LinkText;
        SyncLinksToMessage();
    }

    private void AddManagedLinks_Click(object sender, RoutedEventArgs e) =>
        AddUrlsFromText(LinkPasteBox?.Text, clearInput: true);

    private void ClearManagedLinks_Click(object sender, RoutedEventArgs e)
    {
        if (_managedLinks.Count == 0)
            return;
        _managedLinks.Clear();
        SyncLinksToMessage();
    }

    private void RemoveManagedLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ManagedLinkEntry entry })
            return;
        _managedLinks.Remove(entry);
        SyncLinksToMessage();
    }

    private void LinkPasteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        e.Handled = true;
        AddUrlsFromText(LinkPasteBox.Text, clearInput: true);
    }

    private void LinkPasteBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            return;
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
        if (string.IsNullOrWhiteSpace(text) || LinkTemplateEngine.ExtractUrls(text).Count == 0)
            return;

        e.CancelCommand();
        AddUrlsFromText(text, clearInput: true);
    }

    private void AddUrlsFromText(string? text, bool clearInput)
    {
        var urls = LinkTemplateEngine.ExtractUrls(text);
        if (urls.Count == 0)
        {
            var single = (text ?? string.Empty).Trim();
            if (LinkTemplateEngine.LooksLikeUrl(single))
                urls = new[] { single.TrimEnd('.', ',', ';', ')', ']') };
            else
                return;
        }

        var added = 0;
        foreach (var url in urls)
        {
            if (_managedLinks.Any(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                continue;
            _managedLinks.Add(new ManagedLinkEntry { Url = url });
            added++;
        }

        if (clearInput && LinkPasteBox is not null)
            LinkPasteBox.Text = string.Empty;

        if (added > 0 || urls.Count > 0)
            SyncLinksToMessage();
    }

    private void SyncLinksToMessage()
    {
        RenderManagedLinks();
        RebuildTelegramMessage();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_hasTelegram || e.Key != Key.K || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;
        EditSelectedLinkInMessage();
    }

    private void EditSelectedLinkInMessage()
    {
        TelegramMessageBox.Focus();
        var existing = FindHyperlinkAtSelection();

        string caption;
        string url;
        if (existing is not null)
        {
            caption = ReadLinkText(existing);
            url = ResolveHyperlinkUrl(existing);
        }
        else if (!string.IsNullOrWhiteSpace(TelegramMessageBox.Selection.Text))
        {
            caption = TelegramMessageBox.Selection.Text.Trim();
            url = ResolveUrlFromCaption(caption);
        }
        else
        {
            MessageBox.Show(this,
                "Выделите ссылку или текст в сообщении, затем нажмите Ctrl+K.",
                "Текст ссылки",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new LinkCaptionDialog(caption, url) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (existing is not null)
        {
            existing.Inlines.Clear();
            existing.Inlines.Add(new Run(dialog.LinkText));
            if (Uri.TryCreate(dialog.LinkUrl, UriKind.Absolute, out var uri))
            {
                existing.NavigateUri = uri;
                var entry = _managedLinks.FirstOrDefault(l =>
                    l.Url.Equals(url, StringComparison.OrdinalIgnoreCase)
                    || l.Url.Equals(dialog.LinkUrl, StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    entry.Url = dialog.LinkUrl;
                    entry.CustomCaption = dialog.LinkText;
                    RenderManagedLinks();
                }
            }

            return;
        }

        if (!string.IsNullOrEmpty(TelegramMessageBox.Selection.Text))
            TelegramMessageBox.Selection.Text = string.Empty;

        var inserted = new Hyperlink(new Run(dialog.LinkText), TelegramMessageBox.CaretPosition)
        {
            Foreground = (Brush)FindResource("FgInfoBrush"),
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline
        };
        if (Uri.TryCreate(dialog.LinkUrl, UriKind.Absolute, out var insertedUri))
        {
            inserted.NavigateUri = insertedUri;
            inserted.Click += (_, _) => OpenUri(insertedUri);
        }
    }

    private Hyperlink? FindHyperlinkAtSelection()
    {
        var selection = TelegramMessageBox.Selection;
        var found = FindHyperlink(selection.Start)
                    ?? FindHyperlink(selection.End)
                    ?? FindHyperlink(TelegramMessageBox.CaretPosition);
        if (found is not null)
            return found;

        // Курсор/выделение рядом со ссылкой или на её тексте — ищем пересечение.
        foreach (var link in EnumerateHyperlinks(TelegramMessageBox.Document))
        {
            if (selection.IsEmpty)
            {
                var caret = TelegramMessageBox.CaretPosition;
                if (caret is not null
                    && caret.CompareTo(link.ElementStart) >= 0
                    && caret.CompareTo(link.ElementEnd) <= 0)
                    return link;
                continue;
            }

            if (selection.Start.CompareTo(link.ElementEnd) < 0
                && selection.End.CompareTo(link.ElementStart) > 0)
                return link;
        }

        // Текст выделения совпадает с подписью одной из ссылок в сообщении.
        var selected = selection.Text.Trim();
        if (selected.Length == 0)
            return null;
        return EnumerateHyperlinks(TelegramMessageBox.Document)
            .FirstOrDefault(link => ReadLinkText(link).Equals(selected, StringComparison.Ordinal));
    }

    private static IEnumerable<Hyperlink> EnumerateHyperlinks(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            if (block is not Paragraph paragraph)
                continue;
            foreach (var link in EnumerateHyperlinksInInlines(paragraph.Inlines))
                yield return link;
        }
    }

    private static IEnumerable<Hyperlink> EnumerateHyperlinksInInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Hyperlink link:
                    yield return link;
                    break;
                case Span span:
                    foreach (var nested in EnumerateHyperlinksInInlines(span.Inlines))
                        yield return nested;
                    break;
            }
        }
    }

    private string ResolveHyperlinkUrl(Hyperlink link)
    {
        if (link.NavigateUri is not null)
            return link.NavigateUri.ToString();

        if (link.Tag is string tagUrl && !string.IsNullOrWhiteSpace(tagUrl))
            return tagUrl;

        return ResolveUrlFromCaption(ReadLinkText(link));
    }

    private string ResolveUrlFromCaption(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return string.Empty;

        if (Uri.TryCreate(caption.Trim(), UriKind.Absolute, out var asUrl)
            && (asUrl.Scheme == Uri.UriSchemeHttp || asUrl.Scheme == Uri.UriSchemeHttps))
            return asUrl.ToString();

        var fromList = _managedLinks.FirstOrDefault(l =>
            (!string.IsNullOrWhiteSpace(l.CustomCaption)
             && l.CustomCaption.Trim().Equals(caption, StringComparison.Ordinal))
            || LinkTemplateEngine.FormatUrl(l.Url, _linkTemplates).Caption
                .Equals(caption, StringComparison.Ordinal));
        if (fromList is not null)
            return fromList.Url;

        if (!string.IsNullOrWhiteSpace(_linkUrl)
            && AutoPrTemplate.LinkCaptionOrDefault(_linkCaption)
                .Equals(caption, StringComparison.Ordinal))
            return _linkUrl;

        return string.Empty;
    }

    private static Hyperlink? FindHyperlink(TextPointer? pointer)
    {
        if (pointer is null)
            return null;

        DependencyObject? current = pointer.Parent;
        while (current is not null)
        {
            if (current is Hyperlink link)
                return link;
            current = current is FrameworkContentElement element
                ? element.Parent
                : VisualTreeHelper.GetParent(current);
        }

        // Иногда Parent у указателя пустой — смотрим соседний контекст.
        current = pointer.GetAdjacentElement(LogicalDirection.Forward) as DependencyObject
                  ?? pointer.GetAdjacentElement(LogicalDirection.Backward) as DependencyObject;
        while (current is not null)
        {
            if (current is Hyperlink link)
                return link;
            current = current is FrameworkContentElement element
                ? element.Parent
                : null;
        }

        return null;
    }

    private static string ReadLinkText(Hyperlink link) =>
        new TextRange(link.ContentStart, link.ContentEnd).Text.Trim();

    private void Open_Click(object sender, RoutedEventArgs e) => OpenUri(UrlBox.Text);

    private static void OpenUri(Uri? uri)
    {
        if (uri is null)
            return;
        OpenUri(uri.ToString());
    }

    private static void OpenUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(UrlBox.Text))
            TrySetClipboard(UrlBox.Text);
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e) => CopyTelegramMessage();

    private void TelegramMessageBox_Copying(object sender, DataObjectCopyingEventArgs e)
    {
        var data = TelegramMessageClipboard.Create(TelegramMessageBox.Document);
        if (data is null)
            return;

        e.CancelCommand();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Буфер обмена", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void CopyTelegramMessage()
    {
        try
        {
            if (!TelegramMessageClipboard.TryCopy(TelegramMessageBox.Document))
                return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Буфер обмена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Буфер обмена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class ManagedLinkEntry
    {
        public string Url { get; set; } = string.Empty;
        public string? CustomCaption { get; set; }
    }
}

public sealed class TelegramCompose
{
    public string Template { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string LinkCaption { get; init; } = string.Empty;
    public string LinkUrl { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public IReadOnlyList<TelegramCustomTemplate>? Custom { get; init; }
    public IReadOnlyList<TelegramTimeTextRule>? TimeTexts { get; init; }
    public string? TimeZoneId { get; init; }
    public IReadOnlyList<LinkTemplateRule>? LinkTemplates { get; init; }
}
