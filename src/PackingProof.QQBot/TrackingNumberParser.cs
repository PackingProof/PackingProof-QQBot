using System.Text.RegularExpressions;

namespace PackingProof.QQBot;

public static partial class TrackingNumberParser
{
    public const int MaximumTrackingNumbersPerMessage = 10;

    internal static string NormalizeCommandContent(string? content)
    {
        string value = (content ?? "").Trim();
        while (LeadingMentionPattern().Match(value) is { Success: true } mention)
            value = value[mention.Length..].TrimStart();
        return value;
    }

    public static bool TryParse(string? content, out string trackingNumber)
    {
        string value = RemoveQueryPrefix(NormalizeCommandContent(content));
        if (!TrackingNumberPattern().IsMatch(value))
        {
            trackingNumber = "";
            return false;
        }
        trackingNumber = value.ToUpperInvariant();
        return true;
    }

    internal static bool TryParseMany(string? content, out string[] trackingNumbers, out string error)
    {
        string value = RemoveQueryPrefix(NormalizeCommandContent(content));
        string[] parts = TrackingNumberSeparatorPattern().Split(value)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length == 0)
        {
            trackingNumbers = [];
            error = "没有识别到完整快递单号";
            return false;
        }
        if (parts.Length > MaximumTrackingNumbersPerMessage)
        {
            trackingNumbers = [];
            error = $"一次最多查询 {MaximumTrackingNumbersPerMessage} 个单号";
            return false;
        }

        var unique = new List<string>();
        foreach (string part in parts)
        {
            if (!TrackingNumberPattern().IsMatch(part))
            {
                trackingNumbers = [];
                error = $"无法识别单号“{part}”";
                return false;
            }
            string normalized = part.ToUpperInvariant();
            if (!unique.Contains(normalized, StringComparer.Ordinal)) unique.Add(normalized);
        }
        trackingNumbers = unique.ToArray();
        error = "";
        return true;
    }

    private static string RemoveQueryPrefix(string value)
    {
        if (value.StartsWith("查", StringComparison.Ordinal))
            value = value[1..].TrimStart(':', '：', ' ');
        return value;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{5,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackingNumberPattern();

    [GeneratedRegex("^<@!?[^>]+>\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMentionPattern();

    [GeneratedRegex("[,，、;；|｜/／\\u005C＼\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex TrackingNumberSeparatorPattern();
}
