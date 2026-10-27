using System.Security.Cryptography;
using System.Text;
using System.Web;
using XboxAuthNet.OAuth;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;
using XboxAuthNet.XboxLive;
using AchievementLabs.Services.Auth;
namespace AchievementLabs.Core;

// Uses the same registered desktop redirect as the original app, with PKCE and state.
// Mac captures the registered redirect in its native sign-in window; Windows retains the manual fallback.
public sealed class BrowserXboxLogin : IDisposable
{
    private readonly HttpClient http = new();
    private readonly string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    private readonly string state = Base64Url(RandomNumberGenerator.GetBytes(32));
    private readonly CodeFlowLiveApiClient oauth;
    private bool consumed;
    private readonly XboxOAuthClientProfile profile;
    public BrowserXboxLogin(XboxOAuthClientProfile? selectedProfile = null) { profile = selectedProfile ?? XboxOAuthClientProfile.XboxAppPc; oauth = new(profile.ClientId, XboxAuthConstants.XboxScope, http); }
    public Uri StartUri => new(oauth.CreateAuthorizeCodeUrl(new CodeFlowAuthorizationParameter
    {
        State = state, CodeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), CodeChallengeMethod = "S256", RedirectUri = CodeFlowLiveApiClient.OAuthDesktop
    }));
    public async Task<ConnectedXboxSession> CompleteAsync(string redirect, CancellationToken cancellationToken)
    {
        if (consumed) throw new InvalidOperationException("Start a new sign-in attempt.");
        var code = ValidateRedirect(redirect, state);
        consumed = true;
        var response = await oauth.GetAccessToken(new CodeFlowAccessTokenParameter { Code = code, CodeVerifier = verifier, RedirectUrl = CodeFlowLiveApiClient.OAuthDesktop }, cancellationToken);
        return await SavedXboxSession.GenerateAsync(response, cancellationToken, profile, requestEvents: !DesktopPlatform.IsUnixDesktop);
    }
    public static string ValidateRedirect(string redirect, string expectedState)
    {
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != "/oauth20_desktop.srf" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new InvalidDataException("Unexpected sign-in redirect.");
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query["state"] != expectedState || query.GetValues("state")?.Length != 1 || query.GetValues("code")?.Length != 1 || string.IsNullOrWhiteSpace(query["code"]) || query["error"] != null) throw new InvalidDataException("Invalid or unsuccessful sign-in response.");
        return query["code"]!;
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() => http.Dispose();
}
