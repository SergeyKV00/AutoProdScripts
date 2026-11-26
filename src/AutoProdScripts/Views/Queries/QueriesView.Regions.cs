using System.Windows;
using System.Windows.Controls;
using AutoProdScripts.Core.Connections;
using AutoProdScripts.Core.Execution;
using AutoProdScripts.Core.Regions;
using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Views.Queries;

/// <summary>Запуск скрипта по регионам.</summary>
public partial class QueriesView
{
    private const string RegionalTag = "regions";

    private bool IsRegional => (ConnectionBox.SelectedItem as ComboBoxItem)?.Tag is RegionalTag;

    private void InitializeRegions()
    {
        Regions.Reload();
        Regions.SelectionChanged += () =>
        {
            if (_active is { IsChain: false } doc)
            {
                doc.RegionCodes = Regions.Selected.ToList();
                ScheduleSessionSave();
            }

            UpdateBadge();
        };
        App.Regions.Changed += () =>
        {
            Regions.Reload();
            if (_active is { IsChain: false } doc)
                Regions.SetSelected(doc.RegionCodes);
            UpdateBadge();
        };
    }

    private void RegionTemplate_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _switching)
            return;
        if (_active is { IsChain: false } doc)
        {
            doc.RegionDatabase = RegionTemplateBox.Text.Trim();
            RefreshChrome(doc);
            ScheduleSessionSave();
        }

        UpdateBadge();
    }

    private void UpdateRegionBadge()
    {
        var codes = Regions.Selected;
        var template = RegionTemplateBox.Text.Trim();
        var profiles = codes.Select(code => RegionTargets.Build(code, template).Profile).OfType<DbConnectionProfile>().ToList();

        // Показываем самую опасную среду из выбранных.
        // Группы кластеров выбранных регионов.
        var connections = App.Connections.GetAll();
        ShowGroups(codes
            .Select(code => App.Regions.GetAll().FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)))
            .Select(r => r is null ? null : ScriptConnection.Resolve(r.ConnectionId, r.ConnectionName, connections))
            .OfType<DbConnectionProfile>()
            .Select(ConnectionUi.GroupOf));
        GroupBadge.ToolTip = string.Join(Environment.NewLine, profiles.Select(p => $"{p.Database} · {p.Server}"));

        var problem = RegionDatabase.Validate(template);
        TargetText.Text = problem ?? string.Join(", ", codes.Select(c => RegionDatabase.Resolve(template, c)));
        TargetText.ToolTip = TargetText.Text;
        TargetText.SetResourceReference(TextBlock.ForegroundProperty, problem is null ? "FgInfoBrush" : "WarnBrush");
    }

    private async Task RunRegionsAsync(QueryDocument doc, string sql, SqlAnalysis analysis, int maxRows, bool selection)
    {
        var template = doc.RegionDatabase.Trim();
        // Запускается всегда активная вкладка; в списке — только регионы, которые есть в справочнике.
        var codes = Regions.Selected.ToList();
        var results = doc.Results;
        if (RegionDatabase.Validate(template) is { } problem)
        {
            results.ShowFailure(problem);
            return;
        }

        if (codes.Count == 0)
        {
            results.ShowFailure(App.Regions.GetAll().Count == 0
                ? "Сначала настройте регионы: «Подключения…» → вкладка «Регионы»."
                : "Отметьте регионы, по которым выполнить запрос.");
            return;
        }

        var source = new CancellationTokenSource();
        var scriptId = doc.ScriptId;
        SetBusy(doc, source);
        results.Clear();
        results.Summary = $"Выполняется по регионам: 0 из {codes.Count}…";
        var done = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var targets = codes.Select(code => RegionTargets.Build(code, template).Target).ToList();
            var parameters = analysis.Parameters.Where(doc.Rows.ContainsKey).Select(name => doc.Rows[name].Value()).ToList();
            var progress = new Progress<RegionOutcome>(_ =>
            {
                // Поздние сообщения не должны затереть итог.
                if (doc.IsRunning)
                    results.Summary = $"Выполняется по регионам: {++done} из {codes.Count}…";
            });

            App.Log.Info($"Запрос по регионам: {codes.Count}.");
            var outcomes = await RegionRunner.RunAsync(targets, sql, parameters, maxRows, progress, source.Token);
            doc.Running = null;
            ShowRegionResults(results, outcomes, selection, watch.Elapsed);
            App.Log.Info($"Запрос по регионам завершён за {ResultsPanel.Seconds(watch.Elapsed)}, с ошибкой: {outcomes.Count(o => !o.Success)}.");
            if (scriptId is { } id)
                MarkRun(id);
        }
        catch (OperationCanceledException)
        {
            results.ShowFailure("Выполнение отменено.");
            App.Log.Info("Запрос отменён.");
        }
        catch (Exception ex)
        {
            results.ShowFailure(ex.Message);
            App.Log.Warn("Запрос по регионам не выполнен.");
        }
        finally
        {
            SetBusy(doc, null);
            source.Dispose();
        }
    }

    private static void ShowRegionResults(ResultsPanel results, IReadOnlyList<RegionOutcome> outcomes, bool selection, TimeSpan elapsed)
    {
        var merged = RegionRunner.Merge(outcomes);
        var failed = outcomes.Where(o => !o.Success).ToList();
        var truncated = merged.SelectMany(m => m.Truncated).Distinct().ToList();

        var messages = new List<string>();
        // Ошибки первыми: по неполной выгрузке нельзя делать выводы о стране.
        messages.AddRange(failed.Select(o => $"{o.Code}: ОШИБКА — {o.Error}"));
        foreach (var outcome in outcomes.Where(o => o.Success))
        {
            messages.Add($"{outcome.Code}: строк {outcome.Result!.ResultSets.Sum(s => s.Rows.Count)}");
            messages.AddRange(outcome.Result.Messages.Select(m => "    " + m));
        }

        // В заголовке группы — код и название региона из справочника.
        var titles = App.Regions.GetAll().ToDictionary(r => r.Code, r => r.Title, StringComparer.OrdinalIgnoreCase);
        var sets = merged.Select(m =>
        {
            var sameColumns = merged.Count(x => x.Number == m.Number) == 1;
            var title = sameColumns ? $"Все регионы, набор {m.Number}" : $"Набор {m.Number}: {string.Join(", ", m.Regions)}";
            var groups = m.Set.RowGroups!.Select(code => titles.GetValueOrDefault(code, code)).ToList();
            return (title, m.Set with { RowGroups = groups });
        }).ToList();

        var summary = $"Регионов: {outcomes.Count}, успешно: {outcomes.Count - failed.Count}"
                      + $" · строк: {merged.Sum(m => m.Set.Rows.Count)} · {ResultsPanel.Seconds(elapsed)}"
                      + (selection ? " (выделенный фрагмент)" : "");
        if (failed.Count > 0)
            summary = $"⚠ ДАННЫЕ НЕПОЛНЫЕ: ошибка в {string.Join(", ", failed.Select(o => o.Code))}. " + summary;
        if (truncated.Count > 0)
            summary = $"⚠ Обрезано по лимиту строк: {string.Join(", ", truncated)}. " + summary;

        results.Show(sets, messages, summary, openMessages: sets.Count == 0);
        results.Warn(failed.Count > 0 || truncated.Count > 0);
    }
}
