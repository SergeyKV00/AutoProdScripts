using AutoProdScripts.Core.Execution;

namespace AutoProdScripts.Views.Queries;

internal static class ParameterKinds
{
    private static readonly (SqlParameterKind Kind, string Title)[] All =
    [
        (SqlParameterKind.NVarChar, "Строка (nvarchar)"),
        (SqlParameterKind.VarChar, "Строка (varchar)"),
        (SqlParameterKind.Int, "Целое (int)"),
        (SqlParameterKind.BigInt, "Большое целое (bigint)"),
        (SqlParameterKind.Decimal, "Дробное (decimal)"),
        (SqlParameterKind.Bit, "Да/нет (bit: 0 или 1)"),
        (SqlParameterKind.Date, "Дата"),
        (SqlParameterKind.DateTime, "Дата и время"),
        (SqlParameterKind.Guid, "GUID")
    ];

    public static IReadOnlyList<string> Titles { get; } = All.Select(k => k.Title).ToList();

    public static SqlParameterKind At(int index) => All[Math.Max(0, index)].Kind;

    public static int IndexOf(SqlParameterKind kind) => Math.Max(0, Array.FindIndex(All, k => k.Kind == kind));
}
