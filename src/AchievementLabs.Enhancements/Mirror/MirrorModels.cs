using System.Text.Json.Serialization;

namespace AchievementLabs.Mirror;

/// <summary>Which platform's unlocks are copied to the other one. Each pair has exactly one direction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MirrorDirection { SteamToXbox, XboxToSteam }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MirrorPlatform { Steam, Xbox }

/// <summary>
/// Review state of one Steam ↔ Xbox row. Only <see cref="Matched"/> and <see cref="Confirmed"/> rows can
/// ever cause an unlock; <see cref="Ignored"/> rows are an explicit "no equivalent" decision by the user.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MirrorMatchState
{
    /// <summary>Identical names (after normalisation) on both sides, one candidate only.</summary>
    Matched,
    /// <summary>Picked or approved by the user.</summary>
    Confirmed,
    /// <summary>A close name was found; the user must confirm it before it counts.</summary>
    Suggested,
    /// <summary>No candidate yet; the user must choose one or mark it ignored.</summary>
    Unmatched,
    /// <summary>The user said this source achievement has no equivalent; it is never mirrored.</summary>
    Ignored
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MirrorStatus { Idle, Monitoring, Stopped }

/// <summary>One achievement as read from Steam or Xbox.</summary>
public sealed record MirrorAchievement(string Id, string Name, string Description, bool Unlocked, DateTimeOffset? UnlockedAt);

public sealed class MirrorMatch
{
    public string SteamId { get; set; } = "";
    public string SteamName { get; set; } = "";
    public string? XboxId { get; set; }
    public string XboxName { get; set; } = "";
    public MirrorMatchState State { get; set; } = MirrorMatchState.Unmatched;

    [JsonIgnore] public bool CanMirror => !string.IsNullOrEmpty(SteamId) && !string.IsNullOrEmpty(XboxId) && State is MirrorMatchState.Matched or MirrorMatchState.Confirmed;
    [JsonIgnore] public bool IsResolved => CanMirror || State == MirrorMatchState.Ignored;

    public string SourceId(MirrorDirection direction) => direction == MirrorDirection.SteamToXbox ? SteamId : XboxId ?? "";
    public string? TargetId(MirrorDirection direction) => direction == MirrorDirection.SteamToXbox ? XboxId : SteamId;
}

public sealed record MirrorNamedId(string Id, string Name, bool Unlocked = false);

public sealed record MirrorLogEntry(DateTimeOffset At, string Message, bool IsError = false)
{
    [JsonIgnore] public string Line => $"{At.LocalDateTime:HH:mm:ss}  {Message}";
}

/// <summary>An unlock detected while the pair was stopped. Never sent unless the user chooses to.</summary>
public sealed record MirrorPendingUnlock(string SourceId, string Name, DateTimeOffset DetectedAt);

/// <summary>Saved configuration for one Steam game ↔ Xbox title. Runtime state lives in <see cref="MirrorRunner"/>.</summary>
public sealed class MirrorPair
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MirrorDirection Direction { get; set; } = MirrorDirection.SteamToXbox;
    public long SteamAppId { get; set; }
    public string SteamName { get; set; } = "";
    public string XboxTitleId { get; set; } = "";
    public string XboxName { get; set; } = "";
    /// <summary>SteamID64 whose achievements are read through the Steam Web API.</summary>
    public string SteamId { get; set; } = "";
    /// <summary>
    /// "Also mirror Steam/Xbox unlocks I make in Achievement Labs". Off by default: unlocks the app itself
    /// makes on the source platform are skipped, so experimenting can't cause a permanent Xbox unlock.
    /// </summary>
    public bool MirrorAppUnlocks { get; set; }
    /// <summary>
    /// "Show me in the game on Steam and Xbox while mirroring": idle the game on Steam and show it on Xbox for the
    /// whole session. Off: presence only while you actually play (turn off when playing on a Steam Deck).
    /// </summary>
    public bool ShowInGame { get; set; } = true;
    public List<MirrorMatch> Matches { get; set; } = [];
    /// <summary>Every achievement each side returned at the last scan, so any of them can be picked by hand.</summary>
    public List<MirrorNamedId> SteamList { get; set; } = [];
    public List<MirrorNamedId> XboxList { get; set; } = [];
    public DateTimeOffset? LastScanned { get; set; }

    [JsonIgnore] public MirrorPlatform Source => Direction == MirrorDirection.SteamToXbox ? MirrorPlatform.Steam : MirrorPlatform.Xbox;
    [JsonIgnore] public MirrorPlatform Target => Direction == MirrorDirection.SteamToXbox ? MirrorPlatform.Xbox : MirrorPlatform.Steam;
    [JsonIgnore] public string DisplayName =>
        !string.IsNullOrWhiteSpace(XboxName) ? XboxName
        : !string.IsNullOrWhiteSpace(SteamName) ? SteamName
        : SteamAppId > 0 ? $"Steam app {SteamAppId}" : "New pair";

    /// <summary>One row per source achievement; the matcher builds them for the pair's direction.</summary>
    public IEnumerable<MirrorMatch> SourceRows => Matches.Where(m => m.SourceId(Direction).Length > 0);

    public int UnresolvedCount => SourceRows.Count(m => !m.IsResolved);
    public bool ReadyToMonitor => Matches.Count > 0 && UnresolvedCount == 0;

    public MirrorMatch? FindBySource(string sourceId) => SourceRows.FirstOrDefault(m => m.SourceId(Direction) == sourceId);
}
