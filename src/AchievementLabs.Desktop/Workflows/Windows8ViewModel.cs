using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Desktop.Workflows;

public sealed record Windows8Achievement(string Id, string Name, bool Unlocked)
{
    public string State => Unlocked ? "Unlocked" : "Locked";
}

/// <summary>Legacy progress records remain separate from modern telemetry templates.</summary>
public partial class Windows8ViewModel : ObservableObject, IDisposable
{
    // Direct requests must not pass through a proxy that synthesizes success responses.
    private readonly HttpClient http;
    private readonly Func<(string Authorization, string Xuid)> currentSession;
    private bool bridgeSession;
    public bool ForceEnglish { get; set; }
    public Windows8ViewModel() : this(() => ("", "")) { }
    public Windows8ViewModel(Func<(string Authorization, string Xuid)> currentSession, HttpMessageHandler? handler = null)
    {
        this.currentSession = currentSession;
        Achievements.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(VisibleAchievements)); OnPropertyChanged(nameof(AchievementSummary)); };
        http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
    }
    private ECDsa? signingKey;
    private string readAuth = "", writeAuth = "", xuid = "", loadedTitle = "";
    private DateTimeOffset loadedAt;
    [ObservableProperty] private string _bridgeStatePath = "";
    [ObservableProperty] private string _titleId = "1297292157";
    [ObservableProperty] private string _accountSummary = "Connect Xbox, then use the app session. No bridge or running game is required to attempt a direct request.";
    [ObservableProperty] private string _status = "Direct Xbox authentication is the default. Whether this title accepts standalone submissions must be verified by testing.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadAchievementsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SubmitSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseAppSessionCommand))]
    private bool _isBusy;
    [ObservableProperty] private Windows8Achievement? _selectedAchievement;
    public ObservableCollection<Windows8Achievement> Achievements { get; } = new();
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _stateFilter = "All";
    public IReadOnlyList<string> StateFilters { get; } = ["All", "Locked", "Unlocked"];
    public IEnumerable<Windows8Achievement> VisibleAchievements => Achievements.Where(a =>
        (StateFilter == "All" || (StateFilter == "Unlocked" ? a.Unlocked : !a.Unlocked)) &&
        (a.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)));
    public string AchievementSummary => $"{Achievements.Count} total · {Achievements.Count(a => a.Unlocked)} unlocked · {Achievements.Count(a => !a.Unlocked)} locked";
    partial void OnSearchChanged(string value) => OnPropertyChanged(nameof(VisibleAchievements));
    partial void OnStateFilterChanged(string value) => OnPropertyChanged(nameof(VisibleAchievements));
    public IReadOnlyList<string> Titles { get; } = new[] { "Halo: Spartan Assault — 1297292157", "Halo: Spartan Strike — 1297292194", "Microsoft Mahjong — 1297290225", "Microsoft Minesweeper — 1297290226", "Microsoft Solitaire Collection — 1297287741", "Microsoft Adera — 1297290206", "Hitman GO — 1397824345", "Hydro Thunder Hurricane — 1297290211", "TY the Tasmanian Tiger — 1297292136", "Fishdom 3 — 1297292148" };
    [ObservableProperty] private string? _selectedTitle;
    partial void OnSelectedTitleChanged(string? value) { if (value != null) TitleId = value.Split('—')[^1].Trim(); }
    partial void OnTitleIdChanged(string value) { Achievements.Clear(); SelectedAchievement = null; loadedTitle = ""; }
    private bool Available() => !IsBusy;

    [RelayCommand(CanExecute = nameof(Available))]
    private void UseAppSession()
    {
        ClearSession();
        try { SetAppSession(); Status = "Using the app's Xbox session directly. Load achievements, select one, then submit and verify. You can keep the game closed for this test."; }
        catch (InvalidDataException ex) { Status = ex.Message; }
    }

    private void SetAppSession()
    {
        var active = currentSession();
        if (string.IsNullOrWhiteSpace(active.Authorization) || !ulong.TryParse(active.Xuid, out _))
            throw new InvalidDataException("Connect Xbox in Achievement Labs first, then choose Use app Xbox session.");
        readAuth = writeAuth = active.Authorization; xuid = active.Xuid;
        bridgeSession = false;
        AccountSummary = $"App Xbox session · XUID {xuid} · Direct legacy request testing";
    }

    [RelayCommand(CanExecute = nameof(Available))]
    private async Task LoadSessionAsync()
    {
        IsBusy = true;
        ClearSession();
        try
        {
            var file = new FileInfo(BridgeStatePath);
            if (!file.Exists || file.Length > 1024 * 1024) throw new InvalidDataException("Select a bridge_state.json file from the running bridge.");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
            var root = doc.RootElement;
            var stamp = DateTimeOffset.FromUnixTimeSeconds((long)root.GetProperty("ts").GetDouble());
            if (DateTimeOffset.UtcNow - stamp > TimeSpan.FromHours(1) || stamp > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new InvalidDataException("Bridge session is stale. Restart or refresh the bridge, then load its new state file.");
            var user = root.GetProperty("xuid").ToString();
            if (!ulong.TryParse(user, out _)) throw new InvalidDataException("The bridge did not provide a valid Xbox user ID.");
            var auth = root.GetProperty("xbl3_auth").GetString() ?? "";
            if (!auth.StartsWith("XBL3.0 x=", StringComparison.Ordinal) || auth.Contains('\r') || auth.Contains('\n')) throw new InvalidDataException("The bridge did not provide valid read authentication.");
            var signed = root.TryGetProperty("progress_signed_auth", out var s) ? s.GetString() : null;
            var hex = root.TryGetProperty("signing_key_hex", out var k) ? k.GetString() : null;
            if (!string.IsNullOrWhiteSpace(signed) && !string.IsNullOrWhiteSpace(hex))
            {
                if (!signed.StartsWith("XBL3.0 x=", StringComparison.Ordinal) || signed.Contains('\r') || signed.Contains('\n')) throw new InvalidDataException("Invalid signed authentication.");
                var keyBytes = Convert.FromHexString(hex);
                try
                {
                    if (keyBytes.Length != 32) throw new InvalidDataException("Expected a P-256 bridge signing key.");
                    signingKey = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = keyBytes });
                }
                finally { CryptographicOperations.ZeroMemory(keyBytes); }
                writeAuth = signed;
            }
            xuid = user; readAuth = auth; loadedAt = stamp; bridgeSession = true;
            var name = root.TryGetProperty("gamertag", out var tag) ? tag.GetString() : null;
            AccountSummary = $"{name ?? "Xbox account"} · XUID {xuid} · {(signingKey == null ? "Read only — signed device session unavailable" : "Signed Windows 8 session ready")}";
            Status = "Session loaded in memory. Choose a title and load achievements. This account comes from the bridge, which may differ from the app's Xbox account.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or CryptographicException or ArgumentException or KeyNotFoundException)
        { ClearSession(); Status = ex is InvalidDataException ? ex.Message : "Could not load bridge session. Check the selected file and refresh the bridge."; }
        finally { IsBusy = false; }
    }

    private void ValidateSession()
    {
        if (string.IsNullOrEmpty(readAuth)) SetAppSession();
        if (bridgeSession && DateTimeOffset.UtcNow - loadedAt > TimeSpan.FromHours(1)) { ClearSession(); throw new InvalidDataException("Optional bridge session is stale. Use the app Xbox session, or refresh and reload the bridge state."); }
        if (!bridgeSession)
        {
            var active = currentSession();
            if (active.Xuid != xuid || active.Authorization != readAuth)
            {
                ClearSession();
                throw new InvalidDataException("The app Xbox session changed or disconnected. Use app Xbox session and reload achievements before submitting.");
            }
        }
        if (!uint.TryParse(TitleId, out var id) || id == 0) throw new InvalidDataException("Enter a numeric Xbox title ID.");
    }

    [RelayCommand(CanExecute = nameof(Available))]
    private async Task LoadAchievementsAsync()
    {
        IsBusy = true;
        try
        {
            ValidateSession();
            var title = TitleId;
            // Legacy achievement history can contain only earned records. Always load
            // the definition catalog too, even when history is nonempty.
            var catalog = await ReadAsync(title, true);
            List<Windows8Achievement> history;
            try { history = await ReadAsync(title, false); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { history = []; }
            var rows = MergeCatalog(catalog, history);
            if (TitleId != title) return;
            Achievements.Clear(); foreach (var row in rows) Achievements.Add(row);
            loadedTitle = title;
            Search = ""; StateFilter = "All"; SelectedAchievement = null;
            Status = catalog.Count == 0
                ? $"The title catalog returned no definitions. Showing {rows.Count} history records; the full list could not be confirmed."
                : $"Loaded {rows.Count} achievements from the title catalog and your unlock history.";
        }
        catch (Exception ex) { Status = FriendlyError(ex); }
        finally { IsBusy = false; }
    }

    internal static List<Windows8Achievement> MergeCatalog(IEnumerable<Windows8Achievement> catalog, IEnumerable<Windows8Achievement> history)
    {
        var merged = new Dictionary<string, Windows8Achievement>(StringComparer.Ordinal);
        foreach (var row in catalog.Concat(history))
        {
            if (merged.TryGetValue(row.Id, out var existing))
                merged[row.Id] = existing with { Unlocked = existing.Unlocked || row.Unlocked, Name = string.IsNullOrWhiteSpace(existing.Name) || existing.Name == existing.Id ? row.Name : existing.Name };
            else merged.Add(row.Id, row);
        }
        return merged.Values.OrderBy(a => uint.TryParse(a.Id, out var id) ? id : uint.MaxValue).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
    }

    [RelayCommand(CanExecute = nameof(Available))]
    private async Task SubmitSelectedAsync()
    {
        IsBusy = true;
        try
        {
            ValidateSession();
            var row = SelectedAchievement;
            if (row == null || loadedTitle != TitleId || !Achievements.Contains(row)) throw new InvalidDataException("Load this title's achievements and select one first.");
            if (row.Unlocked) throw new InvalidDataException("This achievement is already unlocked.");
            if (bridgeSession && (signingKey == null || writeAuth.Length == 0)) throw new InvalidDataException("The optional bridge session has no signed device authentication. Use the app Xbox session to test direct requests instead.");
            if (!uint.TryParse(row.Id, out var achievement)) throw new InvalidDataException("This legacy route requires a numeric achievement ID.");
            var title = TitleId;
            var url = $"https://progress.xboxlive.com/users/xuid({xuid})/progress/achievements/{achievement}?titleId={title}";
            var body = JsonSerializer.SerializeToUtf8Bytes(new { achievement = new { id = achievement, platform = 17, titleId = uint.Parse(title), unlocked = true, unlockedOnline = true, timeUnlocked = DateTime.UtcNow.ToString("O") } });
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", writeAuth);
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
            if (bridgeSession) request.Headers.TryAddWithoutValidation("Signature", Sign(signingKey!, url, writeAuth, body));
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json");
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict) throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
            Status = $"Submission returned HTTP {(int)response.StatusCode}. Checking the achievement service…";
            List<Windows8Achievement> confirmed;
            try { confirmed = await ReadAsync(title, false); }
            catch (Exception ex) { Status = $"Submission returned HTTP {(int)response.StatusCode}; verification failed. " + FriendlyError(ex); return; }
            var found = confirmed.FirstOrDefault(a => a.Id == row.Id);
            if (TitleId == title && found?.Unlocked == true)
            { var updated = row with { Unlocked = true }; var index = Achievements.IndexOf(row); if (index >= 0) Achievements[index] = updated; SelectedAchievement = updated; }
            Status = found?.Unlocked == true ? $"Verified unlocked: {row.Name}." : $"HTTP {(int)response.StatusCode}, but an unlock is NOT yet verified. Reload achievements after the service has updated; some games require in-game save synchronization.";
        }
        catch (Exception ex) { Status = FriendlyError(ex); }
        finally { IsBusy = false; }
    }

    private async Task<List<Windows8Achievement>> ReadAsync(string title, bool progress)
    {
        var hostPath = progress ? "progress.xboxlive.com/users" : "achievements.xboxlive.com/users";
        var route = progress ? "progress/titleachievements" : "achievements";
        var rows = new List<Windows8Achievement>();
        string? continuation = null;
        var seen = new HashSet<string>();
        do
        {
        var suffix = continuation == null ? "" : "&continuationToken=" + Uri.EscapeDataString(continuation);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{hostPath}/xuid({xuid})/{route}?titleId={title}&maxItems=1000{suffix}");
        request.Headers.TryAddWithoutValidation("Accept-Language", AchievementLabs.Core.XboxResponseLanguage.Resolve(ForceEnglish));
        request.Headers.TryAddWithoutValidation("Authorization", readAuth);
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("achievements", out var array)) return rows;
        foreach (var a in array.EnumerateArray())
        {
            var id = a.GetProperty("id").ToString();
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var unlocked = a.TryGetProperty("unlocked", out var u) && u.ValueKind == JsonValueKind.True;
            unlocked |= a.TryGetProperty("progressState", out var state) && state.GetString() == "Achieved";
            rows.Add(new(id, name, unlocked));
        }
        continuation = doc.RootElement.TryGetProperty("pagingInfo", out var paging) && paging.TryGetProperty("continuationToken", out var token) ? token.GetString() : null;
        if (!string.IsNullOrEmpty(continuation) && !seen.Add(continuation)) throw new InvalidDataException("The service repeated a page token. Reload the catalog before submitting.");
        } while (!string.IsNullOrEmpty(continuation));
        return rows;
    }

    internal static string Sign(ECDsa key, string url, string auth, byte[] body)
    {
        var version = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(version, 1);
        var time = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(time, DateTime.UtcNow.ToFileTimeUtc());
        using var input = new MemoryStream();
        foreach (var part in new[] { version, time, Encoding.ASCII.GetBytes("POST"), Encoding.ASCII.GetBytes(new Uri(url).PathAndQuery), Encoding.ASCII.GetBytes(auth), body.Take(8192).ToArray() }) { input.Write(part); input.WriteByte(0); }
        var signature = key.SignData(input.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(version.Concat(time).Concat(signature).ToArray());
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Xbox rejected authentication (401). Reconnect Xbox and use the app session again; if testing the optional bridge, refresh its session.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "Xbox denied this operation (403). A working sign-in does not guarantee write access for this title.",
        HttpRequestException h => $"Xbox request failed{(h.StatusCode is { } c ? $" (HTTP {(int)c})" : "")}. No unlock has been verified.",
        TaskCanceledException => "Request timed out. Reload achievements before retrying a submission.",
        _ => "Could not complete this operation. No unlock has been verified."
    };
    private void ClearSession() { signingKey?.Dispose(); signingKey = null; bridgeSession = false; readAuth = writeAuth = xuid = loadedTitle = ""; Achievements.Clear(); SelectedAchievement = null; AccountSummary = "No session selected. Use the app Xbox session."; }
    public void Dispose() { ClearSession(); http.Dispose(); }
}
