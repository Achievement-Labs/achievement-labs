using Newtonsoft.Json;
using XboxAuthNet.OAuth;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;
using XboxAuthNet.XboxLive;
using XboxAuthNet.XboxLive.Requests;
using AchievementLabs.Services.Auth;
namespace AchievementLabs.Core;

public sealed record ConnectedXboxSession(string Authorization, string Xuid, string EventsToken, MicrosoftOAuthResponse? OAuthResponse = null,
    DateTimeOffset? AuthorizationExpiresAt = null, string? OAuthProfileName = null);
public static class SavedXboxSession
{
    // Explicitly invoked by the native UI. Never logs tokens or rewrites the WPF session.
    public static async Task<ConnectedXboxSession> ConnectAsync(string sessionPath, CancellationToken cancellationToken, XboxOAuthClientProfile? selectedProfile = null)
    {
        var native = string.Equals(Path.GetExtension(sessionPath), ".bin", StringComparison.OrdinalIgnoreCase)
            ? await NativeSessionStore.ReadAsync(sessionPath, cancellationToken) : null;
        var saved = native?.OAuth ?? JsonConvert.DeserializeObject<MicrosoftOAuthResponse>(await File.ReadAllTextAsync(sessionPath, cancellationToken)) ?? throw new InvalidDataException("Invalid saved session.");
        using var http = new HttpClient();
        var profile = native == null ? selectedProfile ?? XboxOAuthClientProfile.XboxAppPc
            : XboxOAuthClientProfile.KnownProfiles.FirstOrDefault(p => p.Name == native.ProfileName) ?? throw new InvalidDataException("Unknown saved OAuth profile.");
        var oauth = new CodeFlowLiveApiClient(profile.ClientId, XboxAuthConstants.XboxScope, http);
        var fresh = await oauth.RefreshToken(new CodeFlowRefreshTokenParameter { RefreshToken = saved.RefreshToken }, cancellationToken);
        // Preserve refresh rotation even when the following Xbox exchange fails.
        if (native != null) await NativeSessionStore.SaveAsync(sessionPath, new(profile.Name, fresh), cancellationToken);
        return await GenerateAsync(fresh, cancellationToken, profile, requestEvents: !OperatingSystem.IsMacOS());
    }
    public static async Task<MicrosoftOAuthResponse> RefreshOAuthAsync(MicrosoftOAuthResponse saved, XboxOAuthClientProfile profile, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var oauth = new CodeFlowLiveApiClient(profile.ClientId, XboxAuthConstants.XboxScope, http);
        return await oauth.RefreshToken(new CodeFlowRefreshTokenParameter { RefreshToken = saved.RefreshToken }, ct);
    }
    public static async Task<ConnectedXboxSession> RefreshAuthorizationAsync(MicrosoftOAuthResponse saved, CancellationToken cancellationToken, XboxOAuthClientProfile profile,
        Func<MicrosoftOAuthResponse, Task>? onOAuthRefreshed = null)
    {
        using var http = new HttpClient();
        var oauth = new CodeFlowLiveApiClient(profile.ClientId, XboxAuthConstants.XboxScope, http);
        var fresh = await oauth.RefreshToken(new CodeFlowRefreshTokenParameter { RefreshToken = saved.RefreshToken }, cancellationToken);
        // Rotation must survive a later Xbox device/XSTS failure.
        if (onOAuthRefreshed != null) await onOAuthRefreshed(fresh);
        return await GenerateAsync(fresh, cancellationToken, profile, requestEvents: false);
    }

    public static async Task<ConnectedXboxSession> GenerateAsync(MicrosoftOAuthResponse fresh, CancellationToken cancellationToken, XboxOAuthClientProfile? selectedProfile = null, bool requestEvents = true)
    {
        using var http = new HttpClient();
        var profile = selectedProfile ?? XboxOAuthClientProfile.XboxAppPc;
        var client = new XboxSignedClient(http);
        var device = await client.RequestDeviceToken(profile.DeviceType, profile.DeviceVersion);
        cancellationToken.ThrowIfCancellationRequested();
        var main = await client.SisuAuth(new XboxSisuAuthRequest { AccessToken = fresh.AccessToken, ClientId = profile.ClientId, DeviceToken = device.Token, RelyingParty = XboxAuthConstants.XboxLiveRelyingParty });
        cancellationToken.ThrowIfCancellationRequested();
        var mainToken = main.AuthorizationToken ?? throw new InvalidDataException("Xbox authorization was not returned.");
        var mainClaims = mainToken.XuiClaims ?? throw new InvalidDataException("Xbox account claims were not returned.");
        var expiry = DateTimeOffset.TryParse(mainToken.ExpireOn, out var expiresAt) ? expiresAt : (DateTimeOffset?)null;
        if (!requestEvents) return new ConnectedXboxSession($"XBL3.0 x={mainClaims.UserHash};{mainToken.Token}", mainClaims.XboxUserId ?? throw new InvalidDataException("Xbox user ID was not returned."), "", fresh, expiry, profile.Name);
        var events = await client.SisuAuth(new XboxSisuAuthRequest { AccessToken = fresh.AccessToken, ClientId = profile.ClientId, DeviceToken = device.Token, RelyingParty = XboxAuthConstants.XboxEventsRelyingParty });
        var eventsToken = events.AuthorizationToken ?? throw new InvalidDataException("Xbox events authorization was not returned.");
        var eventsClaims = eventsToken.XuiClaims ?? throw new InvalidDataException("Xbox events claims were not returned.");
        return new ConnectedXboxSession($"XBL3.0 x={mainClaims.UserHash};{mainToken.Token}", mainClaims.XboxUserId ?? throw new InvalidDataException("Xbox user ID was not returned."), $"x:XBL3.0 x={eventsClaims.UserHash};{eventsToken.Token}", fresh, expiry, profile.Name);
    }
}
