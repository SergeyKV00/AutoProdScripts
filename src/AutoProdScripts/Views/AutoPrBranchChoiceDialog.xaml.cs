using System.Windows;

namespace AutoProdScripts.Views;

public enum AutoPrBranchChoice
{
    Current,
    New
}

public partial class AutoPrBranchChoiceDialog : Window
{
    public AutoPrBranchChoice Choice { get; private set; } = AutoPrBranchChoice.Current;

    public AutoPrBranchChoiceDialog(string branch, int changeCount, bool hasActivePullRequest = false)
    {
        InitializeComponent();
        var changes = changeCount > 0
            ? $" Есть изменения ({changeCount}): они будут закоммичены и запушены."
            : string.Empty;
        if (hasActivePullRequest)
        {
            CurrentBranchButton.Content = "В текущую ветку";
            MessageText.Text =
                $"Для ветки «{branch}» уже есть активный pull request.{changes} Запушить в текущую ветку или создать новую?";
            return;
        }

        MessageText.Text =
            $"Для ветки «{branch}» нет активного pull request.{changes} Создать PR из текущей ветки или из новой?";
    }

    private void Current_Click(object sender, RoutedEventArgs e)
    {
        Choice = AutoPrBranchChoice.Current;
        DialogResult = true;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        Choice = AutoPrBranchChoice.New;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
