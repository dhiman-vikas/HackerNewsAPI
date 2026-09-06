using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace RestfulAPIDemo.Api;

/// <summary>
/// Parses the <c>n</c> query parameter by hand so that every failure mode (missing, repeated, non-numeric,
/// out of range) produces the same RFC 9457 validation problem with an <c>errors.n</c> entry.
/// Minimal API integer binding would answer some of those with a bare 400 instead.
/// </summary>
internal static class NParser
{
    public static bool TryParse(StringValues raw, int max, out int n, out string error)
    {
        n = 0;

        if (raw.Count == 0 || (raw.Count == 1 && string.IsNullOrEmpty(raw[0])))
        {
            error = "The query parameter 'n' is required.";
            return false;
        }

        if (raw.Count > 1)
        {
            error = "The query parameter 'n' must be specified exactly once.";
            return false;
        }

        // NumberStyles.None: digits only - no sign, whitespace, decimal point or exponent.
        if (!int.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > max)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"The query parameter 'n' must be an integer between 1 and {max}.");
            return false;
        }

        error = string.Empty;
        return true;
    }
}
