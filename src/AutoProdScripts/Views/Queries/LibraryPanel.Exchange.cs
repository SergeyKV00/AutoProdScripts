using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Library;
using Microsoft.Win32;

namespace AutoProdScripts.Views.Queries;

/// <summary>Выгрузка в файл и запрос импорта.</summary>
public partial class LibraryPanel
{
    public event Action? ImportRequested;

    /// <summary>Перед экспортом даёт сохранить открытую запись; false — отменить экспорт.</summary>
    public Func<bool>? BeforeExport { get; set; }

    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();

    private void ExportAll_Click(object sender, RoutedEventArgs e) => Export(
        () => App.Library.GetAll().Select(s => s.Id).Concat(App.Library.GetChains().Select(c => c.Id)),
        $"library_{DateTime.Now:yyyyMMdd}");

    private void Export_Click(object sender, RoutedEventArgs e) => WithSelected(id =>
    {
        LibraryItem? item = (LibraryItem?)App.Library.Find(id) ?? App.Library.FindChain(id);
        if (item is not null)
            Export(() => [id], item.Name);
    });

    private void ExportGroup_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGroup is not { } group)
            return;

        bool Inside(LibraryItem item) => item.Group.Equals(group, StringComparison.CurrentCultureIgnoreCase)
                                         || item.Group.StartsWith(group + "/", StringComparison.CurrentCultureIgnoreCase);
        Export(
            () => App.Library.GetAll().Cast<LibraryItem>().Concat(App.Library.GetChains()).Where(Inside).Select(i => i.Id),
            group.Replace('/', '_'));
    }

    // Список записей берётся после сохранения: оно могло добавить или изменить их.
    private void Export(Func<IEnumerable<Guid>> ids, string suggestedName)
    {
        if (BeforeExport?.Invoke() == false)
            return;

        var owner = Window.GetWindow(this);
        var result = LibraryExchange.Export(App.Library, ids().ToList());
        var package = result.Package;
        if (!package.Items.Any())
        {
            MessageBox.Show(owner, "Выгружать нечего.", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var message = $"Скриптов: {package.Scripts.Count}, цепочек: {package.Chains.Count}.\n\n"
                      + "Паролей, серверов и значений параметров в файле не будет.\n"
                      + "SQL выгружается как есть — проверьте, нет ли в нём персональных данных."
                      + string.Concat(result.Warnings.Select(w => "\n\n⚠ " + w));
        if (MessageBox.Show(owner, message, "Экспорт", MessageBoxButton.OKCancel,
                result.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = $"Обмен AutoProdScripts (*{LibraryPackage.Extension})|*{LibraryPackage.Extension}",
            FileName = string.Concat(suggestedName.Split(Path.GetInvalidFileNameChars())) + LibraryPackage.Extension
        };
        if (dialog.ShowDialog(owner) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, LibraryExchange.Serialize(package), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "Не удалось сохранить файл: " + ex.Message, "Экспорт", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private string? CurrentGroup => (Tree.SelectedItem as TreeViewItem)?.Tag as string;
}
