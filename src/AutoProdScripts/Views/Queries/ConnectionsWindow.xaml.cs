using System.ComponentModel;
using System.Windows;

namespace AutoProdScripts.Views.Queries;

public partial class ConnectionsWindow : Window
{
    public ConnectionsWindow() => InitializeComponent();

    private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = !Editor.ConfirmDiscard();
}
