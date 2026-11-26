using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Connections;

namespace AutoProdScripts.Views.Queries;

public partial class ImportConnectionsDialog : Window
{
    public ImportConnectionsDialog(ConnectionParseResult result)
    {
        InitializeComponent();
        foreach (var parsed in result.Connections)
        {
            var profile = parsed.Profile;
            Host.Children.Add(new CheckBox
            {
                IsChecked = true,
                Tag = parsed,
                Margin = new Thickness(0, 0, 0, 8),
                // TextBlock, чтобы «_» в именах не превращался в горячую клавишу.
                Content = new TextBlock { Text = $"{profile.Name}  —  {profile.Server} · {profile.Database}" }
            });
        }

        ErrorsText.Text = string.Join(Environment.NewLine, result.Errors);
    }

    public List<ParsedConnection> Selected { get; } = new();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Selected.AddRange(Host.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (ParsedConnection)c.Tag));
        DialogResult = true;
    }
}
