using System.Windows;
using System.Windows.Input;

namespace AutoProdScripts.Views;

public partial class AddScriptDialog : Window
{
    private const string NameTemplate = "FileName";
    private readonly string _autoFileName;

    public string? ChosenFileName { get; private set; }

    public AddScriptDialog(string folderCaption)
    {
        InitializeComponent();
        FolderText.Text = folderCaption;
        _autoFileName = $"{DateTime.Now:yyyyMMddHHmm}.sql";
        var hint = $"Кнопка «Авто» создаст файл {_autoFileName}";
        AutoHint.Text = hint;
        AutoButton.ToolTip = hint;
        Loaded += (_, _) => SelectFileNameTemplate();
    }

    private void SelectFileNameTemplate()
    {
        FileNameBox.Focus();
        Keyboard.Focus(FileNameBox);
        FileNameBox.Select(0, NameTemplate.Length);
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        ChosenFileName = _autoFileName;
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = FileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals(".sql", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Укажите имя файла.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Information);
            SelectFileNameTemplate();
            return;
        }

        ChosenFileName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
