using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using AchievementLabs.Services.Xbox360;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private string? xbox360LoadedFingerprint;
    private string? xbox360PreviewFingerprint;
    private bool xbox360LoadedProfileVerified;
    public bool HasValidatedXbox360WorkingCopy
    {
        get
        {
            if (!xbox360LoadedProfileVerified || string.IsNullOrWhiteSpace(loadedXbox360Profile) || string.IsNullOrWhiteSpace(PackagePath)) return false;
            try { return Path.GetFullPath(PackagePath).Equals(Path.GetFullPath(loadedXbox360Profile),StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
    }
    private void RefreshXbox360Signing()
    {
        Changed(nameof(CanModifyXbox360Profile)); Changed(nameof(CanSaveXbox360Edits)); Changed(nameof(CanInjectXbox360Profile)); Changed(nameof(Xbox360SigningStatus)); Changed(nameof(CanStageXbox360Preview)); Changed(nameof(Xbox360NeedsSigningSetup)); Changed(nameof(Xbox360SigningReady)); Changed(nameof(CanQuickUnlockXbox360));
    }
    public void LoadXbox360SigningData(string path)
    {
        if (busy) return;
        try
        {
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) Xbox360SigningSession.LoadHorizonFile(path);
            else Xbox360SigningSession.LoadKeyvault(path);
            try { Xbox360SigningSession.Remember(Xbox360SigningStorePath); Notice = $"Signing set up. Remembered securely for your {PlatformAccountLabel}."; }
            catch { Notice = "Signing is ready, but could not be remembered on this PC. Check folder access."; }
        }
        catch (Exception) { Notice = "Could not validate local signing data. Select a supported Horizon executable or decrypted console keyvault."; }
        RefreshXbox360Signing();
    }
    public string Xbox360SigningStorePath { get; set; } = AchievementLabs.Core.AchievementLabsPaths.LocalFile("xbox360-signing.bin");
    public bool Xbox360SigningReady => Xbox360ConSigner.SigningAvailable;
    public bool Xbox360NeedsSigningSetup => !Xbox360ConSigner.SigningAvailable;
    public void RestoreXbox360SigningData()
    {
        try
        {
            if (File.Exists(Xbox360SigningStorePath)) Xbox360SigningSession.Restore(Xbox360SigningStorePath);
        }
        catch { Notice = "Saved custom signing setup could not be opened."; }
        if (!Xbox360ConSigner.SigningAvailable)
        {
            try { Xbox360SigningSession.LoadBundledIdentity(); }
            catch { Notice = "Built-in signing could not be loaded. Set up signing manually."; }
        }
        RefreshXbox360Signing();
    }
    public void ClearXbox360SigningData()
    {
        if (busy) return;
        try { if (File.Exists(Xbox360SigningStorePath)) File.Delete(Xbox360SigningStorePath); Xbox360SigningSession.Clear(); Xbox360SigningSession.LoadBundledIdentity(); Notice = Xbox360SigningSession.HasBundledIdentity ? "Custom setup forgotten. Built-in signing is ready." : "Saved signing setup forgotten."; }
        catch { Notice = "Could not remove saved signing setup. Check folder access."; }
        RefreshXbox360Signing();
    }
    private void CaptureXbox360PreviewSource()
    {
        ResetXbox360Preview(); xbox360LoadedProfileVerified = false; xbox360LoadedFingerprint = null;
        try
        {
            if (string.IsNullOrWhiteSpace(loadedXbox360Profile)) return;
            using (var file = File.OpenRead(loadedXbox360Profile)) xbox360LoadedFingerprint = Convert.ToHexString(SHA256.HashData(file));
            var fullPath = Path.GetFullPath(loadedXbox360Profile);
            if (!fullPath.StartsWith(Path.GetFullPath(ProfilesDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !fullPath.EndsWith(".con",StringComparison.OrdinalIgnoreCase)) return;
            using (var file = File.OpenRead(fullPath))
            {
                var header = new byte[0x379]; file.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x344,4))!=0x10000 || header.AsSpan(0x371,8).ToArray().All(b=>b==0)) return;
            }
            if (!Xbox360ConSigner.VerifyFile(fullPath)) return;
            Xbox360StfsIntegrity.VerifyFile(fullPath);
            xbox360LoadedProfileVerified = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) { xbox360LoadedProfileVerified = false; }
        finally { Changed(nameof(HasValidatedXbox360WorkingCopy)); RefreshXbox360Signing(); }
    }
    private string? xbox360ArtworkUrl;
    public string? Xbox360ArtworkUrl { get => xbox360ArtworkUrl; private set { xbox360ArtworkUrl = value; Changed(); } }
    private Xbox360AchievementChoice[] xbox360Choices = [];
    private Xbox360AchievementChoice? selectedXbox360Choice;
    private string xbox360AchievementFilter = "All", xbox360AchievementSort = "Achievement ID";
    public string[] Xbox360AchievementFilters { get; } = ["All", "Locked only", "Unlocked only"];
    public string[] Xbox360AchievementSorts { get; } = ["Achievement ID", "A–Z", "Z–A", "Unlock date (newest)", "Unlock date (oldest)"];
    public string Xbox360AchievementFilter { get => xbox360AchievementFilter; set { xbox360AchievementFilter = value; Changed(); RefreshXbox360Choices(); } }
    public string Xbox360AchievementSort { get => xbox360AchievementSort; set { xbox360AchievementSort = value; Changed(); RefreshXbox360Choices(); } }
    public Xbox360AchievementChoice[] VisibleXbox360Choices
    {
        get
        {
            var items = Xbox360Choices.Where(c => Xbox360AchievementFilter == "All" ||
                Xbox360AchievementFilter == "Locked only" && (c.Achievement.Flags & 0x20000) == 0 ||
                Xbox360AchievementFilter == "Unlocked only" && (c.Achievement.Flags & 0x20000) != 0);
            return (Xbox360AchievementSort switch {
                "A–Z" => items.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Achievement.Id),
                "Z–A" => items.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Achievement.Id),
                "Unlock date (newest)" => items.OrderByDescending(c => c.Achievement.UnlockedAt).ThenBy(c => c.Achievement.Id),
                "Unlock date (oldest)" => items.OrderBy(c => c.Achievement.UnlockedAt ?? DateTimeOffset.MaxValue).ThenBy(c => c.Achievement.Id),
                _ => items.OrderBy(c => c.Achievement.Id)
            }).ToArray();
        }
    }
    public string Xbox360AchievementCount => $"{VisibleXbox360Choices.Length} shown / {Xbox360Choices.Length} total · {Xbox360SelectionCount} selected";
    private void RefreshXbox360Choices() { Changed(nameof(VisibleXbox360Choices)); Changed(nameof(Xbox360AchievementCount)); Changed(nameof(CanQuickUnlockXbox360)); }

    private Xbox360TimestampPreviewRow[] xbox360PreviewRows = [];
    private string xbox360PreviewStatus = "Local test prototype. Select achievements, then preview manual dates or import Exophase JSON / saved HTML. No profile changes are made by preview.";
    public Xbox360AchievementChoice[] Xbox360Choices { get => xbox360Choices; private set { xbox360Choices = value; Changed(); Changed(nameof(Xbox360SelectionCount)); RefreshXbox360Choices(); } }
    public Xbox360AchievementChoice? SelectedXbox360Choice { get => selectedXbox360Choice; set { selectedXbox360Choice = value; SelectedXbox360Achievement = value?.Achievement; Changed(); } }
    public int Xbox360SelectionCount => Xbox360Choices.Count(x => x.Selected);
    public Xbox360TimestampPreviewRow[] Xbox360PreviewRows { get => xbox360PreviewRows; private set { xbox360PreviewRows = value; Changed(); Changed(nameof(CanStageXbox360Preview)); } }
    public bool CanStageXbox360Preview => !busy && CanModifyXbox360Profile && Xbox360PreviewRows.Any(r => r.Result == "Ready for review");
    public string Xbox360PreviewStatus { get => xbox360PreviewStatus; private set { xbox360PreviewStatus = value; Changed(); } }
    public string Xbox360PreviewStart { get; set; } = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture);
    public decimal Xbox360PreviewStepMinutes { get; set; }
    private bool xbox360ReferenceEditionConfirmed;
    public bool Xbox360ReferenceEditionConfirmed { get => xbox360ReferenceEditionConfirmed; set { xbox360ReferenceEditionConfirmed = value; Changed(); } }

    private void ResetXbox360Preview()
    {
        Xbox360PreviewRows = [];
        xbox360PreviewFingerprint = null;
        Xbox360ReferenceEditionConfirmed = false;
        Xbox360PreviewStatus = "No imported dates loaded.";
    }
    private void Xbox360SelectionChanged() { ResetXbox360Preview(); Changed(nameof(Xbox360SelectionCount)); Changed(nameof(Xbox360AchievementCount)); Changed(nameof(CanQuickUnlockXbox360)); }
    public void SelectXbox360PreviewAchievements(bool locked)
    {
        foreach (var choice in Xbox360Choices) choice.Selected = locked && (choice.Achievement.Flags & 0x20000) == 0;
        Changed(nameof(Xbox360SelectionCount));
    }

    private string ValidateXbox360PreviewProfile(bool allowPending = false)
    {
        if (string.IsNullOrWhiteSpace(loadedXbox360Profile) || string.IsNullOrWhiteSpace(Xbox360EditorTitleId))
            throw new InvalidOperationException("Load an extracted Xbox 360 profile and open one of its games first.");
        var fullPath = Path.GetFullPath(loadedXbox360Profile);
        if (!fullPath.Equals(Path.GetFullPath(PackagePath), StringComparison.OrdinalIgnoreCase) ||
            !fullPath.StartsWith(Path.GetFullPath(ProfilesDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use the extracted working copy for the currently loaded profile.");
        using (var file = File.OpenRead(fullPath))
        {
            var header = new byte[0x379]; file.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x344, 4)) != 0x10000 ||
                header.AsSpan(0x371, 8).ToArray().All(b => b == 0))
                throw new InvalidDataException("A valid Xbox 360 profile package is required, not a save or marketplace package.");
        }
        if (!Xbox360ConSigner.VerifyFile(fullPath)) throw new InvalidDataException("The profile signature or metadata hash is invalid.");
        if (!allowPending && pendingXbox360Edits.Count != 0) throw new InvalidOperationException("Save or reload the existing pending edits before previewing reference timestamps.");
        if (!ProfileReader.ReadGames(fullPath).Any(g => g.TitleId.Equals(Xbox360EditorTitleId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This title is not in the loaded profile.");
        if (Xbox360ProfileGameAddService.IsAwaitingConsoleSync(fullPath, Xbox360EditorTitleId))
            throw new InvalidOperationException("This title requires Xbox 360 console sync and re-extraction first.");
        using var input = File.OpenRead(fullPath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(input));
        if (fingerprint != xbox360LoadedFingerprint) throw new IOException("The loaded working profile changed. Reload it before previewing dates.");
        return fingerprint;
    }

    public async Task PreviewXbox360TimestampsAsync(string? referencePath = null)
    {
        if (busy) return;
        var referenceConfirmed = Xbox360ReferenceEditionConfirmed;
        Busy(true); ResetXbox360Preview();
        try
        {
            var fingerprint = ValidateXbox360PreviewProfile();
            var selected = Xbox360Choices.Where(x => x.Selected).Select(x => x.Achievement.Id).ToHashSet();
            if (selected.Count == 0) throw new InvalidOperationException("Select at least one achievement. Empty selection never applies to all.");
            var originals = ProfileReader.ReadAchievements(loadedXbox360Profile, Xbox360EditorTitleId);
            // A changed source must be reloaded; never match a stale list against a different profile.
            if (!originals.SequenceEqual(Xbox360Achievements)) throw new InvalidOperationException("The source achievements changed. Reload this profile first.");
            IReadOnlyList<Xbox360ReferenceUnlock> reference;
            if (referencePath != null)
            {
                if (!referenceConfirmed) throw new InvalidOperationException("Confirm that the reference is for this exact game and edition before importing it. Exophase award IDs are not Xbox achievement IDs.");
                await using var input = File.OpenRead(referencePath);
                if (input.Length > 2 * 1024 * 1024) throw new InvalidDataException("Reference files must be 2 MB or smaller.");
                using var reader = new StreamReader(input);
                reference = Xbox360TimestampPreview.Parse(await reader.ReadToEndAsync(lifetime.Token));
            }
            else
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Xbox360PreviewStart.Trim(), @"(?:Z|[+-]\d{2}:\d{2})$"))
                    throw new InvalidDataException("Enter an ISO timestamp with Z or an explicit UTC offset.");
                var start = DateTimeOffset.Parse(Xbox360PreviewStart, CultureInfo.InvariantCulture);
                if (Xbox360PreviewStepMinutes < 0 || Xbox360PreviewStepMinutes > 525600) throw new InvalidDataException("Invalid timestamp spacing.");
                reference = originals.Where(a => selected.Contains(a.Id)).Select((a, index) =>
                    new Xbox360ReferenceUnlock(a.Name, Xbox360UnlockMode == "Unlocked Offline" ? null : start.AddMinutes((double)Xbox360PreviewStepMinutes * index))).ToArray();
                if (Xbox360UnlockMode == "Locked") throw new InvalidOperationException("Choose an unlocked state for this timestamp preview.");
                if (reference.Any(r => r.Time < new DateTimeOffset(2005,11,22,0,0,0,TimeSpan.Zero) || r.Time > DateTimeOffset.UtcNow))
                    throw new InvalidDataException("Proposed dates must be between Xbox 360 launch and now. Spacing must not put the last date in the future.");
            }
            if (fingerprint != ValidateXbox360PreviewProfile()) throw new IOException("The working profile changed during preview. Reload it before continuing.");
            Xbox360PreviewRows = Xbox360TimestampPreview.Match(originals, reference, selected).ToArray();
            xbox360PreviewFingerprint = fingerprint;
            var ready = Xbox360PreviewRows.Count(r => r.Result == "Ready for review");
            Xbox360PreviewStatus = $"{ready} proposed / {Xbox360PreviewRows.Length} selected; {reference.Count} reference entries. No changes saved.";
        }
        catch (Exception error) { Xbox360PreviewStatus = "Preview blocked: " + error.Message; }
        finally { Busy(false); }
    }
    public async Task DiscardXbox360EditsAsync()
    {
        if (busy || string.IsNullOrEmpty(loadedXbox360Profile)) return;
        PackagePath = loadedXbox360Profile;
        pendingXbox360Edits.Clear();
        Changed(nameof(PendingXbox360EditCount)); RefreshXbox360Signing();
        CaptureXbox360PreviewSource();
        await LoadXbox360EditorAchievementsAsync();
    }
    public void StageXbox360TimestampPreview()
    {
        if (busy || !CanStageXbox360Preview) return;
        try
        {
            if (ValidateXbox360PreviewProfile() != xbox360PreviewFingerprint) throw new IOException("Reload the profile and preview before staging.");
            var rows = Xbox360PreviewRows.Where(r => r.Result == "Ready for review").OrderBy(r => r.TimeUtc).ToArray();
            var edits = new Dictionary<uint, AchievementLabs.Models.Xbox360ProfileAchievement>();
            foreach (var row in rows)
            {
                var original = Xbox360Achievements.Single(a => a.Id.ToString(CultureInfo.InvariantCulture) == row.AchievementId);
                if ((original.Flags & 0x20000) != 0) throw new InvalidOperationException("An achievement changed after preview.");
                DateTimeOffset? time = row.ProposedState == "Unlocked Online"
                    ? DateTimeOffset.ParseExact(row.TimeUtc, "yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;
                edits.Add(original.Id, original with { Flags = (original.Flags & ~0x30000u) | (time.HasValue ? 0x30000u : 0x20000u) | 0x100000u, UnlockedAt = time });
            }
            foreach (var edit in edits) pendingXbox360Edits.Add(edit.Key, edit.Value);
            Xbox360Achievements = Xbox360Achievements.Select(a => edits.GetValueOrDefault(a.Id) ?? a).ToArray();
            Changed(nameof(PendingXbox360EditCount)); Changed(nameof(CanSaveXbox360Edits)); Changed(nameof(CanInjectXbox360Profile));
            Xbox360PreviewStatus = $"{edits.Count} reviewed edits staged in memory. Save working copy to sign and verify a backed-up local file. USB is a separate action.";
        }
        catch (Exception e) { Xbox360PreviewStatus = "Staging blocked: " + e.Message; }
    }
}
