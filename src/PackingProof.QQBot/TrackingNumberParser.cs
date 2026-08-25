using System.Text.RegularExpressions;

namespace PackingProof.QQBot;

public static partial class TrackingNumberParser
{
    internal static string NormalizeCommandContent(string? content)
    {
        string value = (content ?? "").Trim();
        while (LeadingMentionPattern().Match(value) is { Success: true } mention)
            value = value[mention.Length..].TrimStart();
        return value;
    }

    public static bool TryParse(string? content, out string trackingNumber)
    {
        string value = NormalizeCommandContent(content);
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

    [GeneratedRegex("^<@!?[^>]+>\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMentionPattern();
}
