using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

/// <summary>Append-only unlock attempt trace for diagnosing silent event unlock failures. Never logs tokens or raw authorization headers.</summary>
internal static class UnlockDiagnosticsLog
{
    private static readonly object gate = new();
    private static readonly string path = AchievementLabsPaths.LocalFile("unlock-diagnostics.log");

    public static void Write(string message)
    {
        try { lock (gate) File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { }
    }

    public static void WriteResult(string stage, string titleId, string achievementId, int statusCode, string reasonPhrase, string? responseBody)
    {
        string snippet = string.Join(" ", (responseBody ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (snippet.Length > 240) snippet = snippet[..240] + "…";
        Write($"{stage} title={titleId} achievement={achievementId} status={statusCode} {reasonPhrase} body={snippet}");
    }
}
