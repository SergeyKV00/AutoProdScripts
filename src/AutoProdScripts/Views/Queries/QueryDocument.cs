using System.Windows.Controls;
using AutoProdScripts.Core.Library;
using ICSharpCode.AvalonEdit.Document;

namespace AutoProdScripts.Views.Queries;

/// <summary>
/// Открытая вкладка раздела «Запросы». Не путать со скриптом библиотеки: вкладка лишь может быть
/// связана с ним (<see cref="Saved"/>), а текст, подключение, параметры и результаты у неё свои.
/// </summary>
internal sealed class QueryDocument
{
    public QueryDocument(bool chain = false)
    {
        IsChain = chain;
        Results = new ResultsPanel();
    }

    /// <summary>Вкладка цепочки: её содержимое живёт в общем редакторе цепочки.</summary>
    public bool IsChain { get; }

    /// <summary>Скрипт библиотеки в том виде, в каком он сохранён. null — черновик.</summary>
    public LibraryScript? Saved { get; set; }

    /// <summary>Для черновика: текст, с которым сравниваем, и предлагаемое название.</summary>
    public string DraftText { get; set; } = string.Empty;

    public string DraftName { get; set; } = string.Empty;

    /// <summary>Свой документ — своя история Ctrl+Z.</summary>
    public TextDocument Document { get; } = new();

    public int CaretOffset { get; set; }

    /// <summary>Строки параметров переживают временное исчезновение переменной из текста.</summary>
    public Dictionary<string, ParameterRow> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ResultsPanel Results { get; }

    // Подключение вкладки — независимо от других вкладок.
    public Guid? ConnectionId { get; set; }
    public bool Regional { get; set; }
    public string RegionDatabase { get; set; } = string.Empty;
    public List<string> RegionCodes { get; set; } = new();

    /// <summary>База вкладки для подключения к кластеру. Пусто — база самого подключения.</summary>
    public string Database { get; set; } = string.Empty;

    public CancellationTokenSource? Running { get; set; }

    public bool IsRunning => Running is not null;

    // Элементы вкладки и строки в «Открытых».
    public Border? Tab { get; set; }
    public TextBlock? TabTitle { get; set; }
    public TextBlock? TabDirty { get; set; }
    public Border? ExplorerRow { get; set; }
    public TextBlock? ExplorerTitle { get; set; }
    public TextBlock? ExplorerDirty { get; set; }
    public TextBlock? ExplorerHint { get; set; }

    public Guid? ScriptId => Saved?.Id;

    public bool IsModified
    {
        get
        {
            var text = Document.Text;
            if (Saved is null)
                return text != DraftText && !string.IsNullOrWhiteSpace(text);

            return text != Saved.Sql
                   || Saved.Parameters.Any(p => Rows.TryGetValue(p.Name, out var row) && row.Kind != p.Kind);
        }
    }
}
