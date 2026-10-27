using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private PcPresenceAuthorization? pairedPcPresence;
    private int pairedPcPresenceGeneration;
    private string pcPresenceComparison = "Pair PC authorization, then compare its public envelope metadata with the Mac account token. The report contains no credentials or account claims.";
    public string PcPresenceComparison { get => pcPresenceComparison; private set { pcPresenceComparison = value; Changed(); } }
    public void ComparePcPresenceMetadata()
    {
        if (session == null || pairedPcPresence == null || sessionGeneration != pairedPcPresenceGeneration || session.Xuid != pairedPcPresence.Xuid)
        { PcPresenceComparison = "Import a PC pairing for the current account before comparing."; return; }
        PcPresenceComparison = PresenceAuthorizationMetadata.Compare(session.Authorization, pairedPcPresence.Authorization);
    }
    public bool IsMacPlatform => OperatingSystem.IsMacOS();
    public bool IsPortablePlatform => AchievementLabs.Core.DesktopPlatform.IsUnixDesktop;
    public bool IsWindowsPlatform => OperatingSystem.IsWindows();
    internal string LanPairingAccount => session?.Xuid ?? throw new InvalidOperationException();
    internal int LanPairingSession => sessionGeneration;
    internal async Task<MobileTransferFile> CreateLanPresenceTransferAsync(string expectedXuid, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || session?.Xuid != expectedXuid || !CanQuery) throw new InvalidOperationException();
        var generation = sessionGeneration;
        var authorization = await XboxPcAppAuthorizationReader.TryReadValidatedAsync(expectedXuid, RegionOverride, token);
        if (string.IsNullOrWhiteSpace(authorization) || session?.Xuid != expectedXuid || generation != sessionGeneration || token.IsCancellationRequested)
            throw new InvalidOperationException();
        return MobileTransfer.Create(authorization, expectedXuid, "", null, signInOnly: true);
    }
    public PcPresenceAuthorization ReadPcPresence(byte[] bytes, string key)
        => PcPresenceTransfer.Read(bytes, key, session?.Xuid ?? "", DateTimeOffset.UtcNow);
    public string PcPresenceStatus => pairedPcPresence != null && pairedPcPresenceGeneration == sessionGeneration
        ? (pairedPcPresence.ExpiresAt is { } expires ? $"PC authorization expires at {expires.LocalDateTime:g}. " : "Xbox did not supply an authorization expiry. ") + "Ready for Game Session spoofing. Pair again if Xbox refuses or the session expires. Authorization stays in memory until removed, disconnected or the app closes; remembered devices do not renew it automatically."
        : "Pair with Windows on your local network, or import a PC sync file for the same Xbox account. Your desktop sign-in and event tokens stay in place. See Settings → Guides for the steps.";

    public async Task ImportPcPresenceAsync(PcPresenceAuthorization imported, CancellationToken token = default)
    {
        if (!AchievementLabs.Core.DesktopPlatform.IsUnixDesktop || !CanQuery || PresenceRunning || WorkflowBusy) throw new InvalidOperationException();
        var generation = sessionGeneration;
        var xuid = session!.Xuid;
        if (imported.Xuid != xuid || imported.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)) throw new InvalidDataException();
        Busy(true);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
            using var api = new XboxApiClient(imported.Authorization, RegionOverride);
            var profile = await api.GetBasicProfileAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (profile?.ProfileUsers.FirstOrDefault()?.Id != xuid || session?.Xuid != xuid || sessionGeneration != generation)
                throw new InvalidDataException();
            pairedPcPresence = imported;
            pairedPcPresenceGeneration = generation;
            PcPresenceComparison = "PC authorization paired. Choose Compare authorization metadata to generate a local report; no network request is sent.";
            Changed(nameof(PcPresenceStatus));
            Notice = "PC authorization verified for the same account. Open Game Session and start spoofing when ready.";
        }
        finally { Busy(false); }
    }

    public void ClearPcPresence()
    {
        if (PresenceRunning) { Notice = "Stop the spoofer before removing its PC pairing."; return; }
        pairedPcPresence = null;
        PcPresenceComparison = "No PC pairing is loaded.";
        Changed(nameof(PcPresenceStatus));
    }

    private string? PairedPresenceAuthorization(string xuid)
    {
        if (pairedPcPresence == null) return null;
        if (pairedPcPresenceGeneration != sessionGeneration || pairedPcPresence.Xuid != xuid)
        { ClearPcPresence(); throw new InvalidOperationException("PC pairing belongs to an earlier sign-in. Pair again."); }
        if (pairedPcPresence.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidOperationException("PC authorization has expired or is expiring. Use Settings → Sync → Pair with Windows again, or import a fresh PC sync file.");
        return pairedPcPresence.Authorization;
    }
}
