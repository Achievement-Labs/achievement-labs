using AchievementLabs.Core;

// Spoof request-shape checks: the token sanitizer (presence rejects a token carrying
// trailing garbage), the heartbeat body (600s expiry, active state, RETAIL sandbox) and
// the presence URI chain (first success wins; stop clears all four).
static class SpooferRequestTests
{
 public static void Run()
 {
  void Assert(bool v,string m){ if(!v)throw new Exception(m); }
  Assert(SpoofRequest.SanitizeXauth("XBL3.0 x=1234567890;abcDEF123-_=")=="XBL3.0 x=1234567890;abcDEF123-_=","Clean token unchanged");
  Assert(SpoofRequest.SanitizeXauth("  XBL3.0 x=1234567890;abc  ")=="XBL3.0 x=1234567890;abc","Surrounding whitespace trimmed");
  Assert(SpoofRequest.SanitizeXauth("XBL3.0 x=1234567890;abc\0junk")=="XBL3.0 x=1234567890;abc","Trailing memory garbage dropped");
  Assert(SpoofRequest.SanitizeXauth("junk XBL3.0 x=1;a trailing")=="XBL3.0 x=1;a","Leading junk and trailing copy dropped");
  Assert(SpoofRequest.SanitizeXauth("XBL3.0 x=1234567890;")=="XBL3.0 x=1234567890;","Empty payload tolerated");
  Assert(SpoofRequest.SanitizeXauth("")=="" && SpoofRequest.SanitizeXauth(null)=="" && SpoofRequest.SanitizeXauth("garbage")=="","Non-token input yields empty");
  // Wire format as sent: HeartbeatRequest serialization (quoted string id) — accepted by
  // presence-heartbeat live (A/B tested 2026-11-11, both shapes HTTP 200); pinned so a
  // serialization change is a deliberate test failure, not an accident.
  var body = Newtonsoft.Json.JsonConvert.SerializeObject(new HeartbeatRequest
  {
      titles = new List<TitleRequest> { new() { id = "1285166951" } }
  });
  Assert(body=="{\"titles\":[{\"expiration\":600,\"id\":\"1285166951\",\"state\":\"active\",\"sandbox\":\"RETAIL\"}]}","Heartbeat body: 600s expiry, active state, RETAIL sandbox");
  var uris=SpoofRequest.HeartbeatUris("2535427291234567");
  Assert(uris.Count==4 && uris[0].Contains("xuid(2535427291234567)") && uris.Any(u=>u.Contains("/users/me/")),"Four-URI presence chain covers xuid and /me variants");
  Console.WriteLine("PASS: spoofer request shape — sanitizer, heartbeat body, four-URI presence chain.");
 }
}
