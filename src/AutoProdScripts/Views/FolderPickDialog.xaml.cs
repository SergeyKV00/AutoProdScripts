using System.Windows;

namespace AutoProdScripts.Views;

public partial class FolderPickDialog : Window
{
    private readonly List<FolderRow> _folders;

    public string? SelectedFolder { get; private set; }

    public FolderPickDialog(IEnumerable<string> folders, string? preferred)
    {
        InitializeComponent();
        _folders = new List<FolderRow> { new(".") };
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = folder.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(relative) && relative != ".")
                _folders.Add(new FolderRow(relative));
        }

        ApplyFilter(string.Empty);
        var current = string.IsNullOrWhiteSpace(preferred) ? "." : preferred.Replace('\\', '/');
        FolderList.SelectedItem = FolderList.Items.OfType<FolderRow>()
            .FirstOrDefault(row => row.Relative.Equals(current, StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter(SearchBox.Text.Trim());

    private void ApplyFilter(string query)
    {
        var selected = FolderList.SelectedItem as FolderRow;
        var matches = string.IsNullOrEmpty(query)
            ? _folders
            : _folders.Where(row => row.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        FolderList.Items.Clear();
        foreach (var row in matches)
            FolderList.Items.Add(row);
        if (selected is not null && matches.Contains(selected))
            FolderList.SelectedItem = selected;
        else if (FolderList.Items.Count > 0)
            FolderList.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderRow row)
        {
            MessageBox.Show(this, "Выберите папку.", "Выбор папки",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedFolder = row.Relative;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class FolderRow(string relative)
    {
        public string Relative { get; } = relative;
        public string Label { get; } = relative == "." ? "корень проекта" : relative;
    }
}
