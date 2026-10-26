using Newtonsoft.Json;
using System.Globalization;
using AchievementLabs.Models;
using AchievementLabs.Services.Gfwl;
using AchievementLabs.Services.Xbox360;
using AchievementLabs.Services.LegacyXbox.Win8;
using AchievementLabs.Services.LegacyXbox.Ty;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private Workflows.Windows8ViewModel? windows8;
    public Workflows.Windows8ViewModel Windows8 => windows8 ??= new(() => (session?.Authorization ?? "", session?.Xuid ?? ""));
    private Workflows.Win8SaveToolsViewModel? win8Tools;
    public Workflows.Win8SaveToolsViewModel Win8Tools => win8Tools ??= ObserveWorkflow(new Workflows.Win8SaveToolsViewModel(saveTools, new Workflows.NativeNotices(message => Notice = message), queueAccount));
    private Workflows.HaloLegacyViewModel? legacyBridge;
    public Workflows.HaloLegacyViewModel LegacyBridge => legacyBridge ??= ObserveWorkflow(new Workflows.HaloLegacyViewModel(new AchievementLabs.Services.LegacyXbox.LegacyXboxBridge(), new Workflows.NativeNotices(message => Notice = message), queueAccount));
    private readonly IWin8SaveTool[] saveTools = [new ColdAlleySaveTool(), new CutTheRopeWin8Tool(), new Fishdom3SaveTool(), new MicrosoftBingoLocalSyncTool(), new TyWin8SaveTool(new TySaveReader())];
    private readonly GfwlAchievementManagerService gfwl = new();
    private readonly Xbox360PackageService packages = new();
    private readonly Xbox360ProfileService profiles = new();
    private Xbox360ProfileGameAddService GameAdder => new(packages, profiles);
    private Xbox360ProfileReader ProfileReader => new(packages);
    private Xbox360ProfileEditService ProfileEditor => new(packages, profiles);
    private string legacyOutput = "Select a tool to begin.", legacyXuid = "", saveAchievementId = "1", selectedSaveGame = "", packagePath = "";
    private GfwlProcessItem[] gfwlProcesses = [];
    private GfwlProcessItem? selectedGfwlProcess;
    private GfwlAchievementSession? gfwlSession;
    private GfwlAchievement[] gfwlAchievements = [];
    private GfwlAchievement? selectedGfwlAchievement;
    private string gfwlSearch = "", gfwlStatus = "Launch a GFWL game and sign in to its profile, then scan for the process.";
    private bool showAllGfwlProcesses;
    private Xbox360ProfileGame[] xbox360Games = [];
    private string[] localXbox360Profiles = [];
    private string? selectedLocalXbox360Profile;
    private string loadedXbox360Profile = "";
    private Xbox360ProfileGame? selectedXbox360Game;
    private Xbox360ProfileAchievement[] xbox360Achievements = [];
    private Xbox360ProfileAchievement? selectedXbox360Achievement;
    private string xbox360EditorTitleName = "Xbox 360 game", xbox360EditorTitleId = "";
    private string xbox360UnlockMode = "Locked", xbox360UnlockTimeText = "";
    private string gpdTitleId = "", gpdAddStatus = "Place TITLEID.gpd files in the GPD catalog folder to make them available for adding.";
    private readonly Dictionary<uint, Xbox360ProfileAchievement> pendingXbox360Edits = [];
    public bool IsLegacy => page == "Legacy";
    public bool IsGfwl => page == "Gfwl";
    public bool IsXbox360ProfileEditor => page == "Xbox360ProfileEditor";
    public bool IsXbox360Profiles => page == "Xbox360Profiles";
    public string[] LocalXbox360Profiles { get => localXbox360Profiles; private set { localXbox360Profiles = value; Changed(); } }
    public string? SelectedLocalXbox360Profile { get => selectedLocalXbox360Profile; set { selectedLocalXbox360Profile = value; Changed(); } }
    public string LoadedXbox360Profile { get => string.IsNullOrEmpty(loadedXbox360Profile) ? "No profile loaded" : loadedXbox360Profile; private set { loadedXbox360Profile = value; CaptureXbox360PreviewSource(); Changed(); Changed(nameof(CanInjectXbox360Profile)); } }
    public string GpdDirectory => GameAdder.GpdDirectory;
    public string GpdTitleId { get => gpdTitleId; set { gpdTitleId = value ?? ""; Changed(); } }
    public string GpdAddStatus { get => gpdAddStatus; private set { gpdAddStatus = value; Changed(); } }
    public string Xbox360EditorTitleName { get => xbox360EditorTitleName; private set { xbox360EditorTitleName = value; Changed(); } }
    public string Xbox360EditorTitleId { get => xbox360EditorTitleId; private set { xbox360EditorTitleId = value; Xbox360QuickUnlockMode = "Offline (no date)"; Xbox360UnlockStatus = "Select achievements to unlock."; Changed(); } }
    public string HorizonStatus => packages.IsHorizonInstalled ? "Horizon reference found: " + packages.HorizonInstallPath : "Horizon reference not installed; package inspection remains available.";
    public void RefreshHorizonStatus() { Changed(nameof(HorizonStatus)); }
    public void OpenHorizonFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(packages.HorizonInstallPath)!;
            if (!Directory.Exists(folder)) { Notice = "The Horizon reference folder was not found."; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch { Notice = "Could not open the Horizon reference folder."; }
    }
    public string[] SaveGames => saveTools.Select(t => t.DisplayName).ToArray();
    public string SelectedSaveGame { get => selectedSaveGame; set { selectedSaveGame = value; Changed(); } }
    public string LegacyXuid { get => legacyXuid; set { legacyXuid = value; Changed(); } }
    public string SaveAchievementId { get => saveAchievementId; set { saveAchievementId = value; Changed(); } }
    public string PackagePath { get => packagePath; set { packagePath = value; ResetXbox360Preview(); Changed(); Changed(nameof(CanInjectXbox360Profile)); } }
    public Xbox360ProfileGame[] Xbox360Games { get => xbox360Games; private set { xbox360Games = value; RefreshXbox360Library(); Changed(); Changed(nameof(VisibleGames)); Changed(nameof(GameCount)); } }
    public Xbox360ProfileGame? SelectedXbox360Game { get => selectedXbox360Game; set { selectedXbox360Game = value; Changed(); } }
    public Xbox360ProfileAchievement[] Xbox360Achievements { get => xbox360Achievements; private set { xbox360Achievements = value; Xbox360Choices = value.Select(a => new Xbox360AchievementChoice(a, Xbox360SelectionChanged)).ToArray(); SelectedXbox360Choice = null; ResetXbox360Preview(); Changed(); } }
    public Xbox360ProfileAchievement? SelectedXbox360Achievement { get => selectedXbox360Achievement; set { selectedXbox360Achievement = value; Changed(); Xbox360UnlockMode = value?.Status ?? "Locked"; Xbox360UnlockTimeText = (value?.UnlockedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture); } }
    public string[] Xbox360UnlockModes { get; } = ["Locked", "Unlocked Offline", "Unlocked Online"];
    public string Xbox360UnlockMode { get => xbox360UnlockMode; set { xbox360UnlockMode = value; Changed(); Changed(nameof(CanEditXbox360UnlockTime)); } }
    public bool CanEditXbox360UnlockTime => Xbox360UnlockMode == "Unlocked Online";
    public string Xbox360UnlockTimeText { get => xbox360UnlockTimeText; set { xbox360UnlockTimeText = value; Changed(); } }
    public int PendingXbox360EditCount => pendingXbox360Edits.Count;
    public bool CanModifyXbox360Profile => Xbox360ConSigner.SigningAvailable && HasValidatedXbox360WorkingCopy;
    public string Xbox360SigningStatus => !Xbox360ConSigner.SigningAvailable ? "Signing setup needed" : "Signing ready · saves resign automatically";
    public bool CanSaveXbox360Edits => !busy && CanModifyXbox360Profile && pendingXbox360Edits.Count > 0;
    public bool CanInjectXbox360Profile => !busy && HasValidatedXbox360WorkingCopy && pendingXbox360Edits.Count == 0;
    public string ProfilesDirectory => profiles.ProfilesDirectory;
    public string UsbProfileStatus => $"{profiles.FindUsbProfiles().Count} Xbox 360 profile(s) found on connected drives. Extracted profiles: {ProfilesDirectory}";
    public string LegacyOutput { get => legacyOutput; private set { legacyOutput = value; Changed(); } }
    public GfwlProcessItem[] GfwlProcesses { get => gfwlProcesses; private set { gfwlProcesses = value; Changed(); } }
    public GfwlProcessItem? SelectedGfwlProcess { get => selectedGfwlProcess; set { selectedGfwlProcess = value; Changed(); } }
    public GfwlAchievement[] GfwlAchievements { get => gfwlAchievements; private set { gfwlAchievements = value; SelectedGfwlAchievement = value.FirstOrDefault(item => !item.Unlocked); Changed(); Changed(nameof(VisibleGfwlAchievements)); } }
    public IEnumerable<GfwlAchievement> VisibleGfwlAchievements => GfwlAchievements.Where(item => item.Name.Contains(GfwlSearch, StringComparison.OrdinalIgnoreCase) || item.Id.ToString() == GfwlSearch.Trim());
    public GfwlAchievement? SelectedGfwlAchievement { get => selectedGfwlAchievement; set { selectedGfwlAchievement = value; Changed(); Changed(nameof(CanEarnGfwlAchievement)); } }
    public string GfwlSearch { get => gfwlSearch; set { gfwlSearch = value ?? ""; Changed(); Changed(nameof(VisibleGfwlAchievements)); } }
    public string GfwlStatus { get => gfwlStatus; private set { gfwlStatus = value; LegacyOutput = value; Changed(); } }
    public bool ShowAllGfwlProcesses { get => showAllGfwlProcesses; set { showAllGfwlProcesses = value; Changed(); } }
    public bool CanEarnGfwlAchievement => !busy && gfwlSession != null && SelectedGfwlAchievement is { Unlocked: false };
    public void OpenGfwlWorkspace()
    {
        Navigate("Gfwl");
        if (gfwlSession == null) GfwlStatus = gfwl.GetBinaryStatus().Summary + ". Launch the game, sign in, and scan its process.";
    }
    public void OpenLegacy() { queueAccount.XAUTH = session?.Authorization ?? ""; queueAccount.XUIDOnly = session?.Xuid ?? ""; queueAccount.LastOAuthResponse = session?.OAuthResponse; LegacyBridge.OnNavigatedTo(); Win8Tools.OnNavigatedTo(); Navigate("Legacy"); if (string.IsNullOrEmpty(SelectedSaveGame)) SelectedSaveGame = SaveGames[0]; Changed(nameof(UsbProfileStatus)); }
    public void OpenXbox360Profiles() { RefreshLocalXbox360Profiles(); Navigate("Xbox360Profiles"); Changed(nameof(UsbProfileStatus)); }
    public void OpenGpdFolder()
    {
        Directory.CreateDirectory(GpdDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GpdDirectory) { UseShellExecute = true });
    }
    public async Task AddXbox360GameAsync(string titleId, string? titleName = null)
    {
        if (!CanModifyXbox360Profile) { GpdAddStatus = LookupOutput = Xbox360SigningStatus; return; }
        if (busy) return;
        if (pendingXbox360Edits.Count > 0)
        {
            GpdAddStatus = "Save or discard the pending achievement edits before adding a game.";
            LookupOutput = GpdAddStatus;
            return;
        }
        if (string.IsNullOrEmpty(loadedXbox360Profile))
        {
            GpdAddStatus = "Load a local Xbox 360 profile before adding a game.";
            LookupOutput = GpdAddStatus;
            OpenXbox360Profiles();
            return;
        }
        var value = titleId.Trim();
        var parsed = uint.TryParse(value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var hex) && value.Length == 8
            ? hex : uint.TryParse(value, out var decimalId) ? decimalId : 0;
        if (parsed == 0) { GpdAddStatus = "Enter an eight-digit hexadecimal or decimal Xbox 360 title ID."; LookupOutput = GpdAddStatus; return; }
        var key = parsed.ToString("X8");
        GpdTitleId = key;
        if (packages.InspectPackage(loadedXbox360Profile).Entries.Any(entry =>
            entry.Path.Equals(key + ".gpd", StringComparison.OrdinalIgnoreCase)))
        {
            GpdAddStatus = $"{key}.gpd is already in the profile. If this game is not editable yet, sync the profile on Xbox 360, then extract and load it again.";
            LookupOutput = GpdAddStatus;
            return;
        }
        Busy(true);
        try
        {
            var backup = await Task.Run(() => GameAdder.Add(loadedXbox360Profile, key), lifetime.Token);
            GpdAddStatus = $"Added {titleName ?? key} ({key}) to the local profile. Backup: {backup}. Inject this working copy to USB, sync the profile on Xbox 360, then extract and load the synced profile again. Achievement editing stays unavailable until that sync.";
            LookupOutput = GpdAddStatus;
        }
        catch (Exception error)
        {
            GpdAddStatus = $"Could not add {key}.gpd: {error.Message}";
            LookupOutput = GpdAddStatus;
        }
        finally { Busy(false); }
    }
    public void RefreshLocalXbox360Profiles()
    {
        LocalXbox360Profiles = profiles.FindLocalProfiles().ToArray();
        SelectedLocalXbox360Profile = LocalXbox360Profiles.FirstOrDefault(path => path == SelectedLocalXbox360Profile)
            ?? LocalXbox360Profiles.FirstOrDefault();
        Changed(nameof(UsbProfileStatus));
    }
    public async Task LoadSelectedXbox360ProfileAsync()
    {
        if (busy) return;
        if (pendingXbox360Edits.Count > 0) { LegacyOutput = "Save or discard pending edits before switching profiles."; return; }
        if (SelectedLocalXbox360Profile is not { } path) { LegacyOutput = "Select a local Xbox 360 profile first."; return; }
        if (!path.EndsWith(".con", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                path = await profiles.ExtractProfileAsync(path, lifetime.Token);
                RefreshLocalXbox360Profiles();
                SelectedLocalXbox360Profile = path;
            }
            catch (Exception error) { LegacyOutput = $"Could not import the local profile: {error.Message}"; return; }
        }
        PackagePath = path;
        LoadedXbox360Profile = "";
        Xbox360Games = [];
        await LoadXbox360ProfileAsync();
    }
    public async Task ExtractUsbProfileAsync()
    {
      if (busy) return;
      if (pendingXbox360Edits.Count > 0) { LegacyOutput = "Save or discard pending edits before extracting another profile."; return; }
      var previousPath = PackagePath;
      await LegacyOperationAsync(async () =>
      {
        var found = await Task.Run(profiles.FindUsbProfiles, lifetime.Token);
        if (found.Count == 0) { LegacyOutput = "No Xbox 360 profile package was found on connected drives."; return; }
        if (found.Count > 1) { LegacyOutput = "Multiple profiles found. Select the desired source package with Browse before extracting: " + string.Join("; ", found); return; }
        var extracted = await profiles.ExtractProfileAsync(found[0], lifetime.Token);
        PackagePath = extracted;
        LegacyOutput = $"Profile extracted and verified.\nSource: {found[0]}\nWorking copy: {extracted}\nOriginal backup: {extracted}.bak";
        Changed(nameof(UsbProfileStatus));
      });
      if (PackagePath != previousPath && !string.IsNullOrEmpty(PackagePath))
      {
          LoadedXbox360Profile = "";
          Xbox360Games = [];
          await LoadXbox360ProfileAsync();
          RefreshLocalXbox360Profiles();
          SelectedLocalXbox360Profile = PackagePath;
          if (IsXbox360ProfileEditor && Xbox360Games.Any(game => game.TitleId.Equals(Xbox360EditorTitleId, StringComparison.OrdinalIgnoreCase)))
              await LoadXbox360EditorAchievementsAsync();
      }
    }
    public void OpenProfilesFolder()
    {
        Directory.CreateDirectory(ProfilesDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProfilesDirectory) { UseShellExecute = true });
    }
    public async Task RefreshXbox360EditorProfileAsync()
    {
        if (pendingXbox360Edits.Count > 0)
        {
            LegacyOutput = "Save the pending achievement edits before refreshing the profile.";
            return;
        }
        RefreshLocalXbox360Profiles();
        PackagePath = SelectedLocalXbox360Profile ?? "";
        LoadedXbox360Profile = "";
        Xbox360Games = [];
        if (string.IsNullOrEmpty(PackagePath))
        {
            Xbox360Achievements = [];
            SelectedXbox360Achievement = null;
            LegacyOutput = $"No valid Xbox 360 profile found in {ProfilesDirectory}. Copy the profile package there, then refresh.";
            return;
        }
        await LoadXbox360ProfileAsync();
        if (Xbox360Games.Any(game => game.TitleId.Equals(Xbox360EditorTitleId, StringComparison.OrdinalIgnoreCase)))
            await LoadXbox360EditorAchievementsAsync();
        else LegacyOutput = $"{Xbox360EditorTitleName} is not in the selected profile. Choose a game listed in the profile or extract a different profile.";
    }
    public async Task LoadXbox360ProfileAsync() => await LegacyOperationAsync(async () =>
    {
        xbox360LoadedProfileVerified = false; RefreshXbox360Signing();
        var path = PackagePath;
        var games = await Task.Run(() => ProfileReader.ReadGames(path), lifetime.Token);
        Xbox360Games = games.ToArray();
        LoadedXbox360Profile = path;
        SelectedXbox360Game = Xbox360Games.FirstOrDefault();
        Xbox360Achievements = [];
        LegacyOutput = $"Loaded {games.Count} Xbox 360 games from {path}. Select a game to read its achievements.";
    });
    public async Task LoadXbox360AchievementsAsync() => await LegacyOperationAsync(async () =>
    {
        var game = SelectedXbox360Game ?? throw new InvalidOperationException("Select a game first.");
        var achievements = await Task.Run(() => ProfileReader.ReadAchievements(PackagePath, game.TitleId), lifetime.Token);
        Xbox360Achievements = achievements.ToArray();
        LegacyOutput = $"{game.Name}: {achievements.Count} achievements loaded from the local profile.";
    });
    public async Task OpenXbox360TitleAsync(Game game)
    {
        if (busy) return;
        if (pendingXbox360Edits.Count > 0) { Notice = "Save or discard pending edits before switching games."; return; }
        if (await IsGfwlTitleAsync(game)) { OpenGfwlTitle(game); return; }
        if (!uint.TryParse(game.Id, out var numericTitleId))
        {
            Notice = "The Xbox 360 game has an invalid title ID.";
            return;
        }
        Xbox360EditorTitleName = game.Name;
        Xbox360ArtworkUrl = game.ImageUrl;
        Xbox360EditorTitleId = numericTitleId.ToString("X8");
        PackagePath = loadedXbox360Profile;
        Xbox360Achievements = [];
        SelectedXbox360Achievement = null;
        pendingXbox360Edits.Clear();
        Changed(nameof(PendingXbox360EditCount));
        Changed(nameof(CanSaveXbox360Edits));
        Changed(nameof(CanInjectXbox360Profile));
        Navigate("Xbox360ProfileEditor");
        if (string.IsNullOrEmpty(PackagePath))
        {
            OpenXbox360Profiles();
            LegacyOutput = "Load an Xbox 360 profile here before opening a 360 title from the library.";
            return;
        }
        if (!Xbox360Games.Any(item => item.TitleId.Equals(Xbox360EditorTitleId, StringComparison.OrdinalIgnoreCase)))
        {
            Navigate("Library");
            Notice = $"{game.Name} is not present in the loaded Xbox 360 profile. Open Xbox 360 profiles to inspect its games.";
            return;
        }
        if (Xbox360ProfileGameAddService.IsAwaitingConsoleSync(PackagePath, Xbox360EditorTitleId))
        {
            OpenXbox360Profiles();
            GpdAddStatus = $"{game.Name} has been added, but achievement editing is locked until the profile is injected, synced on Xbox 360, and extracted again.";
            return;
        }
        await LoadXbox360EditorAchievementsAsync();
    }
    private async Task LoadXbox360EditorAchievementsAsync() => await LegacyOperationAsync(async () =>
    {
        if (Xbox360ProfileGameAddService.IsAwaitingConsoleSync(PackagePath, Xbox360EditorTitleId))
        {
            Xbox360Achievements = [];
            SelectedXbox360Achievement = null;
            LegacyOutput = "This added title is awaiting Xbox 360 sync. Inject the profile, sync it on the console, then extract and load it again before editing achievements.";
            return;
        }
        var achievements = await Task.Run(() => ProfileReader.ReadAchievements(PackagePath, Xbox360EditorTitleId), lifetime.Token);
        Xbox360Achievements = achievements.ToArray();
        SelectedXbox360Achievement = Xbox360Achievements.FirstOrDefault();
        pendingXbox360Edits.Clear();
        Changed(nameof(PendingXbox360EditCount));
        Changed(nameof(CanSaveXbox360Edits));
        Changed(nameof(CanInjectXbox360Profile));
        LegacyOutput = $"Loaded {achievements.Count} achievements for {Xbox360EditorTitleName} from {PackagePath}.";
    });
    public void SetXbox360Achievement()
    {
        if (busy) return;
        if (!CanModifyXbox360Profile) { Notice = Xbox360SigningStatus; return; }
        var selected = SelectedXbox360Achievement;
        if (selected == null) return;
        DateTimeOffset? timestamp = null;
        if (Xbox360UnlockMode == "Unlocked Online")
        {
            if (!DateTime.TryParseExact(Xbox360UnlockTimeText, "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
            {
                Notice = "Enter the unlock time as yyyy-MM-dd HH:mm:ss.fff.";
                return;
            }
            timestamp = new DateTimeOffset(DateTime.SpecifyKind(localTime, DateTimeKind.Local));
        }
        var stateBits = Xbox360UnlockMode switch
        {
            "Locked" => 0u,
            "Unlocked Offline" => 0x20000u,
            "Unlocked Online" => 0x30000u,
            _ => throw new InvalidDataException("Choose a valid unlock state.")
        };
        if (timestamp < new DateTimeOffset(2005,11,22,0,0,0,TimeSpan.Zero) || timestamp > DateTimeOffset.UtcNow)
        { Notice = "Online unlock dates must be between Xbox 360 launch and now."; return; }
        var updated = selected with
        {
            Flags = (selected.Flags & ~0x30000u) | stateBits | 0x100000u,
            UnlockedAt = timestamp
        };
        pendingXbox360Edits[selected.Id] = updated;
        Xbox360Achievements = Xbox360Achievements.Select(item => item.Id == updated.Id ? updated : item).ToArray();
        SelectedXbox360Achievement = updated;
        Changed(nameof(PendingXbox360EditCount));
        Changed(nameof(CanSaveXbox360Edits));
        Changed(nameof(CanInjectXbox360Profile));
        Notice = $"Staged {updated.Name} as {updated.Status}.";
    }
    public async Task SaveXbox360EditsAsync()
    {
        if (!CanModifyXbox360Profile) { Notice = Xbox360SigningStatus; return; }
        if (busy || pendingXbox360Edits.Count == 0) return;
        var edits = pendingXbox360Edits.Values.ToArray();
        var path = loadedXbox360Profile;
        var titleId = Xbox360EditorTitleId;
        var selectedId = SelectedXbox360Achievement?.Id;
        Busy(true);
        try
        {
            ValidateXbox360PreviewProfile(allowPending: true);
            var backup = await Task.Run(() => ProfileEditor.Save(path, titleId, edits), lifetime.Token);
            CaptureXbox360PreviewSource();
            var achievements = await Task.Run(() => ProfileReader.ReadAchievements(path, titleId), lifetime.Token);
            Xbox360Achievements = achievements.ToArray();
            SelectedXbox360Achievement = Xbox360Achievements.FirstOrDefault(item => item.Id == selectedId)
                ?? Xbox360Achievements.FirstOrDefault();
            pendingXbox360Edits.Clear();
            Xbox360Games = ProfileReader.ReadGames(path).ToArray();
            Changed(nameof(PendingXbox360EditCount));
            Changed(nameof(CanSaveXbox360Edits));
            Changed(nameof(CanInjectXbox360Profile));
            LegacyOutput = $"Saved and verified the signed Xbox 360 working profile.\nWorking copy: {path}\nPre-save backup: {backup}";
        }
        catch (Exception error)
        {
            LegacyOutput = $"Xbox 360 profile save failed: {error.Message}";
        }
        finally { Busy(false); }
    }
    public async Task InjectXbox360ProfileAsync()
    {
        if (busy || !CanInjectXbox360Profile) return;
        var path = loadedXbox360Profile;
        var fileName = Path.GetFileName(path);
        var parentName = Path.GetFileName(Path.GetDirectoryName(path));
        var id = fileName.Length == 16 ? fileName : parentName[..Math.Min(16, parentName.Length)];
        Busy(true);
        try
        {
            using (var input = File.OpenRead(path))
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)) != xbox360LoadedFingerprint)
                    throw new IOException("The working profile changed since loading. Reload and review it before USB injection.");
            Xbox360StfsIntegrity.VerifyFile(path);
            var targets = await Task.Run(() => profiles.FindUsbProfiles().Where(candidate =>
                Path.GetFileName(candidate).Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray(), lifetime.Token);
            if (targets.Length != 1)
                throw new InvalidOperationException(targets.Length == 0
                    ? "Connect the USB drive containing this profile."
                    : "Multiple matching USB profiles were found; leave only the intended drive connected.");
            var backup = await profiles.InjectProfileAsync(path, targets[0], lifetime.Token);
            LegacyOutput = $"Injected the signed Xbox 360 profile to {targets[0]}.\nVerified USB backup: {backup}";
            Changed(nameof(UsbProfileStatus));
        }
        catch (Exception error)
        {
            LegacyOutput = $"Xbox 360 USB injection failed: {error.Message}";
        }
        finally { Busy(false); }
    }
    public async Task RunSaveToolAsync(string operation)
    {
        if (busy) return;
        if (!long.TryParse(LegacyXuid, out var xuid) || xuid <= 0) { LegacyOutput = "Enter the Xbox user ID for this save."; return; }
        var tool = saveTools.FirstOrDefault(t => t.DisplayName == SelectedSaveGame);
        if (tool == null) return;
        var idText = SaveAchievementId;
        await LegacyOperationAsync(async () =>
        {
            object result = await Task.Run<object>(() => operation switch
            {
                "Backup" => tool.Backup(xuid),
                "Prepare" when tool.CanPrepare && int.TryParse(idText, out var id) && id >= 1 && id <= tool.MaxAchievementId => tool.PrepareAchievement(xuid, id),
                "PrepareAll" when tool.CanPrepare => tool.PrepareAll(xuid),
                "Read" => tool.Read(xuid),
                _ => throw new InvalidDataException("Unsupported operation or achievement ID.")
            }, lifetime.Token);
            LegacyOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
        });
    }
    public async Task InspectPackageAsync() => await LegacyOperationAsync(async () =>
    {
        var path = PackagePath;
        var result = await Task.Run(() => packages.InspectPackage(path), lifetime.Token);
        LegacyOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
    });
    public async Task ScanGfwlAsync()
    {
        if (busy) return;
        Busy(true);
        try
        {
            var result = await Task.Run(() => (Status: gfwl.GetBinaryStatus(), Processes: gfwl.GetCandidateProcesses(ShowAllGfwlProcesses)), lifetime.Token);
            GfwlProcesses = result.Processes.ToArray();
            SelectedGfwlProcess = result.Processes.FirstOrDefault(item => item.ProcessId == gfwlSession?.ProcessId)
                ?? result.Processes.FirstOrDefault(item => item.HasXlive);
            GfwlStatus = result.Processes.Count == 0
                ? result.Status.Summary + ". No GFWL process found. Start the game, or enable compatibility mode and scan again."
                : $"{result.Status.Summary}. Found {result.Processes.Count} process(es); select the signed-in game.";
        }
        catch (Exception error) when (error is not OperationCanceledException) { GfwlStatus = $"Process scan failed: {error.Message}"; }
        finally { Busy(false); }
    }
    public async Task InjectGfwlAsync()
    {
        var process = SelectedGfwlProcess;
        if (process == null || busy) { GfwlStatus = "Select a GFWL game process first."; return; }
        Busy(true);
        GfwlAchievementSession? candidate = null;
        try
        {
            if (gfwlSession is { } previous && previous.ProcessId != process.ProcessId)
            {
                try { if (!System.Diagnostics.Process.GetProcessById(previous.ProcessId).HasExited) throw new InvalidOperationException("Close the previously connected GFWL game before switching processes. The original engine supports one active game at a time."); }
                catch (ArgumentException) { }
                previous.Dispose(); gfwlSession = null;
            }
            if (gfwlSession == null)
            {
                GfwlAchievements = [];
                candidate = new GfwlAchievementSession(process.ProcessId);
                var injected = await gfwl.InjectAdapterAsync(process, lifetime.Token);
                if (!injected.Success) throw new InvalidOperationException(injected.Message);
                gfwlSession = candidate;
                candidate = null;
            }
            GfwlAchievements = (await gfwlSession.ReadAchievementsAsync(lifetime.Token)).ToArray();
            GfwlStatus = $"Connected to {process.Name} (PID {process.ProcessId}). Loaded {GfwlAchievements.Length} achievements. Double-click a locked row to earn it.";
        }
        catch (Exception error) when (error is not OperationCanceledException) { GfwlStatus = $"GFWL connection failed: {error.Message}"; }
        finally { candidate?.Dispose(); Busy(false); Changed(nameof(CanEarnGfwlAchievement)); }
    }
    public async Task RefreshGfwlAchievementsAsync()
    {
        if (gfwlSession == null || busy) { GfwlStatus = "Connect to a signed-in GFWL game first."; return; }
        Busy(true);
        try { GfwlAchievements = (await gfwlSession.ReadAchievementsAsync(lifetime.Token)).ToArray(); GfwlStatus = $"Loaded {GfwlAchievements.Length} achievements from the running game."; }
        catch (Exception error) when (error is not OperationCanceledException) { GfwlStatus = $"Could not refresh GFWL achievements: {error.Message}"; }
        finally { Busy(false); Changed(nameof(CanEarnGfwlAchievement)); }
    }
    public async Task EarnSelectedGfwlAchievementAsync()
    {
        var selected = SelectedGfwlAchievement;
        if (selected == null || gfwlSession == null || busy) return;
        Busy(true);
        try
        {
            var verified = await gfwlSession.EarnAsync(selected.Id, lifetime.Token);
            GfwlAchievements = (await gfwlSession.ReadAchievementsAsync(lifetime.Token)).ToArray();
            SelectedGfwlAchievement = GfwlAchievements.FirstOrDefault(item => item.Id == selected.Id);
            GfwlStatus = $"Verified {verified.Name}: {verified.State}.";
        }
        catch (Exception error) when (error is not OperationCanceledException) { GfwlStatus = $"Could not earn {selected.Name}: {error.Message}"; }
        finally { Busy(false); Changed(nameof(CanEarnGfwlAchievement)); }
    }
    private async Task LegacyOperationAsync(Func<Task> action)
    {
        if (busy) return; Busy(true);
        try { await action(); }
        catch (OperationCanceledException) { LegacyOutput = "Operation cancelled."; }
        catch (Exception error) { LegacyOutput = $"Operation failed: {error.Message}"; }
        finally { Busy(false); }
    }
}
