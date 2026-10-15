using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Models;

namespace AutoProdScripts.Views;

public partial class PullRequestResultDialog : Window
{
    private readonly bool _hasTelegram;
    private Hyperlink? _prLink;
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
        if (telegram is null)
            return;

        TelegramPanel.Visibility = Visibility.Visible;
        CopyMessageButton.Visibility = Visibility.Visible;
        _linkCaption = AutoPrTemplate.LinkCaptionOrDefault(telegram.LinkCaption);
        _linkUrl = string.IsNullOrWhiteSpace(telegram.LinkUrl) ? url ?? string.Empty : telegram.LinkUrl;
        if (!string.IsNullOrWhiteSpace(_linkUrl))
            UrlBox.Text = _linkUrl;
        BuildTelegramDocument(telegram);
        Width = 740;
        Height = 680;
    }

    private void BuildTelegramDocument(TelegramCompose telegram)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        TelegramPreviewRenderer.Render(
            paragraph,
            telegram.Template,
            telegram.Text,
            _linkCaption,
            _linkUrl,
            telegram.Tags,
            telegram.Custom,
            (Brush)FindResource("FgInfoBrush"),
            url => OpenUri(url),
            telegram.TimeTexts,
            telegram.TimeZoneId);
        _prLink = paragraph.Inlines.OfType<Hyperlink>().FirstOrDefault();
        var document = new FlowDocument
        {
            PagePadding = new Thickness(2),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("FgBrush")
        };
        document.Blocks.Add(paragraph);
        TelegramMessageBox.Document = document;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_hasTelegram || e.Key != Key.K || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;
        EditLinkCaption();
    }

    private void EditLinkCaption()
    {
        var existing = FindHyperlink(TelegramMessageBox.Selection.Start)
                       ?? FindHyperlink(TelegramMessageBox.Selection.End);
        var caption = existing is null
            ? (string.IsNullOrWhiteSpace(TelegramMessageBox.Selection.Text) ? _linkCaption : TelegramMessageBox.Selection.Text.Trim())
            : ReadLinkText(existing);
        var url = existing?.NavigateUri?.ToString() ?? _linkUrl;
        var dialog = new LinkCaptionDialog(caption, url) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _linkCaption = dialog.LinkText;
        _linkUrl = dialog.LinkUrl;
        UrlBox.Text = dialog.LinkUrl;
        OpenButton.IsEnabled = true;

        if (existing is not null)
        {
            existing.Inlines.Clear();
            existing.Inlines.Add(new Run(dialog.LinkText));
            if (Uri.TryCreate(dialog.LinkUrl, UriKind.Absolute, out var uri))
                existing.NavigateUri = uri;
            _prLink = existing;
            return;
        }

        if (!string.IsNullOrEmpty(TelegramMessageBox.Selection.Text))
            TelegramMessageBox.Selection.Text = string.Empty;

        var inserted = new Hyperlink(new Run(dialog.LinkText), TelegramMessageBox.CaretPosition)
        {
            Foreground = (Brush)FindResource("FgInfoBrush"),
            Cursor = Cursors.Hand
        };
        if (Uri.TryCreate(dialog.LinkUrl, UriKind.Absolute, out var insertedUri))
            inserted.NavigateUri = insertedUri;
        inserted.Click += (_, _) => OpenUri(inserted.NavigateUri);
        _prLink = inserted;
    }

    private static Hyperlink? FindHyperlink(TextPointer? pointer)
    {
        var current = pointer?.Parent as DependencyObject;
        while (current is not null)
        {
            if (current is Hyperlink link)
                return link;
            current = current is FrameworkContentElement element ? element.Parent : null;
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
}
