using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AutoProdScripts.Core.Execution;
using Microsoft.Win32;

namespace AutoProdScripts.Views.Queries;

/// <summary>Наборы результатов, сообщения, копирование и CSV — общее для скриптов и цепочек.</summary>
public partial class ResultsPanel : UserControl
{
    public ResultsPanel() => InitializeComponent();

    public string Summary
    {
        get => SummaryText.Text;
        set => SummaryText.Text = value;
    }

    public string Messages => MessagesBox.Text;

    public static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) + " с";

    public void Clear()
    {
        while (ResultTabs.Items.Count > 1)
            ResultTabs.Items.RemoveAt(0);
        MessagesBox.Clear();
        SummaryText.Text = string.Empty;
        ResultTabs.SelectedItem = MessagesTab;
        UpdateButtons();
    }

    public void ShowFailure(string message)
    {
        Clear();
        SummaryText.Text = "Не выполнено — подробности в «Сообщениях».";
        MessagesBox.Text = message;
    }

    public void Show(
        IReadOnlyList<(string Title, QueryResultSet Set)> sets,
        IReadOnlyList<string> messages,
        string summary,
        bool openMessages)
    {
        Clear();
        for (var i = 0; i < sets.Count; i++)
        {
            var (title, set) = sets[i];
            ResultTabs.Items.Insert(i, new TabItem
            {
                // Через TextBlock, чтобы «_» в названии не стал горячей клавишей.
                Header = new TextBlock { Text = $"{title} ({set.Rows.Count}{(set.Truncated ? "+" : "")})" },
                Content = CreateGrid(set),
                Tag = set
            });
        }

        MessagesBox.Text = messages.Count == 0 ? "Выполнено без сообщений." : string.Join(Environment.NewLine, messages);
        SummaryText.Text = summary;
        ResultTabs.SelectedIndex = !openMessages && sets.Count > 0 ? 0 : ResultTabs.Items.Count - 1;
        UpdateButtons();
    }

    private static DataGrid CreateGrid(QueryResultSet set)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableColumnVirtualization = true,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader
        };
        // Через шаблон, иначе «_» в имени колонки съедается как горячая клавиша.
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        var header = new DataTemplate { VisualTree = text };

        for (var i = 0; i < set.Columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = set.Columns[i],
                HeaderTemplate = header,
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay, Converter = CellConverter.Instance },
                MaxWidth = 500
            });
        }

        grid.ItemsSource = set.Rows;
        return grid;
    }

    private (QueryResultSet Set, DataGrid Grid)? CurrentResult() =>
        ResultTabs.SelectedItem is TabItem { Tag: QueryResultSet set, Content: DataGrid grid } ? (set, grid) : null;

    private void ResultTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (ReferenceEquals(e.OriginalSource, ResultTabs))
            UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasResult = CurrentResult() is not null;
        CopyButton.IsEnabled = hasResult;
        ExportButton.IsEnabled = hasResult;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResult() is not { } current)
            return;

        var all = current.Grid.SelectedCells.Count <= 1;
        if (all)
            current.Grid.SelectAllCells();
        try
        {
            ApplicationCommands.Copy.Execute(null, current.Grid);
        }
        catch (ExternalException)
        {
            SummaryText.Text = "Буфер обмена занят другой программой, попробуйте ещё раз.";
        }

        if (all)
            current.Grid.UnselectAllCells();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentResult() is not { } current)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"result_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, ResultFormatter.ToCsv(current.Set), new UTF8Encoding(true));
            SummaryText.Text = current.Set.Truncated
                ? "Сохранено. В файле только строки в пределах лимита."
                : "Сохранено в CSV.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = "Не удалось сохранить файл: " + ex.Message;
        }
    }

    private sealed class CellConverter : IValueConverter
    {
        public static readonly CellConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ResultFormatter.Format(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
