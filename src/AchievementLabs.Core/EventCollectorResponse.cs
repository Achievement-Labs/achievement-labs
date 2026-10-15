using System.Text.Json;

namespace AchievementLabs.Core;

/// <summary>Collector acknowledgement is separate from Xbox achievement progress.</summary>
public static class EventCollectorResponse
{
    public sealed record Details(long? Accepted, long? Rejected, bool ExplicitRejection, bool TokenFailure, string BodyState)
    {
        public string Summary => $"Collector accepted: {Accepted?.ToString() ?? "unknown"}; rejected: {Rejected?.ToString() ?? "unknown"}"
            + (TokenFailure ? "; token cracking failure" : "")
            + (ExplicitRejection ? "; explicit event rejection" : "")
            + (BodyState.Length == 0 ? "" : "; " + BodyState);
    }

    public static Details Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, null, false, false, "empty response body");
        if (text.Length > 65536) return new(null, null, false, false, "response too large to inspect");
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, null, false, false, "unrecognised response body");
            long? Count(string key) => root.TryGetProperty(key, out var value) && value.TryGetInt64(out var n) && n >= 0 ? n : null;
            // TryGetInt64 is valid only for numeric JsonElements.
            long? SafeCount(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? Count(key) : null;
            var acc = SafeCount("acc"); var rej = SafeCount("rej");
            var token = root.TryGetProperty("TokenCrackingFailure", out var tokenValue)
                && tokenValue.ValueKind != JsonValueKind.False && tokenValue.ValueKind != JsonValueKind.Null;
            bool explicitRejection = false;
            if (root.TryGetProperty("efi", out var efi) && efi.ValueKind == JsonValueKind.Object)
                foreach (var field in efi.EnumerateObject())
                    if ((field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() == "all")
                        || (field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0))
                        explicitRejection = true;
            return new(acc, rej, explicitRejection, token, "");
        }
        catch (JsonException) { return new(null, null, false, false, "unrecognised response body"); }
    }

    public static void EnsureNoRejection(int status, string? text)
    {
        // Non-2xx handling stays with the existing caller, including HTTP 401/403 token recovery.
        if (status < 200 || status >= 300) return;
        var details = Read(text);
        if (details.TokenFailure || details.ExplicitRejection || details.Rejected > 0 || details.Accepted == 0)
            throw new InvalidDataException($"HTTP {status}: telemetry collector did not accept all submitted events. {details.Summary}. Achievement unlock is not confirmed.");
    }
}
