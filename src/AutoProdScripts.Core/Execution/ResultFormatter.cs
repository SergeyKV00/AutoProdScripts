using System.Globalization;
using System.Text;

namespace AutoProdScripts.Core.Execution;

public static class ResultFormatter
{
    public static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
        bool flag => flag ? "1" : "0",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>CSV для русского Excel: разделитель «;». NULL — пустая ячейка.</summary>
    public static string ToCsv(QueryResultSet result)
    {
        var csv = new StringBuilder();
        csv.AppendJoin(';', result.Columns.Select(c => Escape(Neutralize(c)))).Append("\r\n");
        foreach (var row in result.Rows)
        {
            csv.AppendJoin(';', row.Select(v => v switch
            {
                null => string.Empty,
                string text => Escape(Neutralize(text)),
                _ => Escape(Format(v))
            })).Append("\r\n");
        }

        return csv.ToString();
    }

    // Excel исполняет ячейки, начинающиеся с = + - @, как формулы.
    private static string Neutralize(string text)
    {
        if (text.Length == 0 || text[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
            return text;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? text : "'" + text;
    }

    private static string Escape(string text) =>
        text.IndexOfAny([';', '"', '\r', '\n']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
}
