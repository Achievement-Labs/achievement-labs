using System.Diagnostics;
using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private bool xboxPcAppDetected, xboxPcAttached, oauthFallbackAvailable;
    private string xboxPcAttachmentLabel = "Checking Xbox PC app…";
    private string xboxPcAttachmentDetail = "Achievement Labs uses the account already signed in to the Xbox PC app.";
    public bool XboxPcAppDetected { get => xboxPcAppDetected; private set { xboxPcAppDetected = value; Changed(); } }
    public bool XboxPcAttached { get => xboxPcAttached; private set { xboxPcAttached = value; Changed(); } }
    public bool OAuthFallbackAvailable { get => oauthFallbackAvailable; private set { oauthFallbackAvailable = value; Changed(); } }
    public string XboxPcAttachmentLabel { get => xboxPcAttachmentLabel; private set { xboxPcAttachmentLabel = value; Changed(); } }
    public string XboxPcAttachmentDetail { get => xboxPcAttachmentDetail; private set { xboxPcAttachmentDetail = value; Changed(); } }

    public void RefreshXboxPcAppPresence()
    {
        using var process = Process.GetProcessesByName("XboxPcApp").FirstOrDefault();
        XboxPcAppDetected = process != null;
        if (XboxPcAttached && process == null)
        {
            XboxPcAttached = false;
            XboxPcAttachmentLabel = "Xbox PC app is no longer running";
            XboxPcAttachmentDetail = session == null ? "Open the Xbox app and retry attachment." : "The current Achievement Labs session remains connected, but it is no longer attached to the Xbox app process.";
        }
        else if (session == null && process != null && !busy)
        {
            XboxPcAttachmentLabel = $"Xbox PC app ready (PID {process.Id})";
            XboxPcAttachmentDetail = "Select Attach Xbox PC app to use its signed-in account.";
        }
    }

    /// <summary>Startup attach, retried once after a short wait in case the Xbox app was still loading.</summary>
    public async Task AttachXboxPcAppOnStartupAsync()
    {
        await AttachXboxPcAppAsync();
        if (session != null || lifetime.IsCancellationRequested) return;
        try { await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token); } catch (OperationCanceledException) { return; }
        if (session == null && CanConnect) await AttachXboxPcAppAsync();
    }

    public async Task AttachXboxPcAppAsync()
    {
        if (!CanConnect) return;
        Busy(true);
        XboxApiClient? candidate = null;
        try
        {
            var processes = Process.GetProcessesByName("XboxPcApp");
            XboxPcAppDetected = processes.Length > 0;
            XboxPcAttached = false;
            XboxPcAttachmentLabel = XboxPcAppDetected ? $"Xbox PC app detected (PID {processes[0].Id})" : "Xbox PC app is not running";
            XboxPcAttachmentDetail = XboxPcAppDetected ? "Reading the active Xbox session…" : "Open the Xbox app, sign in, then retry attachment.";
            if (!XboxPcAppDetected) throw new InvalidOperationException("XboxPcApp is not running.");

            Notice = "Attaching to the Xbox PC app…";
            var authorization = await XboxPcAppAuthorizationReader.TryReadValidatedAsync(null, RegionOverride, lifetime.Token);
            if (string.IsNullOrWhiteSpace(authorization)) throw new InvalidDataException("No active Xbox authorization was found in XboxPcApp.");
            candidate = new XboxApiClient(authorization, RegionOverride);
            var profile = await Task.Run(candidate.GetBasicProfileAsync, lifetime.Token);
            var user = profile?.ProfileUsers.FirstOrDefault() ?? throw new InvalidDataException("Xbox profile validation failed.");
            if (string.IsNullOrWhiteSpace(user.Id)) throw new InvalidDataException("Xbox user ID was not returned.");
            var connected = new ConnectedXboxSession(authorization, user.Id, savedEventsToken);
            await ActivateXboxSessionAsync(connected, candidate, "Xbox PC app");
            candidate = null;
            XboxPcAttached = true;
            OAuthFallbackAvailable = false;
            XboxPcAttachmentLabel = $"Attached to Xbox PC app (PID {processes[0].Id})";
            XboxPcAttachmentDetail = $"Using {ProfileName}. The Xbox app remains the source of this session.";
            Notice = $"Attached to Xbox PC app. Loaded {Games.Length} titles.";
        }
        catch (OperationCanceledException) { Notice = "Xbox PC app attachment cancelled."; }
        catch
        {
            OAuthFallbackAvailable = true;
            XboxPcAttached = false;
            XboxPcAttachmentLabel = XboxPcAppDetected ? "Xbox PC app detected, but attachment failed" : "Xbox PC app is not running";
            XboxPcAttachmentDetail = XboxPcAppDetected
                ? "Make sure the Xbox app is signed in and fully loaded, then retry. Microsoft OAuth is now available as a fallback."
                : "Open the Xbox app and sign in, or use Microsoft OAuth below as a fallback.";
            Notice = XboxPcAttachmentDetail;
        }
        finally { candidate?.Dispose(); Busy(false); }
    }

    public async Task LaunchXboxPcAppAndAttachAsync()
    {
        if (!CanConnect) return;
        try
        {
            Process.Start(new ProcessStartInfo(@"shell:appsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App") { UseShellExecute = true });
            XboxPcAttachmentLabel = "Opening Xbox PC app…";
            XboxPcAttachmentDetail = "Wait for the Xbox app to finish signing in; Achievement Labs will retry shortly.";
            await Task.Delay(3500, lifetime.Token);
            await AttachXboxPcAppAsync();
        }
        catch (OperationCanceledException) { }
        catch { OAuthFallbackAvailable = true; Notice = "The Xbox PC app could not be opened. Microsoft OAuth is available as a fallback."; }
    }

    public async Task ConnectSavedOAuthFallbackAsync()
    {
        if (!CanConnect || !OAuthFallbackAvailable) return;
        Busy(true);
        XboxApiClient? candidate = null;
        try
        {
            Notice = "Connecting the saved Microsoft OAuth fallback…";
            var connected = UseSavedEventToken(await SavedXboxSession.ConnectAsync(SessionPath, lifetime.Token, SelectedOAuthProfile));
            candidate = new XboxApiClient(connected.Authorization, RegionOverride);
            await ActivateXboxSessionAsync(connected, candidate, "Microsoft OAuth fallback");
            candidate = null;
            XboxPcAttached = false;
            OAuthFallbackAvailable = false;
            XboxPcAttachmentLabel = "Connected with Microsoft OAuth fallback";
            XboxPcAttachmentDetail = "Xbox PC app attachment was unavailable. This protected saved sign-in is being used for the current connection.";
            Notice = $"OAuth fallback connected. Loaded {Games.Length} titles.";
        }
        catch (OperationCanceledException) { Notice = "OAuth fallback connection cancelled."; }
        catch (FileNotFoundException) { Notice = "No saved OAuth fallback exists yet. Use Open Microsoft sign-in below."; }
        catch { Notice = "The saved OAuth fallback could not be refreshed. Start a new Microsoft sign-in below."; }
        finally { candidate?.Dispose(); Busy(false); }
    }

    public string[] OAuthProfiles => AchievementLabs.Services.Auth.XboxOAuthClientProfile.KnownProfiles.Select(p => p.Name).ToArray();
    private string oauthProfile = "Xbox App PC", manualAuthorization = "";
    public string OAuthProfile { get => oauthProfile; set { oauthProfile = value; Changed(); } }
    private AchievementLabs.Services.Auth.XboxOAuthClientProfile SelectedOAuthProfile => AchievementLabs.Services.Auth.XboxOAuthClientProfile.KnownProfiles.FirstOrDefault(p => p.Name == OAuthProfile) ?? AchievementLabs.Services.Auth.XboxOAuthClientProfile.XboxAppPc;
    public string ManualAuthorization { get => manualAuthorization; set { manualAuthorization = value ?? ""; Changed(); } }
    private string savedEventsToken = "", eventTokenInput = "", eventTokenStatus = "No manual event token is saved.";
    public string EventTokenInput { get => eventTokenInput; set { eventTokenInput = value ?? ""; Changed(); } }
    public string EventTokenStatus { get => eventTokenStatus; private set { eventTokenStatus = value; Changed(); } }
    public Func<Task>? EventTokenRequired { get; set; }
    // Browser and saved-session sign-in can still return an events token, but that
    // flow is not dependable in production. Event actions use only a token the
    // user explicitly pasted and saved.
    private ConnectedXboxSession UseSavedEventToken(ConnectedXboxSession connected) => connected with { EventsToken = savedEventsToken };
    private async Task LoadSavedEventTokenAsync()
    {
        try
        {
            savedEventsToken = await EventTokenStore.ReadAsync(lifetime.Token);
            ResetEventTokenFreshness();
            EventTokenInput = savedEventsToken;
            EventTokenValidator.TryValidate(savedEventsToken, out _, out var message);
            EventTokenStatus = message;
            if (session != null && !string.IsNullOrWhiteSpace(savedEventsToken)) session = session with { EventsToken = savedEventsToken };
        }
        catch { savedEventsToken = ""; EventTokenStatus = "The saved event token could not be read."; }
    }
    private bool acquiringAccountEventToken;
    public bool CanAcquireAccountEventToken => session != null && !acquiringAccountEventToken;
    public async Task AcquireAccountEventTokenAsync(Avalonia.Controls.Window owner)
    {
        if (!CanAcquireAccountEventToken || session == null) return;
        var xuid=session.Xuid;var generation=sessionGeneration;
        acquiringAccountEventToken=true;Changed(nameof(CanAcquireAccountEventToken));
        EventTokenStatus="Requesting an event token for the connected Xbox account…";
        try {
            var grant=await WamEventTokens.AcquireAsync(owner,xuid,lifetime.Token);
            if(session?.Xuid!=xuid || sessionGeneration!=generation) { EventTokenStatus="Account changed; retrieved token discarded.";return; }
            if(grant==null) { EventTokenStatus="No matching event token was retrieved. You can paste a current token manually.";return; }
            EventTokenInput=grant.Value;await SaveEventTokenAsync();
        } catch(OperationCanceledException) { }
        catch { EventTokenStatus="Could not retrieve a matching event token. Paste a current token manually."; }
        finally { acquiringAccountEventToken=false;Changed(nameof(CanAcquireAccountEventToken)); }
    }
    public async Task SaveEventTokenAsync()
    {
        if (!EventTokenValidator.TryNormalize(EventTokenInput, out var value)) { EventTokenStatus = "The event token format could not be recognized. Copy the complete x:XBL3.0 value."; return; }
        if (!EventTokenValidator.TryValidate(value, out _, out var message)) { EventTokenStatus = message; return; }
        if (value == rejectedEventsToken) { EventTokenStatus = "This token was rejected by Xbox. Save a different token."; return; }
        var hashMismatch = session != null && EventTokenValidator.IsDifferentUser(value, session.Authorization);
        await eventTokenWrites.WaitAsync(lifetime.Token);
        try
        {
            await EventTokenStore.SaveAsync(value, lifetime.Token);
            savedEventsToken = value;
            ResetEventTokenFreshness();
            queueAccount.EventsToken = value;
            if (session != null) session = session with { EventsToken = value };
            queueAccount.EventsToken = value;
            // Keep the masked value visible so Save does not look like it failed.
            EventTokenInput = value;
            EventTokenValidator.TryValidate(value, out _, out message);
            EventTokenStatus = "Saved securely · " + message
                + (hashMismatch ? " · Note: its user hash differs from the attached Xbox session; Xbox will reject it if it is for another account." : "");
            Notice = "Event token saved securely for this Windows user.";
        }
        catch { EventTokenStatus = "The event token could not be saved."; }
        finally { eventTokenWrites.Release(); }
    }
    public async Task ClearEventTokenAsync()
    {
        await eventTokenWrites.WaitAsync(lifetime.Token);
        try { EventTokenStore.Delete(); savedEventsToken = ""; ResetEventTokenFreshness(); queueAccount.EventsToken = ""; EventTokenInput = ""; if (session != null) session = session with { EventsToken = "" }; EventTokenStatus = "No manual event token is saved."; }
        catch { EventTokenStatus = "The saved event token could not be removed."; }
        finally { eventTokenWrites.Release(); }
    }
    private void MarkEventTokenRejected()
    {
        eventTokenRejected = true;
        rejectedEventsToken = savedEventsToken;
        EventTokenStatus = "The saved event token was rejected by Xbox. It may be expired; paste a current token.";
        Notice = EventTokenStatus;
    }
    public async Task AttachManualAsync()
    {
        if (!CanConnect || !OAuthFallbackAvailable) return;
        Busy(true); XboxApiClient? candidate = null;
        try
        {
            var authorization = ManualAuthorization.Trim();
            if (!authorization.StartsWith("XBL3.0 x=", StringComparison.Ordinal)) throw new InvalidDataException();
            candidate = new XboxApiClient(authorization, RegionOverride);
            var api = candidate;
            var profile = await Task.Run(api.GetBasicProfileAsync, lifetime.Token);
            var user = profile?.ProfileUsers.FirstOrDefault() ?? throw new InvalidDataException();
            if (string.IsNullOrWhiteSpace(user.Id)) throw new InvalidDataException();
            lifetime.Token.ThrowIfCancellationRequested();
            session = new(authorization, user.Id, savedEventsToken); client = candidate; candidate = null;
            connectionMethod = "manual authorization"; XboxPcAttached = false; OAuthFallbackAvailable = false;
            BeginTokenMaintenance();
            ApplyXboxProfile(user);
            ManualAuthorization = ""; Changed(nameof(ConnectionLabel));
        }
        catch (OperationCanceledException) { }
        catch { Notice = "Could not attach this authorization. Check the token and try again."; }
        finally { candidate?.Dispose(); Busy(false); }
        if (session != null) await RefreshLibraryAsync();
    }
    private string accountSummary = "Connect an account to view its profile.";
    public string AccountSummary { get => PrivacyMode ? "Profile details hidden by privacy mode." : accountSummary; private set { accountSummary = value; Changed(); } }
    public async Task RefreshAccountProfileAsync()
    {
        await WithAccountAsync(async (api, xuid) =>
        {
            var profile = await api.GetBasicProfileAsync() ?? throw new InvalidDataException();
            var user = profile.ProfileUsers.FirstOrDefault() ?? throw new InvalidDataException();
            ApplyXboxProfile(user);
            AccountSummary = string.Join(Environment.NewLine, user.Settings.Select(s => s.Id + ": " + s.Value));
        });
    }
    public void ShowSteamProfile()
    {
        try { AccountSummary = Newtonsoft.Json.JsonConvert.SerializeObject(steam.GetProfileSummary(), Newtonsoft.Json.Formatting.Indented); }
        catch { Notice = "Could not read the local Steam profile."; }
    }
    private BrowserXboxLogin? browserLogin;
    private string browserProfileName = "";
    public bool RememberBrowserSession { get; set; }
    private string loginRedirect = "";
    public string LoginRedirect { get => loginRedirect; set { loginRedirect = value ?? ""; Changed(); } }
    public void StartBrowserLogin()
    {
        if (!CanConnect || !OAuthFallbackAvailable) return;
        browserLogin?.Dispose(); browserLogin = new(SelectedOAuthProfile);
        browserProfileName = SelectedOAuthProfile.Name;
        try { Process.Start(new ProcessStartInfo(browserLogin.StartUri.AbsoluteUri) { UseShellExecute = true }); Notice = "Complete Microsoft sign-in, then paste the final login.live.com redirect URL below."; }
        catch { browserLogin.Dispose(); browserLogin = null; Notice = "Could not open the system browser."; }
    }
    public async Task CompleteBrowserLoginAsync()
    {
        if (!CanConnect || !OAuthFallbackAvailable || browserLogin == null) return;
        Busy(true); XboxApiClient? candidate = null;
        var login = browserLogin; browserLogin = null;
        var redirect = LoginRedirect; LoginRedirect = "";
        try
        {
            var connected = UseSavedEventToken(await login.CompleteAsync(redirect, lifetime.Token));
            candidate = new XboxApiClient(connected.Authorization, RegionOverride);
            var profile = await Task.Run(() => candidate.GetBasicProfileAsync(), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            client = candidate; candidate = null; session = connected;
            connectionMethod = "Microsoft OAuth"; XboxPcAttached = false; OAuthFallbackAvailable = false;
            BeginTokenMaintenance();
            ApplyXboxProfile(profile?.ProfileUsers.FirstOrDefault());
            Changed(nameof(ConnectionLabel)); Notice = "Signed in for this app session.";
            if (RememberBrowserSession && connected.OAuthResponse != null)
            {
                try
                {
                    await NativeSessionStore.SaveAsync(NativeSessionStore.DefaultPath, new(browserProfileName, connected.OAuthResponse), lifetime.Token);
                    SessionPath = NativeSessionStore.DefaultPath;
                    activeOAuthSessionPath = NativeSessionStore.DefaultPath;
                    await SavePreferencesAsync();
                    Notice = "Signed in. Session saved for this Windows user; use Connect saved session next time.";
                }
                catch (OperationCanceledException) { throw; }
                catch { Notice = "Signed in for this app session, but the saved session could not be written."; }
            }
        }
        catch (OperationCanceledException) { Notice = "Sign-in cancelled."; }
        catch { Notice = "Sign-in did not complete. Start a new browser sign-in and use its final redirect URL."; }
        finally { login.Dispose(); candidate?.Dispose(); Busy(false); }
        if (session != null) { await RefreshLibraryAsync(); Navigate("Library"); }
    }
}
