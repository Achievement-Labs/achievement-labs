using AchievementLabs.Services.Xbox360;

namespace AchievementLabs.Desktop;

// Open any exported profile as a private working copy, and export the signed result or the untouched original.
public sealed partial class DesktopModel
{
    public bool CanExportXbox360Profile => !busy && HasValidatedXbox360WorkingCopy && pendingXbox360Edits.Count == 0;
    public bool CanExportXbox360Original => !busy && !string.IsNullOrEmpty(loadedXbox360Profile) && File.Exists(Xbox360ProfileService.OriginalBackupPath(loadedXbox360Profile));
    public string SuggestedXbox360ExportName => string.IsNullOrEmpty(loadedXbox360Profile) ? "profile" : Path.GetFileName(Path.GetDirectoryName(loadedXbox360Profile))!.Replace("-working", "");

    public async Task OpenExportedXbox360ProfileAsync(string source)
    {
        if (busy) return;
        if (pendingXbox360Edits.Count > 0) { LegacyOutput = "Save or discard pending edits before opening another profile."; return; }
        string working;
        Busy(true);
        try { working = await profiles.ImportProfileAsync(source, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { LegacyOutput = $"Could not open the exported profile: {error.Message}"; return; }
        finally { Busy(false); }
        RefreshLocalXbox360Profiles();
        SelectedLocalXbox360Profile = working;
        PackagePath = working;
        LoadedXbox360Profile = "";
        Xbox360Games = [];
        await LoadXbox360ProfileAsync();
        if (HasValidatedXbox360WorkingCopy) LegacyOutput = $"Verified profile · {Xbox360Games.Length} games. The original export is unchanged; edits use a private copy.";
        RefreshXbox360Files();
    }

    public async Task ExportXbox360ProfileAsync(string destination, bool original)
    {
        if (busy) return;
        var source = original ? Xbox360ProfileService.OriginalBackupPath(loadedXbox360Profile) : loadedXbox360Profile;
        if (original ? !CanExportXbox360Original : !CanExportXbox360Profile) { LegacyOutput = original ? "No original backup is available for this profile." : "Load a verified profile with no pending edits first."; return; }
        if (Path.GetFullPath(destination).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) { LegacyOutput = "Choose a different export location."; return; }
        Busy(true);
        try
        {
            if (!original) ValidateXbox360PreviewProfile(allowPending: false, requireEditable: false);
            var temporary = destination + ".partial";
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, lifetime.Token);
            using (var a = File.OpenRead(source)) using (var b = File.OpenRead(temporary))
                if (!System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b))) throw new IOException("Export verification failed.");
            File.Move(temporary, destination, true);
            LegacyOutput = original ? $"Original backup exported to {destination}." : $"Signed profile exported to {destination}. Copy it back using your console/USB workflow.";
            Xbox360UnlockStatus = LegacyOutput;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { LegacyOutput = "Export stopped: " + error.Message; Xbox360UnlockStatus = LegacyOutput; }
        finally { if (File.Exists(destination + ".partial")) File.Delete(destination + ".partial"); Busy(false); }
    }

    // Titles added from a GPD stay locked until the console writes their dashboard record.
    public string[] PendingXbox360SyncTitles
    {
        get
        {
            var marker = loadedXbox360Profile + ".awaiting-console-sync.txt";
            if (string.IsNullOrEmpty(loadedXbox360Profile) || !File.Exists(marker)) return [];
            try
            {
                return File.ReadAllLines(marker).Select(l => l.Trim()).Where(Xbox360GpdCatalogClient.IsTitleId).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id => gpdCatalogTitles.FirstOrDefault(t => t.TitleId.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name is { Length: > 0 } name ? $"{name} ({id})" : id).ToArray();
            }
            catch (IOException) { return []; }
        }
    }
    public string PendingXbox360SyncText => PendingXbox360SyncTitles is { Length: > 0 } list
        ? "Awaiting console sync: " + string.Join(", ", list) + ". Export or copy the profile to the console, sign in, then open the re-exported profile to edit these games."
        : "";
    public bool HasPendingXbox360Sync => PendingXbox360SyncTitles.Length > 0;

    private void RefreshXbox360Files()
    {
        foreach (var property in new[] { nameof(CanExportXbox360Profile), nameof(CanExportXbox360Original), nameof(SuggestedXbox360ExportName), nameof(PendingXbox360SyncText), nameof(HasPendingXbox360Sync), nameof(CanContributeXbox360Gpd) }) Changed(property);
    }
}
