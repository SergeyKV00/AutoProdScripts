using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Connections;

namespace AutoProdScripts.Views.Queries;

/// <summary>Мастер одного подключения: создание, настройки или «на основе текущего». Проверка — до сохранения.</summary>
internal sealed class ConnectionDialog : Window
{
    private readonly ConnectionsEditor _editor = new();
    private bool _saved;

    /// <param name="isNew">true — сохранится как новое подключение.</param>
    /// <param name="copyPasswordFrom">Чей пароль взять, если новый не введён.</param>
    public ConnectionDialog(DbConnectionProfile profile, bool isNew, Guid? copyPasswordFrom = null)
    {
        Title = isNew ? "Новое подключение" : $"Подключение «{profile.Name}»";
        Width = 620;
        Height = 820;
        MinWidth = 480;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "FgBrush");
        Content = new Border { Padding = new Thickness(16), Child = _editor };

        if (copyPasswordFrom is { } source)
            _editor.CopyPasswordFrom(source);
        _editor.ShowSingle(profile, isNew);
        _editor.Saved += saved =>
        {
            Result = saved;
            _saved = true;
            DialogResult = true;
        };
    }

    /// <summary>Сохранённое подключение.</summary>
    public DbConnectionProfile? Result { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_saved && !_editor.ConfirmDiscard())
            e.Cancel = true;
        base.OnClosing(e);
    }
}

/// <summary>Однострочный ввод: переименование, группа.</summary>
internal sealed class InputDialog : Window
{
    private readonly ComboBox _box = new() { IsEditable = true, MinWidth = 320 };

    public InputDialog(string title, string prompt, string value, IEnumerable<string>? suggestions = null)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "FgBrush");

        if (suggestions is not null)
            _box.ItemsSource = suggestions.ToList();
        _box.Text = value;

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => _box.Focus();
    }

    public string Value => _box.Text.Trim();
}
