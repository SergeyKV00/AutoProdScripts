using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AutoProdScripts.Views;

public partial class NewScriptDialog : Window
{
    private readonly string _projectRoot;

    public string? CreatedFilePath { get; private set; }

    public NewScriptDialog(string projectRoot, IEnumerable<string> folders, string? preferredFolder)
    {
        InitializeComponent();
        _projectRoot = projectRoot;

        FolderBox.Items.Add(".");
        foreach (var f in folders)
            FolderBox.Items.Add(f);

        FolderBox.Text = string.IsNullOrWhiteSpace(preferredFolder) ? "." : preferredFolder;
        FileNameBox.Text = $"fix_{DateTime.Now:yyyyMMddHHmm}.sql";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var folderRel = string.IsNullOrWhiteSpace(FolderBox.Text) ? "." : FolderBox.Text.Trim();
        var name = (FileNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Укажите имя файла.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            name += ".sql";

        var dir = folderRel == "."
            ? _projectRoot
            : Path.Combine(_projectRoot, folderRel.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, name);
        if (File.Exists(fullPath))
        {
            MessageBox.Show(this, "Файл уже существует. Выберите другое имя.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var content = BuildTemplate(name);
        // UTF-8 without BOM by default (MVP); encoding policy TBD
        File.WriteAllText(fullPath, content);
        CreatedFilePath = fullPath;
        DialogResult = true;
        Close();
    }

    private string BuildTemplate(string fileName)
    {
        var selected = (TemplateBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Пустой";
        return selected switch
        {
            "Header (author / date)" =>
                $"-- Файл: {fileName}\n-- Автор: {Environment.UserName}\n-- Дата: {DateTime.Now:yyyy-MM-dd HH:mm}\n\n",
            "BEGIN TRAN / COMMIT" =>
                $"-- Файл: {fileName}\n-- Автор: {Environment.UserName}\n-- Дата: {DateTime.Now:yyyy-MM-dd HH:mm}\n\nBEGIN TRAN;\n\n-- TODO: SQL\n\n-- COMMIT;\n-- ROLLBACK;\n",
            _ => string.Empty
        };
    }
}
