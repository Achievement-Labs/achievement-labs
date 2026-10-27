using System.Net;
using System.Text;
using System.Text.Json;
using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

internal static class MacEventTokenChecks
{
    private sealed class Fake : HttpMessageHandler
    {
        public string ProfileXuid = "42", EventHash = "same", EventXuid = "42";
        public bool Expired, RefuseDevice, Ambiguous;
        public readonly List<string> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host; Calls.Add(host);
            object result;
            if (host == "profile.xboxlive.com") result = new { profileUsers = new[] { new { id = ProfileXuid } } };
            else
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (host == "device.auth.xboxlive.com")
                {
                    if (!request.Headers.Contains("Signature") || body.RootElement.GetProperty("Properties").GetProperty("AuthMethod").GetString() != "ProofOfPossession") throw new Exception("Missing device proof.");
                    if (RefuseDevice) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    result = new { Token = "synthetic-device" };
                }
                else if (host == "user.auth.xboxlive.com") result = new { Token = "synthetic-user" };
                else if (host == "xsts.auth.xboxlive.com")
                {
                    var events = body.RootElement.GetProperty("RelyingParty").GetString() == "http://events.xboxlive.com";
                    var claim = new { uhs = events ? EventHash : "same", xid = events ? EventXuid : "42" };
                    result = new { Token = "synthetic-token", NotAfter = DateTimeOffset.UtcNow.AddMinutes(Expired && events ? -1 : 30).ToString("O"), DisplayClaims = new { xui = Ambiguous ? new[] { claim, claim } : new[] { claim } } };
                }
                else throw new Exception("Unexpected authentication endpoint.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }
    public static async Task RunAsync()
    {
        var valid = new Fake();
        using (var http = new HttpClient(valid))
        {
            var grant = await NativeEventTokens.ExchangeAsync(http, "synthetic-access", "42", "Win32", CancellationToken.None);
            if (grant.Value != "x:XBL3.0 x=same;synthetic-token" || grant.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)) throw new Exception("Mobile-style event exchange failed.");
            if (valid.Calls.Count != 5 || valid.Calls[3] != "profile.xboxlive.com") throw new Exception("Identity verification order changed.");
        }
        foreach (var fake in new[] { new Fake { ProfileXuid = "43" }, new Fake { EventHash = "other" }, new Fake { EventXuid = "43" }, new Fake { Expired = true }, new Fake { RefuseDevice = true }, new Fake { Ambiguous = true } })
        {
            using var http = new HttpClient(fake);
            try { await NativeEventTokens.ExchangeAsync(http, "synthetic-access", "42", "Win32", CancellationToken.None); throw new Exception("Invalid event grant accepted."); }
            catch (InvalidDataException) { }
            if (fake.ProfileXuid != "42" && fake.Calls.Count != 4) throw new Exception("Event requested before account match.");
        }
    }
}
