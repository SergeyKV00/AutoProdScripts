using System.Diagnostics;
using System.Windows;

namespace AutoProdScripts.Views;

public partial class PullRequestResultDialog : Window
{
    public PullRequestResultDialog(string title, string status, string? url, bool success)
    {
        InitializeComponent();
        TitleText.Text = title;
        StatusText.Text = status;
        UrlBox.Text = url ?? string.Empty;
        OpenButton.IsEnabled = success && !string.IsNullOrWhiteSpace(url);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(UrlBox.Text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(UrlBox.Text))
            Clipboard.SetText(UrlBox.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
