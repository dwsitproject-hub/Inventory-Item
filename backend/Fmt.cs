using System.Globalization;

namespace BcInventory.Api;

/// <summary>
/// Indonesian number formatting for user-visible text — "." thousands, "," decimals.
///
/// Built by hand rather than from CultureInfo("id-ID") because the app runs with
/// InvariantGlobalization, so no ICU culture data exists at runtime and any named
/// culture would silently fall back to invariant (English) separators.
/// </summary>
public static class Fmt
{
    private static readonly NumberFormatInfo Id = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = new[] { 3 },
    };

    public static string N(long v) => v.ToString("N0", Id);
    public static string N(int v) => v.ToString("N0", Id);
    public static string N(double v, int decimals) => v.ToString("N" + decimals, Id);
    public static string N(decimal v, int decimals) => v.ToString("N" + decimals, Id);
}
