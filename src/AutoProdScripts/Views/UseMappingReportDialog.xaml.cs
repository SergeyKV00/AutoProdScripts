using System.IO;
using System.Windows;
using AutoProdScripts.Models;
using AutoProdScripts.Services.Mapping;

namespace AutoProdScripts.Views;

public partial class UseMappingReportDialog : Window
{
    private readonly string _projectRoot;
    private readonly UseFolderReport _report;
    private UseFolderMapping _mapping;

    public bool MappingChanged { get; private set; }

    public UseMappingReportDialog(string projectRoot, UseFolderReport report, UseFolderMapping mapping)
    {
        InitializeComponent();
        _projectRoot = projectRoot;
        _report = report;
        _mapping = mapping;
        ReportBox.Text = UseFolderMappingFile.FormatReport(report);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var draft = new UseFolderMapping
        {
            FilePath = Path.Combine(_projectRoot, "mapping", "use-folders.draft.yaml"),
            OnMissingMapping = _mapping.OnMissingMapping
        };
        foreach (var folder in _report.UnambiguousFolders)
        {
            var database = folder.DominantUse!;
            if (_report.Databases.Any(item =>
                    item.IsAmbiguous && item.Database.Equals(database, StringComparison.OrdinalIgnoreCase)))
                continue;
            draft.Mappings[database] = folder.Folder;
        }

        try
        {
            UseFolderMappingFile.Save(draft);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Черновик YAML", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(this, $"Черновик записан:\n{draft.FilePath}", "Черновик YAML",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        var added = UseFolderMappingFile.MergeUnambiguous(_mapping, _report, out var skipped);
        MappingChanged = added > 0;
        var text = added == 0
            ? "Новых однозначных пар нет."
            : $"Добавлено пар: {added}.";
        if (skipped.Count > 0)
            text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, skipped);
        MessageBox.Show(this, text, "Mapping", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
