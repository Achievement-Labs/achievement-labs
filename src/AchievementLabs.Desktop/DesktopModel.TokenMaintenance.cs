using AchievementLabs.Core;
using AchievementLabs.Services.Auth;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private bool eventTokenRejected, eventTokenExpiryNotified;
    private readonly SemaphoreSlim authorizationRefresh = new(1, 1), eventTokenWrites = new(1, 1);
    private DateTimeOffset lastAuthorizationRefresh = DateTimeOffset.UtcNow, nextAuthorizationRetry;
    private int sessionGeneration;
    private bool maintenanceRunning;
    private string rejectedEventsToken = "";
    private string authorizationStatus = "Connect an Xbox account to enable token maintenance.";
    private XboxOAuthClientProfile activeOAuthProfile = XboxOAuthClientProfile.XboxAppPc;
    private string? activeOAuthSessionPath;
    public string AuthorizationStatus { get => authorizationStatus; private set { authorizationStatus = value; Changed(); } }

    private void BeginTokenMaintenance()
    {
        ++sessionGeneration;
        lastAuthorizationRefresh = DateTimeOffset.UtcNow;
        nextAuthorizationRetry = default;
        activeOAuthProfile = XboxOAuthClientProfile.KnownProfiles.FirstOrDefault(p => p.Name == session?.OAuthProfileName) ?? SelectedOAuthProfile;
        activeOAuthSessionPath = connectionMethod == "Microsoft OAuth fallback" && Path.GetExtension(SessionPath).Equals(".bin", StringComparison.OrdinalIgnoreCase)
            ? SessionPath : null;
        if (session != null && !session.AuthorizationExpiresAt.HasValue)
        {
            EventTokenValidator.TryValidate(session.Authorization, out var expiry, out _);
            session = session with { AuthorizationExpiresAt = expiry };
        }
        AuthorizationStatus = connectionMethod == "manual authorization" ? "Manual XAUTH: automatic renewal is unavailable; reattach when it expires."
            : "Authorization maintenance active · next check within one minute.";
        SyncWorkflowAccount();
    }

    private void SyncWorkflowAccount()
    {
        queueAccount.XAUTH = session?.Authorization ?? "";
        queueAccount.XUIDOnly = session?.Xuid ?? "";
        queueAccount.EventsToken = session?.EventsToken ?? "";
        queueAccount.LastOAuthResponse = session?.OAuthResponse;
        queueAccount.EnsureAuthorizationAsync = token => EnsureAuthorizationFreshAsync(token);
        queueAccount.EnsureEventTokenAsync = EnsureEventTokenAsync;
        queueAccount.EventTokenRejected = MarkEventTokenRejected;
        xboxBuildQueue?.Refresh();
    }

    public async Task MaintainTokensAsync()
    {
        if (maintenanceRunning || lifetime.IsCancellationRequested) return;
        maintenanceRunning = true;
        try
        {
            CheckEventTokenFreshness();
            if (session == null) return;
            await EnsureAuthorizationFreshAsync(lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { /* Source-specific status is retained. Keep the timer alive. */ }
        finally { maintenanceRunning = false; }
    }

    private async Task EnsureAuthorizationFreshAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        await authorizationRefresh.WaitAsync(token);
        try
        {
            var original = session;
            if (original == null) throw new InvalidOperationException("Connect Xbox before continuing.");
            var now = DateTimeOffset.UtcNow;
            if (!XboxTokenMaintenancePolicy.ShouldRefresh(now, lastAuthorizationRefresh, original.AuthorizationExpiresAt)) return;
            if (now < nextAuthorizationRetry) throw new InvalidOperationException("Authorization renewal is waiting to retry.");
            if (connectionMethod == "manual authorization")
            {
                AuthorizationStatus = "Manual authorization cannot renew automatically. Disconnect and sign in again to renew it.";
                if (original.AuthorizationExpiresAt <= now) throw new InvalidOperationException(AuthorizationStatus);
                return;
            }
            var generation = sessionGeneration;
            ConnectedXboxSession fresh;
            var refreshSaveFailed = false;
            XboxApiClient? candidate = null;
            try
            {
                if (connectionMethod == "Xbox PC app")
                {
                    var value = await XboxPcAppAuthorizationReader.TryReadValidatedAsync(original.Xuid, RegionOverride, token);
                    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Open and sign in to the Xbox PC app.");
                    EventTokenValidator.TryValidate(value, out var expiry, out _);
                    fresh = original with { Authorization = value, AuthorizationExpiresAt = expiry };
                }
                else fresh = await SavedXboxSession.RefreshAuthorizationAsync(original.OAuthResponse
                    ?? throw new InvalidOperationException("This connection has no refresh credential."), lifetime.Token, activeOAuthProfile, async oauth =>
                    {
                        await requests.WaitAsync(lifetime.Token);
                        try
                        {
                            if (sessionGeneration != generation || session?.Xuid != original.Xuid) return;
                            session = session with { OAuthResponse = oauth };
                            queueAccount.LastOAuthResponse = oauth;
                            if (activeOAuthSessionPath != null)
                            {
                                try { await NativeSessionStore.SaveAsync(activeOAuthSessionPath, new(activeOAuthProfile.Name, oauth), lifetime.Token); }
                                catch (OperationCanceledException) { throw; }
                                catch { refreshSaveFailed = true; }
                            }
                        }
                        finally { requests.Release(); }
                    });
                candidate = new XboxApiClient(fresh.Authorization, RegionOverride);
                var profile = await candidate.GetBasicProfileAsync();
                if (profile?.ProfileUsers.FirstOrDefault()?.Id != original.Xuid)
                    throw new InvalidOperationException("The authorization source changed accounts. Reconnect explicitly.");
                token.ThrowIfCancellationRequested();
                await requests.WaitAsync(token);
                try
                {
                    if (sessionGeneration != generation || session?.Xuid != original.Xuid) return;
                    token.ThrowIfCancellationRequested();
                    if (sessionGeneration != generation) return;
                    var old = client;
                    session = fresh with { EventsToken = savedEventsToken };
                    client = candidate; candidate = null;
                    old?.Dispose();
                    lastAuthorizationRefresh = DateTimeOffset.UtcNow;
                    nextAuthorizationRetry = default;
                    SyncWorkflowAccount();
                    AuthorizationStatus = "XAUTH checked and renewed · " + lastAuthorizationRefresh.LocalDateTime.ToString("t");
                    if (refreshSaveFailed)
                    {
                        AuthorizationStatus += " · refreshed sign-in could not be saved for next launch.";
                        Notice = AuthorizationStatus;
                    }
                }
                finally { requests.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                nextAuthorizationRetry = DateTimeOffset.UtcNow.AddMinutes(1);
                AuthorizationStatus = "Authorization renewal failed. Check that the source is signed in; retrying within one minute.";
                Notice = AuthorizationStatus;
                throw new InvalidOperationException(AuthorizationStatus);
            }
            finally { candidate?.Dispose(); }
        }
        finally { authorizationRefresh.Release(); }
    }

    private bool IsEventTokenUsable()
        => session != null && !eventTokenRejected && (!eventTokenIssuerExpiry.HasValue || eventTokenIssuerExpiry > DateTimeOffset.UtcNow.AddMinutes(1)) && EventTokenValidator.TryValidate(savedEventsToken, out _, out _);

    private Task EnsureEventTokenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsEventTokenUsable()) throw new InvalidOperationException(OperatingSystem.IsMacOS() ? "A current event token is required. Use Get event token on this Mac in Settings." : "A current event token for this account is required. Paste and save a token in Settings.");
        return Task.CompletedTask;
    }

    // Called on the UI thread. Opaque token age is not proof of expiration.
    public void CheckEventTokenFreshness()
    {
        if (lifetime.IsCancellationRequested || eventTokenRejected) return;
        var ready = EventTokenValidator.TryValidate(savedEventsToken, out var expiry, out var message);
        if (eventTokenIssuerExpiry is { } issued)
        {
            expiry = expiry.HasValue && expiry < issued ? expiry : issued;
            if (expiry <= DateTimeOffset.UtcNow.AddMinutes(1)) { ready = false; message = "The saved event token is expired or about to expire."; }
            else message = $"Event token ready · estimated time remaining: {Math.Max(1, (int)(expiry.Value - DateTimeOffset.UtcNow).TotalMinutes)} minutes · issuer expiry {expiry.Value.LocalDateTime:g}";
        }
        EventTokenStatus = message;
        if (!ready && expiry.HasValue && !eventTokenExpiryNotified)
        {
            eventTokenExpiryNotified = true;
            Notice = "Your event token has expired or is about to expire. Save a current token before continuing event actions.";
        }
    }

    private void ResetEventTokenFreshness()
    {
        eventTokenRejected = false;
        eventTokenExpiryNotified = false;
        eventTokenIssuerExpiry = null;
    }
}
