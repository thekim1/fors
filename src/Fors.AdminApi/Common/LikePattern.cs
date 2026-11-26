namespace Fors.AdminApi.Common;

/// <summary>Text as an ILIKE pattern that matches it literally: the escape character and the wildcards are escaped.</summary>
internal static class LikePattern
{
    /// <summary>The escape character the patterns use; pass it to <c>EF.Functions.ILike</c>.</summary>
    public const string Escape = "\\";

    /// <summary>A pattern that matches only <paramref name="text"/> itself (case-insensitively with ILIKE).</summary>
    public static string Literal(string text) =>
        text.Replace(Escape, Escape + Escape, StringComparison.Ordinal)
            .Replace("%", Escape + "%", StringComparison.Ordinal).Replace("_", Escape + "_", StringComparison.Ordinal);

    /// <summary>A pattern that matches any text containing <paramref name="text"/>.</summary>
    public static string Containing(string text) => "%" + Literal(text) + "%";
}
