using System.Text;

namespace AutoProdScripts.Services.Mapping;

public static class UseStatementParser
{
    public static string? TryExtract(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        var text = StripComments(sql);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 4 || !line.StartsWith("USE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (char.IsLetterOrDigit(line[3]) || line[3] == '_')
                continue;

            var rest = line[3..].Trim().TrimEnd(';').Trim();
            var name = ReadDatabaseName(rest);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static string? ReadDatabaseName(string rest)
    {
        if (rest.Length == 0)
            return null;

        if (rest[0] == '[')
        {
            var end = rest.IndexOf(']');
            return end > 1 ? rest[1..end].Trim() : null;
        }

        if (rest[0] is '"' or '\'')
        {
            var quote = rest[0];
            var end = rest.IndexOf(quote, 1);
            return end > 1 ? rest[1..end].Trim() : null;
        }

        var length = 0;
        while (length < rest.Length && (char.IsLetterOrDigit(rest[length]) || rest[length] == '_'))
            length++;
        return length == 0 ? null : rest[..length];
    }

    private static string StripComments(string sql)
    {
        var buffer = new StringBuilder(sql.Length);
        var inLine = false;
        var inBlock = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (inLine)
            {
                if (ch is '\n')
                {
                    inLine = false;
                    buffer.Append('\n');
                }

                continue;
            }

            if (inBlock)
            {
                if (ch == '*' && next == '/')
                {
                    inBlock = false;
                    i++;
                }
                else if (ch == '\n')
                {
                    buffer.Append('\n');
                }

                continue;
            }

            if (ch == '-' && next == '-')
            {
                inLine = true;
                i++;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                inBlock = true;
                i++;
                continue;
            }

            buffer.Append(ch);
        }

        return buffer.ToString();
    }
}
