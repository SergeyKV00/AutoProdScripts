using System.Data;
using System.Data.Common;

namespace AutoProdScripts.Core.Execution;

/// <summary>Тип колонки результата — чтобы передать значения дальше без потери и неявных преобразований.</summary>
public sealed record SqlColumnType(SqlDbType DbType, int Size = 0, byte Precision = 0, byte Scale = 0)
{
    private bool IsCharacter => DbType is SqlDbType.Char or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.NVarChar;

    private bool IsSized => IsCharacter || DbType is SqlDbType.Binary or SqlDbType.VarBinary;

    /// <summary>Объявление колонки временной таблицы.</summary>
    public string Declaration
    {
        get
        {
            var name = DbType.ToString().ToLowerInvariant();
            if (IsSized)
            {
                var size = Size == -1 ? "max" : Size.ToString();
                // Без DATABASE_DEFAULT строки взяли бы collation tempdb и не сравнивались бы с колонками базы.
                return IsCharacter ? $"{name}({size}) COLLATE DATABASE_DEFAULT" : $"{name}({size})";
            }

            return DbType switch
            {
                SqlDbType.Decimal => $"decimal({Precision},{Scale})",
                SqlDbType.DateTime2 or SqlDbType.DateTimeOffset or SqlDbType.Time => $"{name}({Scale})",
                _ => name
            };
        }
    }

    /// <returns>null — значения этого типа передавать дальше нельзя.</returns>
    public static SqlColumnType? From(DbColumn column)
    {
        var typeName = column.DataTypeName?.ToLowerInvariant() ?? string.Empty;
        var size = column.ColumnSize ?? 0;
        var precision = (byte)Math.Clamp(column.NumericPrecision ?? 0, 0, 38);
        var scale = (byte)Math.Clamp(column.NumericScale is null or 255 ? 7 : column.NumericScale.Value, 0, 38);

        int Limit(int max) => size <= 0 || size > max ? -1 : size;

        return typeName switch
        {
            "int" => new(SqlDbType.Int),
            "bigint" => new(SqlDbType.BigInt),
            "smallint" => new(SqlDbType.SmallInt),
            "tinyint" => new(SqlDbType.TinyInt),
            "bit" => new(SqlDbType.Bit),
            "real" => new(SqlDbType.Real),
            "float" => new(SqlDbType.Float),
            "money" => new(SqlDbType.Money),
            "smallmoney" => new(SqlDbType.SmallMoney),
            "decimal" or "numeric" => new(SqlDbType.Decimal, Precision: precision, Scale: scale),
            "date" => new(SqlDbType.Date),
            "datetime" => new(SqlDbType.DateTime),
            "smalldatetime" => new(SqlDbType.SmallDateTime),
            "datetime2" => new(SqlDbType.DateTime2, Scale: Math.Min(scale, (byte)7)),
            "datetimeoffset" => new(SqlDbType.DateTimeOffset, Scale: Math.Min(scale, (byte)7)),
            "time" => new(SqlDbType.Time, Scale: Math.Min(scale, (byte)7)),
            "uniqueidentifier" => new(SqlDbType.UniqueIdentifier),
            "char" => new(SqlDbType.Char, Math.Clamp(size, 1, 8000)),
            "nchar" => new(SqlDbType.NChar, Math.Clamp(size, 1, 4000)),
            "varchar" => new(SqlDbType.VarChar, Limit(8000)),
            "nvarchar" or "sysname" => new(SqlDbType.NVarChar, Limit(4000)),
            "binary" => new(SqlDbType.Binary, Math.Clamp(size, 1, 8000)),
            "varbinary" => new(SqlDbType.VarBinary, Limit(8000)),
            "timestamp" or "rowversion" => new(SqlDbType.Binary, 8),
            "text" => new(SqlDbType.VarChar, -1),
            "ntext" or "xml" => new(SqlDbType.NVarChar, -1),
            "image" => new(SqlDbType.VarBinary, -1),
            _ => null
        };
    }
}
