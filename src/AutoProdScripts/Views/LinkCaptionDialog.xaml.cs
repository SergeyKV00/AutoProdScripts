using System.Windows;

namespace AutoProdScripts.Views;

public partial class LinkCaptionDialog : Window
{
    public string LinkText { get; private set; } = string.Empty;
    public string LinkUrl { get; private set; } = string.Empty;

    public LinkCaptionDialog(string caption, string url)
    {
        InitializeComponent();
        CaptionBox.Text = caption;
        UrlBox.Text = url;
        Loaded += (_, _) =>
        {
            CaptionBox.Focus();
            CaptionBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var caption = CaptionBox.Text.Trim();
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(caption))
        {
            MessageBox.Show(this, "Укажите текст ссылки.", "Текст ссылки",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Адрес должен быть полной ссылкой, например https://…", "Текст ссылки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LinkText = caption;
        LinkUrl = url;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
