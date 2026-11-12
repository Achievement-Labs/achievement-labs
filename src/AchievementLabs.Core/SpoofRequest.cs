namespace AchievementLabs.Core;

// Pure request-shape helpers for presence spoofing. Kept free of I/O so tests can pin the
// exact wire format the presence endpoints accept (see tests SpooferRequestTests).
public static class SpoofRequest
{
    public static string SanitizeXauth(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        const string prefix = "XBL3.0 x=";
        int start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int i = start + prefix.Length;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        if (i == start + prefix.Length || i >= text.Length || text[i] != ';') return string.Empty;
        int end = ++i;
        while (end < text.Length && text[end] > ' ' && !char.IsControl(text[end])) end++;
        return text[start..end];
    }

    // The same operation is served at four URIs; first success wins and 404 on a
    // variant that was never set is normal during stop/cleanup.
    public static IReadOnlyList<string> HeartbeatUris(string xuid) => new[]
    {
        $"https://userpresence.xboxlive.com/users/xuid({xuid})/devices/current",
        "https://userpresence.xboxlive.com/users/me/devices/current",
        $"https://presence-heartbeat.xboxlive.com/users/xuid({xuid})/devices/current/",
        "https://presence-heartbeat.xboxlive.com/users/me/devices/current/"
    };
}
