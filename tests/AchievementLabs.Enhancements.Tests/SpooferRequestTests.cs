using AchievementLabs.Core;

// Spoof request-shape checks: the token sanitizer (presence rejects a token carrying
// trailing garbage), the heartbeat body (numeric title id, 600s expiry, active state,
// RETAIL sandbox) and the presence URI chain (first success wins; stop clears all four).
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
  Assert(SpoofRequest.BuildHeartbeatBody("1285166951")=="{\"titles\":[{\"expiration\":600,\"id\":1285166951,\"state\":\"active\",\"sandbox\":\"RETAIL\"}]}","Heartbeat body uses the accepted shape");
  Assert(SpoofRequest.BuildHeartbeatBody(" 1285166951 ")==SpoofRequest.BuildHeartbeatBody("1285166951"),"Whitespace around title id tolerated");
  Assert(!SpoofRequest.BuildHeartbeatBody("1285166951").Contains("\"1285166951\""),"Title id is numeric, never quoted");
  bool threw=false;try{SpoofRequest.BuildHeartbeatBody("not-a-number");}catch(FormatException){threw=true;}
  Assert(threw,"Non-numeric title id rejected before any request");
  var uris=SpoofRequest.HeartbeatUris("2535427291234567");
  Assert(uris.Count==4 && uris[0].Contains("xuid(2535427291234567)") && uris.Any(u=>u.Contains("/users/me/")),"Four-URI presence chain covers xuid and /me variants");
  Console.WriteLine("PASS: spoofer request shape — sanitizer, numeric heartbeat body and four-URI presence chain.");
 }
}
