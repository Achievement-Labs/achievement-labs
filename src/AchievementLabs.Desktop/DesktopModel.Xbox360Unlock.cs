using System.Globalization;
using AchievementLabs.Models;
using AchievementLabs.Services.Xbox360;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private string xbox360QuickUnlockMode = "Offline (no date)", xbox360UnlockStatus = "Select achievements to unlock.";
    public string[] Xbox360QuickUnlockModes { get; } = ["Offline (no date)", "Online (now)", "Custom dates", "Imported dates"];
    public string Xbox360QuickUnlockMode { get => xbox360QuickUnlockMode; set { xbox360QuickUnlockMode = value; Changed(); Changed(nameof(Xbox360CustomDatesVisible)); } }
    public bool Xbox360CustomDatesVisible => Xbox360QuickUnlockMode == "Custom dates";
    public string Xbox360UnlockStatus { get => xbox360UnlockStatus; private set { xbox360UnlockStatus = value; Changed(); } }
    public bool CanQuickUnlockXbox360 => !busy && CanModifyXbox360Profile && pendingXbox360Edits.Count == 0 && Xbox360Choices.Any(c => c.Selected && (c.Achievement.Flags & 0x20000) == 0);

    public async Task<bool> UnlockSelectedXbox360Async()
    {
        if (busy) return false;
        if (!CanModifyXbox360Profile)
        { Xbox360UnlockStatus = Xbox360NeedsSigningSetup ? "Set up signing once to continue." : "Load a valid extracted working profile first."; return false; }
        Busy(true);
        try
        {
            var fingerprint = ValidateXbox360PreviewProfile();
            var path = loadedXbox360Profile; var titleId = Xbox360EditorTitleId;
            var originals = ProfileReader.ReadAchievements(path, titleId);
            if (!originals.SequenceEqual(Xbox360Achievements)) throw new IOException("The profile changed. Reload it before unlocking.");
            var selected = Xbox360Choices.Where(c => c.Selected && (c.Achievement.Flags & 0x20000) == 0).Select(c => c.Achievement.Id).ToHashSet();
            if (selected.Count == 0) throw new InvalidOperationException("Select at least one locked achievement.");
            var edits = new List<Xbox360ProfileAchievement>();
            DateTimeOffset? start = null;
            if (Xbox360QuickUnlockMode == "Online (now)") start = DateTimeOffset.UtcNow;
            else if (Xbox360QuickUnlockMode == "Custom dates")
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Xbox360PreviewStart.Trim(), @"(?:Z|[+-]\d{2}:\d{2})$"))
                    throw new InvalidDataException("Include Z or a UTC offset in the date.");
                start = DateTimeOffset.Parse(Xbox360PreviewStart, CultureInfo.InvariantCulture);
                if (Xbox360PreviewStepMinutes < 0 || Xbox360PreviewStepMinutes > 525600) throw new InvalidDataException("Invalid date spacing.");
            }
            else if (Xbox360QuickUnlockMode is not ("Offline (no date)" or "Imported dates")) throw new InvalidDataException("Choose an unlock date option.");
            foreach (var original in originals.Where(a => selected.Contains(a.Id)).OrderBy(a => a.Id))
            {
                var time = start;
                if (Xbox360QuickUnlockMode == "Custom dates") time = start!.Value.AddMinutes((double)Xbox360PreviewStepMinutes * edits.Count);
                if (Xbox360QuickUnlockMode == "Imported dates")
                {
                    if (fingerprint != xbox360PreviewFingerprint) throw new InvalidOperationException("Import dates again for the current selection.");
                    var row = Xbox360PreviewRows.SingleOrDefault(r => r.AchievementId == original.Id.ToString(CultureInfo.InvariantCulture) && r.Result == "Ready for review");
                    if (row == null) continue;
                    time = row.ProposedState == "Unlocked Online" ? DateTimeOffset.ParseExact(row.TimeUtc, "yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;
                }
                if (time < new DateTimeOffset(2005,11,22,0,0,0,TimeSpan.Zero) || time > DateTimeOffset.UtcNow)
                    throw new InvalidDataException("Unlock dates must be between Xbox 360 launch and now.");
                edits.Add(original with { Flags = (original.Flags & ~0x30000u) | (time.HasValue ? 0x30000u : 0x20000u) | 0x100000u, UnlockedAt = time });
            }
            if (edits.Count == 0) throw new InvalidOperationException("No selected achievements have matching imported dates.");
            if (fingerprint != ValidateXbox360PreviewProfile()) throw new IOException("The profile changed before saving. Reload it.");
            // Offline entries precede dated entries; dated entries are synced oldest first.
            var ordered = edits.OrderBy(e => e.UnlockedAt.HasValue).ThenBy(e => e.UnlockedAt).ThenBy(e => e.Id).ToArray();
            Xbox360UnlockStatus = "Unlocking and resigning...";
            var backup = await Task.Run(() => ProfileEditor.Save(path, titleId, ordered), lifetime.Token);
            CaptureXbox360PreviewSource();
            Xbox360Achievements = ProfileReader.ReadAchievements(path,titleId).ToArray();
            Xbox360Games = ProfileReader.ReadGames(path).ToArray();
            var skipped = selected.Count - edits.Count;
            Xbox360UnlockStatus = $"{edits.Count} unlocked · saved and resigned" + (skipped > 0 ? $" · {skipped} unmatched skipped" : "");
            LegacyOutput = $"Working copy: {path}\nPre-save backup: {backup}";
            return true;
        }
        catch (Exception error) { Xbox360UnlockStatus = "Unlock stopped: " + error.Message; return false; }
        finally { Busy(false); }
    }
}
