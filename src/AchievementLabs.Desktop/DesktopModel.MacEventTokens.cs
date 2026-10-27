using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private async Task AcquireMacEventTokenAsync()
    {
        var original = session; var generation = sessionGeneration;
        if (original?.OAuthResponse == null) { EventTokenStatus = "Sign in with Microsoft on this Mac first. Manual/PC authorization has no Microsoft renewal credential."; return; }
        acquiringAccountEventToken = true; Busy(true);
        EventTokenStatus = "Requesting an event token using this Mac's Microsoft sign-in…";
        var locked = false;
        try
        {
            await authorizationRefresh.WaitAsync(lifetime.Token); locked = true;
            var fresh = await SavedXboxSession.RefreshOAuthAsync(original.OAuthResponse, activeOAuthProfile, lifetime.Token);
            if (session?.Xuid != original.Xuid || generation != sessionGeneration) return;
            // Save a rotated renewal credential before any Xbox exchange can fail.
            session = session with { OAuthResponse = fresh }; queueAccount.LastOAuthResponse = fresh;
            if (activeOAuthSessionPath != null) await NativeSessionStore.SaveAsync(activeOAuthSessionPath, new(activeOAuthProfile.Name, fresh), lifetime.Token);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var grant = await NativeEventTokens.ExchangeAsync(http, fresh.AccessToken ?? throw new InvalidDataException("Microsoft returned no access token; existing event token retained."), original.Xuid, activeOAuthProfile.DeviceType, lifetime.Token);
            if (session?.Xuid != original.Xuid || generation != sessionGeneration) { EventTokenStatus = "Account changed; retrieved token discarded."; return; }
            EventTokenInput = grant.Value; await SaveEventTokenAsync();
            if (savedEventsToken == grant.Value)
            {
                eventTokenIssuerExpiry = grant.ExpiresAt;
                await EventTokenIssuerExpiry.SaveAsync(grant.Value, grant.ExpiresAt, lifetime.Token);
                CheckEventTokenFreshness();
                Notice = "Matching event token retrieved and saved securely on this Mac.";
            }
        }
        catch (OperationCanceledException) { EventTokenStatus = "Event-token retrieval cancelled. Existing token retained."; }
        catch (InvalidDataException error) { EventTokenStatus = error.Message; }
        catch { EventTokenStatus = "Event-token retrieval could not complete. Existing token retained; sign in again if Microsoft renewal was refused."; }
        finally { if (locked) authorizationRefresh.Release(); acquiringAccountEventToken = false; Busy(false); }
    }
}
