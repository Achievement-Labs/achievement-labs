using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XboxAuthNet.XboxLive.Crypto;

namespace AchievementLabs.Core;

// Same RPS user + proof-of-possession device/XSTS flow used by the mobile apps.
// Authentication and a read-only profile check only; never sends presence or events.
public static class NativeEventTokens
{
    public sealed record Grant(string Value, DateTimeOffset ExpiresAt)
    { public override string ToString() => "[redacted event authorization]"; }
    public static async Task<Grant> ExchangeAsync(HttpClient http, string access, string expectedXuid, string deviceType, CancellationToken ct)
    {
        if (!ulong.TryParse(expectedXuid, out _) || string.IsNullOrWhiteSpace(access)) throw new InvalidDataException("Sign in with Microsoft before retrieving an event token.");
        var signer = new XboxRequestSigner(new ECDCertificatePopCryptoProvider());
        async Task<JsonDocument> Post(string url, object body, bool signed = false)
        {
            var json = JsonSerializer.Serialize(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
            if (signed) request.Headers.TryAddWithoutValidation("Signature", signer.SignRequest(url, "", json));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException($"Xbox authentication refused (HTTP {(int)response.StatusCode}). Existing event token retained.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Xbox authentication response exceeded its limit.");
            return JsonDocument.Parse(bytes);
        }
        string Token(JsonDocument doc)
        {
            var value = doc.RootElement.GetProperty("Token").GetString() ?? "";
            if (value.Length is 0 or > 131072 || value.Any(char.IsWhiteSpace)) throw new InvalidDataException("Invalid Xbox authorization response.");
            return value;
        }
        JsonElement Claim(JsonDocument doc)
        {
            var claims = doc.RootElement.GetProperty("DisplayClaims").GetProperty("xui");
            if (claims.ValueKind == JsonValueKind.Object) return claims;
            if (claims.ValueKind == JsonValueKind.Array && claims.GetArrayLength() == 1) return claims[0];
            throw new InvalidDataException("Ambiguous Xbox identity; existing token retained.");
        }
        static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
        using var device = await Post("https://device.auth.xboxlive.com/device/authenticate", new { RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT", Properties = new { AuthMethod = "ProofOfPossession", Id = "{" + Guid.NewGuid() + "}", DeviceType = deviceType, Version = "0.0.0", ProofKey = signer.ProofKey, SerialNumber = "{" + Guid.NewGuid() + "}" } }, true);
        using var user = await Post("https://user.auth.xboxlive.com/user/authenticate", new { RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT", Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "t=" + access } });
        var properties = new { SandboxId = "RETAIL", UserTokens = new[] { Token(user) }, DeviceToken = Token(device) };
        Task<JsonDocument> Xsts(string party) => Post("https://xsts.auth.xboxlive.com/xsts/authorize", new { RelyingParty = party, TokenType = "JWT", Properties = properties });
        using var identity = await Xsts("http://xboxlive.com");
        var claim = Claim(identity); var hash = Text(claim, "uhs"); var xid = Text(claim, "xid");
        if (hash.Length == 0 || hash.Any(char.IsWhiteSpace) || hash.Contains(';') || xid.Length > 0 && xid != expectedXuid) throw new InvalidDataException("Xbox account mismatch; existing token retained.");
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://profile.xboxlive.com/users/me/profile/settings?settings=Gamertag"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "XBL3.0 x=" + hash + ";" + Token(identity));
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "2");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("Xbox profile verification refused; existing token retained.");
            using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var users = profile.RootElement.GetProperty("profileUsers");
            if (users.GetArrayLength() != 1 || users[0].GetProperty("id").GetString() != expectedXuid) throw new InvalidDataException("Xbox account mismatch; existing token retained.");
        }
        using var events = await Xsts("http://events.xboxlive.com");
        var eventClaim = Claim(events); var eventXid = Text(eventClaim, "xid");
        if (Text(eventClaim, "uhs") != hash || eventXid.Length > 0 && eventXid != expectedXuid) throw new InvalidDataException("Event account mismatch; existing token retained.");
        if (!DateTimeOffset.TryParse(Text(events.RootElement, "NotAfter"), out var expiry) || expiry <= DateTimeOffset.UtcNow.AddMinutes(1)) throw new InvalidDataException("Event-token expiry is missing or too soon; existing token retained.");
        return new Grant("x:XBL3.0 x=" + hash + ";" + Token(events), expiry);
    }
}
