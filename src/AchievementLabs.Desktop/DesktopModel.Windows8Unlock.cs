namespace AchievementLabs.Desktop;

// Windows 8 requests use a title/account-bound legacy token issued through the receipt helper.
public partial class DesktopModel
{
    private string win8Status = "", win8Pfn = "", win8DateInput = "", win8TimeInput = "", win8TimestampError = "";
    private DateTimeOffset? win8PreparedTimestamp;
    private bool win8Acquiring;
    private bool win8Busy, win8TokenHeld;

    public bool IsWindows8Page => OperatingSystem.IsWindows() && SelectedGame?.Platform == "Windows8";
    public string Win8Status { get => win8Status; private set { win8Status = value; Changed(); } }
    public string Win8DateInput
    {
        get => win8DateInput;
        set { win8DateInput = value ?? ""; Changed(); PrepareWin8Timestamp(); }
    }
    public string Win8TimeInput
    {
        get => win8TimeInput;
        set { win8TimeInput = value ?? ""; Changed(); PrepareWin8Timestamp(); }
    }
    public string Win8TimestampPreview
    {
        get
        {
            if (win8TimestampError.Length > 0) return win8TimestampError;
            if (win8PreparedTimestamp is not { } selected) return "Current UTC time will be used when you send.";
            return "Selected local time: " + selected.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") +
                " · Request time: " + selected.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") + ".";
        }
    }
    public bool CanRandomizeWin8Time => !string.IsNullOrWhiteSpace(Win8DateInput) && !Win8TimeInput.Contains('.');
    public void RandomizeWin8Time() => PrepareWin8Timestamp();
    private void PrepareWin8Timestamp()
    {
        win8PreparedTimestamp = null; win8TimestampError = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(Win8DateInput) || !string.IsNullOrWhiteSpace(Win8TimeInput))
                win8PreparedTimestamp = Windows8UnlockRequest.ResolveLocalTimestamp(Win8DateInput, Win8TimeInput, DateTimeOffset.UtcNow);
        }
        catch (InvalidDataException error) { win8TimestampError = error.Message; }
        Changed(nameof(Win8TimestampPreview)); Changed(nameof(CanRandomizeWin8Time)); Changed(nameof(CanSendWin8)); Changed(nameof(CanUnlockSelected));
    }
    public bool Win8TokenHeld { get => win8TokenHeld; private set { win8TokenHeld = value; Changed(); Changed(nameof(CanUnlockSelected)); } }
    public bool CanLaunchWin8 => IsWindows8Page && !win8Busy && !win8Acquiring && session != null;
    public bool CanGrabWin8Token => CanLaunchWin8 && win8Pfn.Length > 0;
    public bool CanAcquireWin8Token => IsWindows8Page && !win8Busy && !win8Acquiring && session != null;
    public bool CanSendWin8 => IsWindows8Page && !win8Busy && win8TokenHeld && session != null && win8TimestampError.Length == 0;
    private bool IsCurrentWin8Page(int version, string titleId) => version == selectionVersion && IsWindows8Page && Win8TitleId == titleId;

    private void SetWin8Busy(bool value)
    {
        win8Busy = value;
        Changed(nameof(CanAcquireWin8Token)); Changed(nameof(CanLaunchWin8)); Changed(nameof(CanGrabWin8Token)); Changed(nameof(CanUnlockSelected));
    }

    private string Win8TitleId => SelectedGame is { } game ? ResolveWindows8TitleId(game.Id) : "";

    /// <summary>Called when a game page opens: resets the Win8 panel and reflects any held token.</summary>
    private async Task RefreshWin8PageAsync()
    {
        win8Pfn = ""; Win8TokenHeld = false; Win8Status = ""; Win8DateInput = ""; Win8TimeInput = "";
        Changed(nameof(IsWindows8Page)); SetWin8Busy(false);
#if WINDOWS
        if (!IsWindows8Page) return;
        var version = selectionVersion; var titleId = Win8TitleId;
        var held = session is { } account ? await Windows8LicenseClient.ValidAsync(titleId, account.Xuid) : null;
        if (!IsCurrentWin8Page(version, titleId)) return;
        Win8TokenHeld = held != null;
        Win8Status = held != null
            ? $"Token held until {held.ExpiresAt.ToLocalTime():t}. Select an achievement and choose Unlock / retry."
            : "Launch the game and grab its token, get a license and token, or open this game's signed pop.bin.";
#else
        await Task.CompletedTask;
#endif
    }

    public async Task LaunchWin8GameAsync()
    {
#if WINDOWS
        if (!CanLaunchWin8 || session is not { } account) return;
        var version = selectionVersion; var titleId = Win8TitleId;
        SetWin8Busy(true);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var pfn = await Windows8TokenGrab.ResolvePfnAsync(http, titleId, account.Xuid, account.Authorization, lifetime.Token);
            if (!IsCurrentWin8Page(version, titleId) || session?.Xuid != account.Xuid) return;
            win8Pfn = pfn;
            if (pfn.Length == 0) { Win8Status = "Xbox did not return a Store package for this title."; return; }
            if (!Windows8TokenGrab.IsInstalled(pfn)) { Win8Status = "The game is not installed. You can use Get license & token instead."; return; }
            var launched = await Windows8TokenGrab.LaunchAsync(pfn);
            if (!IsCurrentWin8Page(version, titleId)) return;
            Win8Status = launched ? "Launched. Sign in to Xbox in the game, then grab its token." : "Windows could not launch the game.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentWin8Page(version, titleId)) Win8Status = "Launch failed: " + error.Message; }
        finally { if (IsCurrentWin8Page(version, titleId)) SetWin8Busy(false); }
#else
        await Task.CompletedTask;
#endif
    }

    public async Task GrabWin8TokenAsync()
    {
#if WINDOWS
        if (!CanGrabWin8Token || session is not { } account) return;
        var version = selectionVersion; var titleId = Win8TitleId; var pfn = win8Pfn;
        SetWin8Busy(true);
        try
        {
            Win8Status = "Looking for the game's token (up to two minutes)...";
            var token = await Task.Run(() => Windows8TokenGrab.Grab(pfn, TimeSpan.FromSeconds(120), lifetime.Token), lifetime.Token);
            if (!IsCurrentWin8Page(version, titleId) || session?.Xuid != account.Xuid) return;
            if (token == null) { Win8Status = "No token found. Finish signing in to Xbox in the game, then try again."; return; }
            var held = await Windows8LicenseClient.ImportCapturedAsync(titleId, account.Xuid, token, lifetime.Token);
            if (!IsCurrentWin8Page(version, titleId) || session?.Xuid != account.Xuid) return;
            Windows8TokenGrab.Close(pfn);
            Win8TokenHeld = true;
            Win8Status = $"Token captured and game closed. Cached until {held.ExpiresAt.ToLocalTime():t}; Xbox may expire it sooner.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentWin8Page(version, titleId)) Win8Status = "Grab failed: " + error.Message; }
        finally { if (IsCurrentWin8Page(version, titleId)) SetWin8Busy(false); }
#else
        await Task.CompletedTask;
#endif
    }

    public async Task AcquireWin8TokenAsync(string? receiptPath = null)
    {
#if WINDOWS
        if (!CanAcquireWin8Token || session is not { } account) return;
        var version = selectionVersion; var titleId = Win8TitleId;
        win8Acquiring = true; SetWin8Busy(true);
        Win8Status = receiptPath == null
            ? "Getting license and Xbox token. Complete any Windows sign-in prompt."
            : "Getting an Xbox token from the selected pop.bin.";
        try
        {
            var held = receiptPath == null
                ? await Windows8LicenseClient.AcquireAsync(titleId, account.Xuid, lifetime.Token)
                : await Windows8LicenseClient.AcquireFromReceiptAsync(titleId, account.Xuid, receiptPath, lifetime.Token);
            if (!IsCurrentWin8Page(version, titleId) || session?.Xuid != account.Xuid) return;
            Win8TokenHeld = true;
            Win8Status = $"Windows 8 token ready until {held.ExpiresAt.ToLocalTime():t}." +
                (held.SettingsMetadataChanged ? " Windows updated settings metadata; private before/after copies were retained." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentWin8Page(version, titleId)) Win8Status = error.Message; }
        finally
        {
            win8Acquiring = false;
            if (IsCurrentWin8Page(version, titleId)) SetWin8Busy(false);
            Changed(nameof(CanAcquireWin8Token)); Changed(nameof(CanLaunchWin8)); Changed(nameof(CanGrabWin8Token));
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task SendWin8RequestAsync(Achievement achievement)
    {
#if WINDOWS
        if (!CanSendWin8 || session is not { } account || !achievements.Contains(achievement)) return;
        var version = selectionVersion; var titleId = Win8TitleId;
        var id = achievement.Id; var name = achievement.Name;
        // Capture the prepared date before awaiting token validation or sending.
        var timestamp = win8PreparedTimestamp ?? DateTimeOffset.UtcNow;
        var mirrorExpected = false;
        SetWin8Busy(true);
        try
        {
            Windows8UnlockRequest.ValidateTimestamp(timestamp, DateTimeOffset.UtcNow);
            var held = await Windows8LicenseClient.ValidAsync(titleId, account.Xuid);
            if (!IsCurrentWin8Page(version, titleId) || session?.Xuid != account.Xuid) return;
            if (held == null) { Win8TokenHeld = false; Win8Status = "The token expired. Get a new license and token."; return; }
            if (!uint.TryParse(id, out _) || !uint.TryParse(titleId, out _)) { Win8Status = "This achievement or title ID isn't numeric."; return; }
            var (url, body) = Windows8UnlockRequest.Build(account.Xuid, titleId, id, timestamp);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            message.Headers.TryAddWithoutValidation("Authorization", held.Token);
            message.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
            message.Headers.TryAddWithoutValidation("x-xbl-build-version", "current");
            message.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
            MirrorExpectXbox(titleId, id, name); mirrorExpected = true;
            using var response = await http.SendAsync(message, lifetime.Token);
            var responseBody = await response.Content.ReadAsStringAsync(lifetime.Token);
            var code = Win8ErrorCode(responseBody);
            var result = (int)response.StatusCode is >= 200 and < 300 ? $"HTTP {(int)response.StatusCode} · request accepted. Verifying Xbox progress…"
                : code == 19 ? "Already unlocked (code 19)."
                : code == 21 ? "Token belongs to a different title (code 21). Get this game's license and token."
                : code == 11 ? "Invalid platform (code 11). This title's Xbox Live service is likely shut down."
                : (int)response.StatusCode is 401 or 403 ? $"HTTP {(int)response.StatusCode} · token rejected. Get a new license and token."
                : $"HTTP {(int)response.StatusCode} · {(responseBody.Length > 200 ? responseBody[..200] : responseBody)}";
            if ((int)response.StatusCode is 401 or 403 || code == 21) { await Windows8LicenseClient.ForgetAsync(titleId, account.Xuid); if (IsCurrentWin8Page(version, titleId)) Win8TokenHeld = false; }
            if (response.IsSuccessStatusCode && session?.Xuid == account.Xuid)
            {
                if (IsCurrentWin8Page(version, titleId)) Win8Status = $"{name}: {result}";
                try
                {
                    using var verifyHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    var verification = await Windows8Achievements.VerifyAsync(verifyHttp, account.Authorization, account.Xuid, titleId, id, lifetime.Token);
                    if (verification.Confirmed && session?.Xuid == account.Xuid)
                    {
                        result = $"HTTP {(int)response.StatusCode} · verified unlocked.";
                        if (!await MirrorXboxAppUnlockAsync(titleId, id, name, lifetime.Token))
                            result += " Achievement Mirror could not copy it to Steam; check the Mirror page.";
                    }
                    else
                    {
                        MirrorCancelXbox(titleId, id); mirrorExpected = false;
                        result = $"HTTP {(int)response.StatusCode} · request accepted, unlock unconfirmed. Refresh to check again.";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    MirrorCancelXbox(titleId, id); mirrorExpected = false;
                    result = $"HTTP {(int)response.StatusCode} · request accepted, verification unavailable. Refresh before retrying.";
                }
            }
            else { MirrorCancelXbox(titleId, id); mirrorExpected = false; }
            ReportWin8Result(version, titleId, name, result);
        }
        catch (OperationCanceledException) { if (mirrorExpected) MirrorCancelXbox(titleId, id); }
        catch (Exception error) { if (mirrorExpected) MirrorCancelXbox(titleId, id); ReportWin8Result(version, titleId, name, "Send failed: " + error.Message); }
        finally { if (IsCurrentWin8Page(version, titleId)) SetWin8Busy(false); }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task SendWindows8AchievementAsync(string titleId, string achievementId, DateTimeOffset? unlockTimestamp, CancellationToken cancellationToken)
    {
#if WINDOWS
        var active = session ?? throw new InvalidOperationException("Connect an Xbox account first.");
        var resolvedTitle = ResolveWindows8TitleId(titleId);
        if (!IsKnownWindows8Title(titleId) || !uint.TryParse(resolvedTitle, out _) || !uint.TryParse(achievementId, out _))
            throw new InvalidDataException("A known Windows 8 title and numeric achievement ID are required.");
        var held = await Windows8LicenseClient.ValidAsync(resolvedTitle, active.Xuid)
            ?? throw new InvalidOperationException("The Windows 8 token is missing or expired. Get this game's license and token or open its signed pop.bin, then resume.");
        if (session?.Xuid != active.Xuid) throw new InvalidOperationException("The connected Xbox account changed.");
        var timestamp = unlockTimestamp ?? DateTimeOffset.UtcNow;
        Windows8UnlockRequest.ValidateTimestamp(timestamp, DateTimeOffset.UtcNow);
        var (url, body) = Windows8UnlockRequest.Build(active.Xuid, resolvedTitle, achievementId, timestamp);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        message.Headers.TryAddWithoutValidation("Authorization", held.Token);
        message.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        message.Headers.TryAddWithoutValidation("x-xbl-build-version", "current");
        message.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        using var response = await http.SendAsync(message, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var code = Win8ErrorCode(responseBody);
        if ((int)response.StatusCode is 401 or 403 || code == 21) await Windows8LicenseClient.ForgetAsync(resolvedTitle, active.Xuid);
        if (session?.Xuid != active.Xuid) throw new InvalidOperationException("The connected Xbox account changed.");
        if (!response.IsSuccessStatusCode && code != 19)
            throw new HttpRequestException($"Windows 8 request returned HTTP {(int)response.StatusCode}" + (code is { } n ? $" (code {n})" : "") + ".", null, response.StatusCode);
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Windows 8 submission requires Windows.");
#endif
    }

    private async Task EnsureWindows8TokenAsync(string titleId, CancellationToken cancellationToken)
    {
#if WINDOWS
        cancellationToken.ThrowIfCancellationRequested();
        var active = session ?? throw new InvalidOperationException("Connect an Xbox account first.");
        if (await Windows8LicenseClient.ValidAsync(ResolveWindows8TitleId(titleId), active.Xuid) == null)
            throw new InvalidOperationException("The Windows 8 token is missing or expired. Get this game's license and token or open its signed pop.bin, then resume.");
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Windows 8 submission requires Windows.");
#endif
    }

    private void ReportWin8Result(int version, string titleId, string name, string result)
    {
        if (!IsCurrentWin8Page(version, titleId)) return;
        ActionReports.Add(new(name, result));
        Win8Status = "";
    }

    private static int? Win8ErrorCode(string body)
    {
        try { using var doc = System.Text.Json.JsonDocument.Parse(body); return doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : null; }
        catch { return null; }
    }
}
