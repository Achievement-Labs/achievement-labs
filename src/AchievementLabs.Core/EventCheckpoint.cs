using System.Security.Cryptography;
using System.Text;
using AchievementLabs.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>
/// Guards the per-request resume checkpoint of a multi-request event achievement. Catalog refreshes can now land
/// mid-queue, so a resumed achievement must still be on the same recipe it started with.
/// </summary>
public static class EventCheckpoint
{
    /// <summary>
    /// Stable fingerprint of one achievement's recipe: its mapping plus the title template. Payload bodies cannot be
    /// hashed directly because they embed timestamps, session GUIDs and sequence numbers.
    /// </summary>
    public static string? Fingerprint(JToken? mapping, string? template)
    {
        if (mapping == null || template == null) return null;
        var text = mapping.ToString(Formatting.None) + "\n" + template.Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the request index to resume from. A checkpoint above 0 whose stored fingerprint differs from the
    /// current recipe (or that no longer fits the payload count) restarts at 0; <paramref name="reset"/> explains why.
    /// Checkpoints saved before fingerprints existed (empty) keep the previous behaviour.
    /// </summary>
    public static int Resume(AutoUnlockQueueEntry entry, string? fingerprint, int requestCount, out string? reset)
    {
        reset = null;
        if (entry.EventRequestIndex <= 0) return 0;
        var stored = entry.EventPayloadFingerprint ?? "";
        if (stored.Length > 0 && stored != (fingerprint ?? ""))
            reset = $"Event recipe for achievement {entry.AchievementId} changed after request {entry.EventRequestIndex}/{requestCount} (catalog update); restarting its requests from 1.";
        else if (stored.Length > 0 && entry.EventRequestIndex > requestCount)
            reset = $"Event checkpoint for achievement {entry.AchievementId} no longer fits its {requestCount} requests; restarting from 1.";
        if (reset == null) return entry.EventRequestIndex;
        entry.EventRequestIndex = 0; entry.EventPayloadFingerprint = "";
        return 0;
    }

    /// <summary>Records progress; the fingerprint is stored when the index first goes above 0.</summary>
    public static void Advance(AutoUnlockQueueEntry entry, int nextIndex, string? fingerprint)
    {
        if (entry.EventRequestIndex == 0 && nextIndex > 0) entry.EventPayloadFingerprint = fingerprint ?? "";
        entry.EventRequestIndex = nextIndex;
    }
}
