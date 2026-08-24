using System.Text.RegularExpressions;

namespace PackingProof.QqBot;

public static partial class TrackingNumberParser
{
    public static bool TryParse(string? content, out string trackingNumber)
    {
        string value = (content ?? "").Trim();
        if (value.StartsWith("查", StringComparison.Ordinal))
            value = value[1..].TrimStart(':', '：', ' ');
        if (!TrackingNumberPattern().IsMatch(value))
        {
            trackingNumber = "";
            return false;
        }
        trackingNumber = value.ToUpperInvariant();
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{5,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackingNumberPattern();
}
