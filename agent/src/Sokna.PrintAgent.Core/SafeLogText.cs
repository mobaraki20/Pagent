namespace Sokna.PrintAgent.Core;

public static class SafeLogText
{
    private static readonly string[] SensitiveMarkers =
    [
        "authorization",
        "bearer",
        "lease_token",
        "token",
        "secret",
        "hmac",
        "payload_json",
        "sokna-print-document"
    ];

    public static string Sanitize(string? value, int maxLength = 800)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        if (SensitiveMarkers.Any(marker => singleLine.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return "[redacted-sensitive-text]";

        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }
}
