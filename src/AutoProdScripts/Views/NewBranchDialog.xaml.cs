using System.Windows;

namespace AutoProdScripts.Views;

public partial class NewBranchDialog : Window
{
    public string? SelectedBaseBranch { get; private set; }
    public string? NewBranchName { get; private set; }

    public NewBranchDialog(IEnumerable<string> branches, string? preferredBase)
    {
        InitializeComponent();
        foreach (var b in branches.Distinct(StringComparer.OrdinalIgnoreCase))
            BaseBranchBox.Items.Add(b);

        BaseBranchBox.Text = preferredBase ?? "master";
        AutoName_Click(this, new RoutedEventArgs());
        BaseBranchBox.SelectionChanged += (_, _) =>
        {
            if (BranchNameBox.Text.Contains('_'))
                AutoName_Click(this, new RoutedEventArgs());
        };
        BaseBranchBox.LostFocus += (_, _) => { /* keep manual name if user edited */ };
    }

    private void AutoName_Click(object sender, RoutedEventArgs e)
    {
        var baseName = string.IsNullOrWhiteSpace(BaseBranchBox.Text) ? "master" : BaseBranchBox.Text.Trim();
        // strip remotes/ prefix style if any
        if (baseName.Contains('/'))
            baseName = baseName.Split('/').Last();
        BranchNameBox.Text = $"{baseName}_{DateTime.Now:yyyyMMddHHmm}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BaseBranchBox.Text) || string.IsNullOrWhiteSpace(BranchNameBox.Text))
        {
            MessageBox.Show(this, "Укажите базовую ветку и имя новой ветки.", "Новая ветка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedBaseBranch = BaseBranchBox.Text.Trim();
        NewBranchName = BranchNameBox.Text.Trim();
        DialogResult = true;
        Close();
    }
}
