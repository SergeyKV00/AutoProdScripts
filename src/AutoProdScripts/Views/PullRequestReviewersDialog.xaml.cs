using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoProdScripts.Models;
using AutoProdScripts.Services.AzureDevOps;

namespace AutoProdScripts.Views;

public partial class PullRequestReviewersDialog : Window
{
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly string _repo;
    private readonly AzureDevOpsAuthorization _authorization;
    private readonly List<SavedReviewer> _saved;
    private readonly CancellationTokenSource _cts = new();
    private List<ReviewerRow> _rows = new();

    public IReadOnlyList<SavedReviewer> SelectedReviewers { get; private set; } = Array.Empty<SavedReviewer>();

    public PullRequestReviewersDialog(
        string orgUrl,
        string project,
        string repo,
        AzureDevOpsAuthorization authorization,
        IReadOnlyList<SavedReviewer>? saved)
    {
        InitializeComponent();
        _orgUrl = orgUrl;
        _project = project;
        _repo = repo;
        _authorization = authorization;
        _saved = (saved ?? Array.Empty<SavedReviewer>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => r.Copy())
            .ToList();

        Loaded += OnLoaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Cursor = Cursors.Wait;
            await LoadAsync(_cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_cts.IsCancellationRequested)
                return;

            App.Log.Error("ревьюверы: " + ex.Message);
            LoadStatusText.Text = ex.Message;
            LoadStatusText.Foreground = ErrorBrush();
            BindPeople(Array.Empty<ProjectMember>(), failed: true);
        }
        finally
        {
            if (!_cts.IsCancellationRequested)
            {
                Cursor = Cursors.Arrow;
                ContinueButton.IsEnabled = true;
                UpdateSummary();
            }
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        ProjectMembersResult result;
        using (var client = new AzureDevOpsClient(_orgUrl, _project, _repo, _authorization))
            result = await client.ListProjectMembersAsync(ct);

        if (ct.IsCancellationRequested)
            return;

        if (!string.IsNullOrWhiteSpace(result.Error))
            App.Log.Warn("ревьюверы: " + result.Error);
        else
            App.Log.Info($"ревьюверы: загружено {result.Members.Count}");

        if (result.Members.Count == 0 && !string.IsNullOrWhiteSpace(result.Error))
        {
            LoadStatusText.Text = result.Error;
            LoadStatusText.Foreground = ErrorBrush();
        }
        else if (!string.IsNullOrWhiteSpace(result.Error))
        {
            LoadStatusText.Text = $"Загружено сотрудников: {result.Members.Count}. {result.Error}";
            LoadStatusText.Foreground = WarnBrush();
        }
        else
        {
            LoadStatusText.Text = $"Загружено сотрудников: {result.Members.Count}.";
            LoadStatusText.Foreground = InfoBrush();
        }

        BindPeople(result.Members, failed: false);
    }

    private void BindPeople(IReadOnlyList<ProjectMember> people, bool failed)
    {
        var byId = new Dictionary<string, ReviewerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
            byId[person.Id] = new ReviewerRow(person.Id, person.DisplayName, person.UniqueName, fromDirectory: true);

        foreach (var saved in _saved)
        {
            if (!byId.TryGetValue(saved.Id, out var row))
            {
                var display = string.IsNullOrWhiteSpace(saved.DisplayName)
                    ? (string.IsNullOrWhiteSpace(saved.UniqueName) ? saved.Id : saved.UniqueName)
                    : saved.DisplayName;
                row = new ReviewerRow(saved.Id, display, saved.UniqueName, fromDirectory: false);
                byId[saved.Id] = row;
            }

            row.ApplySaved(saved.IsRequired);
        }

        foreach (var row in _rows)
            row.PropertyChanged -= OnRowChanged;

        _rows = byId.Values
            .OrderByDescending(r => r.IsSelected)
            .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var row in _rows)
            row.PropertyChanged += OnRowChanged;

        if (failed && _rows.Count > 0)
            LoadStatusText.Text += " Показан сохранённый выбор.";

        ApplyFilter();
        UpdateSummary();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

    private void ApplyFilter()
    {
        if (SearchBox is null || ReviewersList is null || EmptyText is null)
            return;

        var query = SearchBox.Text.Trim();
        IEnumerable<ReviewerRow> rows = _rows;
        if (query.Length > 0)
        {
            rows = rows.Where(r =>
                r.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                r.UniqueName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = rows.ToList();
        ReviewersList.ItemsSource = list;
        EmptyText.Text = _rows.Count == 0
            ? "Список пуст. Можно продолжить без ревьюверов."
            : "Никого не найдено.";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        var selected = _rows.Count(r => r.IsSelected);
        var required = _rows.Count(r => r.IsSelected && r.IsRequired);
        SummaryText.Text =
            $"Выбрано: {selected}. Обязательных: {required}. Опциональных: {selected - required}. Выбор сохранится для следующих PR.";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsSelected = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedReviewers = _rows
            .Where(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => new SavedReviewer
            {
                Id = r.Id,
                DisplayName = r.DisplayName,
                UniqueName = r.UniqueName,
                IsRequired = r.IsRequired
            })
            .ToList();
        DialogResult = true;
    }

    private static SolidColorBrush InfoBrush() => Brush(0x9C, 0xDC, 0xFE);
    private static SolidColorBrush WarnBrush() => Brush(0xDC, 0xDC, 0xAA);
    private static SolidColorBrush ErrorBrush() => Brush(0xF4, 0x87, 0x71);

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed class ReviewerRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isRequired;

        public ReviewerRow(string id, string displayName, string uniqueName, bool fromDirectory)
        {
            Id = id;
            DisplayName = displayName;
            UniqueName = uniqueName;
            FromDirectory = fromDirectory;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string UniqueName { get; }
        public bool FromDirectory { get; }

        public string SecondaryText =>
            FromDirectory
                ? UniqueName
                : "сохранён ранее, среди участников команд сейчас не найден";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                if (!value && _isRequired)
                {
                    _isRequired = false;
                    OnPropertyChanged(nameof(IsRequired));
                }

                OnPropertyChanged();
            }
        }

        public bool IsRequired
        {
            get => _isRequired;
            set
            {
                if (_isRequired == value)
                    return;
                _isRequired = value;
                if (value && !_isSelected)
                {
                    _isSelected = true;
                    OnPropertyChanged(nameof(IsSelected));
                }

                OnPropertyChanged();
            }
        }

        public void ApplySaved(bool isRequired)
        {
            _isSelected = true;
            if (isRequired)
                _isRequired = true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
