using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Execution;

public enum SqlParameterKind
{
    NVarChar,
    VarChar,
    Int,
    BigInt,
    Decimal,
    Bit,
    Date,
    DateTime,
    Guid
}

/// <param name="Text">null означает NULL.</param>
public sealed record SqlParameterValue(string Name, SqlParameterKind Kind, string? Text)
{
    /// <summary>Значение, полученное из результата другого запроса: передаётся как есть, со своим типом.</summary>
    private object? Raw { get; init; }

    private SqlColumnType? RawType { get; init; }

    public static SqlParameterValue FromResult(string name, object? value, SqlColumnType type) =>
        new(name, SqlParameterKind.NVarChar, null) { Raw = value, RawType = type };

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "dd.MM.yyyy", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss"];

    public SqlParameter ToSqlParameter()
    {
        if (RawType is not null)
        {
            return new SqlParameter(Name, RawType.DbType)
            {
                Size = RawType.Size,
                Precision = RawType.Precision,
                Scale = RawType.Scale,
                Value = Raw ?? DBNull.Value
            };
        }

        var parameter = new SqlParameter(Name, DbType());
        if (Kind is SqlParameterKind.NVarChar or SqlParameterKind.VarChar)
        {
            // Фиксированный размер, чтобы сервер не плодил планы под каждую длину.
            var limit = Kind == SqlParameterKind.NVarChar ? 4000 : 8000;
            parameter.Size = Text is null || Text.Length <= limit ? limit : -1;
        }

        parameter.Value = Text is null ? DBNull.Value : Convert();
        if (Kind == SqlParameterKind.Decimal && parameter.Value is decimal number)
        {
            parameter.Precision = 38;
            parameter.Scale = (byte)((decimal.GetBits(number)[3] >> 16) & 0xFF);
        }

        return parameter;
    }

    private SqlDbType DbType() => Kind switch
    {
        SqlParameterKind.VarChar => SqlDbType.VarChar,
        SqlParameterKind.Int => SqlDbType.Int,
        SqlParameterKind.BigInt => SqlDbType.BigInt,
        SqlParameterKind.Decimal => SqlDbType.Decimal,
        SqlParameterKind.Bit => SqlDbType.Bit,
        SqlParameterKind.Date => SqlDbType.Date,
        SqlParameterKind.DateTime => SqlDbType.DateTime,
        SqlParameterKind.Guid => SqlDbType.UniqueIdentifier,
        _ => SqlDbType.NVarChar
    };

    private object Convert()
    {
        var text = Text!;
        var trimmed = text.Trim();
        var invariant = CultureInfo.InvariantCulture;

        switch (Kind)
        {
            case SqlParameterKind.Int when int.TryParse(trimmed, NumberStyles.Integer, invariant, out var i):
                return i;
            case SqlParameterKind.BigInt when long.TryParse(trimmed, NumberStyles.Integer, invariant, out var l):
                return l;
            case SqlParameterKind.Decimal when decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.Number & ~NumberStyles.AllowThousands, invariant, out var d):
                return d;
            case SqlParameterKind.Bit when trimmed is "0" or "1":
                return trimmed == "1";
            case SqlParameterKind.Date or SqlParameterKind.DateTime
                when System.DateTime.TryParseExact(trimmed, DateFormats, invariant, DateTimeStyles.None, out var date):
                return date;
            case SqlParameterKind.Guid when System.Guid.TryParse(trimmed, out var guid):
                return guid;
            case SqlParameterKind.NVarChar or SqlParameterKind.VarChar:
                return text;
            default:
                // Само значение в сообщение не включаем: там могут быть персональные данные.
                throw new FormatException($"Параметр {Name}: значение не подходит под тип {Kind}.");
        }
    }
}
