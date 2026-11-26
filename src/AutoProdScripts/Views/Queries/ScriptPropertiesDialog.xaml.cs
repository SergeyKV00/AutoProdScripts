using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Library;

namespace AutoProdScripts.Views.Queries;

public partial class ScriptPropertiesDialog : Window
{
    private readonly LibraryScript _script;
    private readonly List<(ScriptParameter Source, TextBox Label, ComboBox Kind, TextBox Default)> _parameters = new();

    public ScriptPropertiesDialog(LibraryScript script)
    {
        InitializeComponent();
        _script = script.Clone();
        NameBox.Text = _script.Name;
        GroupBox.ItemsSource = App.Library.Groups();
        GroupBox.Text = _script.Group;
        TagsBox.Text = string.Join(", ", _script.Tags);
        DescriptionBox.Text = _script.Description;
        FillConnections();
        FillParameters();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Заполнено после успешного сохранения.</summary>
    public LibraryScript? Saved { get; private set; }

    private void FillConnections()
    {
        var connections = App.Connections.GetAll();
        var current = ScriptConnection.Resolve(_script, connections);
        ConnectionBox.Items.Add(new ComboBoxItem { Content = "(не задано)" });
        ConnectionBox.SelectedIndex = 0;
        foreach (var profile in connections)
        {
            var item = new ComboBoxItem { Content = profile.Name, Tag = (profile.Id, profile.Name) };
            ConnectionBox.Items.Add(item);
            if (profile.Id == current?.Id)
                ConnectionBox.SelectedItem = item;
        }

        // Подключения с таким именем на этом ПК нет — не теряем ссылку при сохранении.
        if (current is null && _script.ConnectionName.Length > 0)
        {
            var missing = new ComboBoxItem
            {
                Content = $"{_script.ConnectionName} (нет на этом ПК)",
                Tag = (_script.ConnectionId ?? Guid.Empty, _script.ConnectionName)
            };
            ConnectionBox.Items.Add(missing);
            ConnectionBox.SelectedItem = missing;
        }
    }

    private void FillParameters()
    {
        ParametersHint.Text = _script.Parameters.Count == 0
            ? "В скрипте нет параметров."
            : "Подпись, тип и значение по умолчанию. Значение по умолчанию хранится в файле скрипта — "
              + "не записывайте туда персональные данные и пароли. Пустое поле — значения нет.";

        if (_script.Parameters.Count == 0)
            return;

        ParametersHost.Children.Add(Row(Header("Имя"), Header("Подпись"), Header("Тип"), Header("По умолчанию")));
        foreach (var parameter in _script.Parameters)
        {
            var label = new TextBox { Text = parameter.Label };
            var kind = new ComboBox
            {
                ItemsSource = ParameterKinds.Titles,
                SelectedIndex = ParameterKinds.IndexOf(parameter.Kind)
            };
            var value = new TextBox { Text = parameter.DefaultValue ?? string.Empty };
            var name = new TextBlock { Text = parameter.Name, VerticalAlignment = VerticalAlignment.Center };
            ParametersHost.Children.Add(Row(name, label, kind, value));
            _parameters.Add((parameter, label, kind, value));
        }
    }

    private static TextBlock Header(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(TextBlock.ForegroundProperty, "FgMutedBrush");
        return block;
    }

    private static Grid Row(params FrameworkElement[] cells)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        double[] widths = [1, 1.3, 1.5, 1.2];
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i], GridUnitType.Star) });
            cells[i].Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }

        return grid;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var script = _script.Clone();
        script.Name = NameBox.Text;
        script.Group = GroupBox.Text;
        script.Tags = ScriptLibrary.ParseTags(TagsBox.Text);
        script.Description = DescriptionBox.Text;

        if ((ConnectionBox.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<Guid, string> connection)
        {
            script.ConnectionId = connection.Item1 == Guid.Empty ? null : connection.Item1;
            script.ConnectionName = connection.Item2;
        }
        else
        {
            script.ConnectionId = null;
            script.ConnectionName = string.Empty;
        }

        try
        {
            script.Parameters = _parameters.Select(row =>
            {
                var parameter = row.Source.Clone();
                parameter.Label = row.Label.Text.Trim();
                parameter.Kind = ParameterKinds.At(row.Kind.SelectedIndex);
                parameter.DefaultValue = row.Default.Text.Length == 0 ? null : row.Default.Text;
                if (parameter.DefaultValue is not null)
                    new SqlParameterValue(parameter.Name, parameter.Kind, parameter.DefaultValue).ToSqlParameter();
                return parameter;
            }).ToList();

            Saved = App.Library.Save(script);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
