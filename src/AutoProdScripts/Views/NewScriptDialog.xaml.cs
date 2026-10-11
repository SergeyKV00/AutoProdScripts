using System.IO;
using System.Windows;
using System.Windows.Input;

namespace AutoProdScripts.Views;

public partial class NewScriptDialog : Window
{
    private const string NameTemplate = "FileName";
    private readonly string _projectRoot;
    private readonly string _autoFileName;
    private readonly List<FolderChoice> _folders;

    public string? CreatedFilePath { get; private set; }

    public NewScriptDialog(string projectRoot, IEnumerable<string> folders, string? preferredFolder)
    {
        InitializeComponent();
        _projectRoot = projectRoot;
        _autoFileName = $"{DateTime.Now:yyyyMMddHHmm}.sql";
        var hint = $"Кнопка «Авто» создаст файл {_autoFileName}";
        AutoHint.Text = hint;
        AutoButton.ToolTip = hint;

        _folders = new List<FolderChoice> { new(".") };
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(folder) && folder != ".")
                _folders.Add(new FolderChoice(folder.Replace('\\', '/')));
        }

        ShowFolders(_folders);
        var preferred = string.IsNullOrWhiteSpace(preferredFolder) ? "." : preferredFolder.Replace('\\', '/');
        FolderList.SelectedItem = _folders.FirstOrDefault(folder =>
            folder.Relative.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? _folders[0];
        FolderList.ScrollIntoView(FolderList.SelectedItem);
        Loaded += (_, _) => SelectFileNameTemplate();
    }

    private void FolderSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = FolderSearch.Text.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _folders
            : _folders.Where(folder => folder.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = FolderList.SelectedItem as FolderChoice;
        ShowFolders(matches);
        if (selected is not null && matches.Contains(selected))
            FolderList.SelectedItem = selected;
        else if (FolderList.Items.Count > 0)
            FolderList.SelectedIndex = 0;
    }

    private void ShowFolders(IReadOnlyList<FolderChoice> folders)
    {
        FolderList.Items.Clear();
        foreach (var folder in folders)
            FolderList.Items.Add(folder);
    }

    private void SelectFileNameTemplate()
    {
        FileNameBox.Focus();
        Keyboard.Focus(FileNameBox);
        FileNameBox.Select(0, NameTemplate.Length);
    }

    private void Auto_Click(object sender, RoutedEventArgs e) => CreateFile(_autoFileName);

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

        CreateFile(name);
    }

    private void CreateFile(string name)
    {
        if (FolderList.SelectedItem is not FolderChoice folder)
        {
            MessageBox.Show(this, "Выберите папку.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        name = name.Trim();
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            name += ".sql";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            MessageBox.Show(this, "Имя файла содержит недопустимые символы.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dir = folder.Relative == "."
            ? _projectRoot
            : Path.Combine(_projectRoot, folder.Relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var fullPath = Path.Combine(dir, name);
        if (File.Exists(fullPath))
        {
            MessageBox.Show(this, "Файл уже существует. Выберите другое имя.", "Новый скрипт",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        File.WriteAllText(fullPath, string.Empty);
        CreatedFilePath = fullPath;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class FolderChoice(string relative)
    {
        public string Relative { get; } = relative;
        public string Label { get; } = relative == "." ? "корень проекта" : relative;
    }
}
