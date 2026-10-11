using System.Windows;

namespace AutoProdScripts.Views;

public partial class NewFolderDialog : Window
{
    public string FolderName { get; private set; } = string.Empty;

    public NewFolderDialog(string locationCaption)
    {
        InitializeComponent();
        FolderText.Text = locationCaption;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Укажите имя папки.", "Новая папка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FolderName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
