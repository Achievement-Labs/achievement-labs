package org.achievementlabs.mobile;
import org.json.*;
import org.junit.Test;
import static org.junit.Assert.*;
import java.security.KeyPair;
import java.util.*;

public class MobileEventTokensTests {
 JSONObject account()throws Exception{return new JSONObject().put("xuid","42").put("authorization","XBL3.0 x=123;old-authorization").put("eventToken","x:XBL3.0 x=123;old-event").put("loginProfile","Microsoft browser").put("refreshToken","old-refresh");}
 static JSONObject grant(String value,String hash,String id,String expiry)throws Exception{return new JSONObject().put("Token",value).put("NotAfter",expiry).put("DisplayClaims",new JSONObject().put("xui",new JSONArray().put(new JSONObject().put("uhs",hash).put("xid",id))));}
 static class Fake implements MobileEventTokens.Transport {
  KeyPair deviceKey;List<String> calls=new ArrayList<>();boolean rotated,nullEventToken,presence,missingTitle,emptyTitle;String reject="",hash="123",xuid="42",expiry="2099-01-01T00:00:00Z",profile="42";
  public JSONObject refresh(String token)throws Exception{assertEquals("old-refresh",token);calls.add("refresh");return new JSONObject().put("access_token","private-access").put("refresh_token","next-refresh");}
  public JSONObject post(String url,JSONObject body,KeyPair proof)throws Exception{
   assertTrue("Rotation precedes further requests",rotated);calls.add(url+"|"+body.optString("RelyingParty"));
   if(url.contains(reject)&&!reject.isEmpty())throw new Net.HttpFailure(403,"private-access private-refresh response-must-not-leak");
   if(url.equals("https://device.auth.xboxlive.com/device/authenticate")){deviceKey=proof;assertNotNull(proof);assertEquals(presence?"Win32":"Android",body.getJSONObject("Properties").getString("DeviceType"));return new JSONObject().put("Token","device");}
   if(presence){
    assertSame(deviceKey,proof);
    if(url.equals("https://sisu.xboxlive.com/authorize")){
     assertEquals(OAuth.CLIENT,body.getString("AppId"));assertEquals("device",body.getString("DeviceToken"));assertEquals("t=private-access",body.getString("AccessToken"));
     JSONObject result=new JSONObject().put("UserToken",new JSONObject().put("Token","user"));if(!missingTitle)result.put("TitleToken",new JSONObject().put("Token",emptyTitle?"":"title"));return result;
    }
    JSONObject properties=body.getJSONObject("Properties");assertEquals("title",properties.getString("TitleToken"));assertEquals("user",properties.getJSONArray("UserTokens").getString(0));
    assertEquals(OAuth.url64(OAuth.fixed(((java.security.interfaces.ECPublicKey)deviceKey.getPublic()).getW().getAffineX())),properties.getJSONObject("ProofKey").getString("x"));
   }else{assertNull(proof);assertFalse(body.getJSONObject("Properties").has("TitleToken"));}
   if(url.equals("https://user.auth.xboxlive.com/user/authenticate")){assertEquals("t=private-access",body.getJSONObject("Properties").getString("RpsTicket"));return new JSONObject().put("Token","user");}
   assertEquals("https://xsts.auth.xboxlive.com/xsts/authorize",url);assertEquals("device",body.getJSONObject("Properties").getString("DeviceToken"));
   JSONObject result=body.getString("RelyingParty").equals("http://events.xboxlive.com")?grant("issued-event",hash,xuid,expiry):grant("identity","123","42",presence?expiry:"2099-01-01T00:00:00Z");
   if(nullEventToken&&body.getString("RelyingParty").equals("http://events.xboxlive.com"))result.put("Token",JSONObject.NULL);return result;
  }
  public JSONObject connect(String auth)throws Exception{calls.add("profile-read");return new JSONObject().put("xuid",profile).put("authorization",auth);}
 }
 JSONObject acquire(JSONObject a,Fake f)throws Exception{return MobileEventTokens.acquire(a,next->{assertEquals("next-refresh",next);f.rotated=true;},"14",f);}
 @Test public void matchingTokenOnlyAfterReadOnlyVerification()throws Exception{JSONObject a=account();Fake f=new Fake();JSONObject result=acquire(a,f);assertEquals("x:XBL3.0 x=123;issued-event",result.getString("eventToken"));assertEquals("next-refresh",result.getString("refreshToken"));assertEquals("x:XBL3.0 x=123;old-event",a.getString("eventToken"));assertEquals(6,f.calls.size());assertFalse(f.calls.toString().contains("v20.events"));assertFalse(f.calls.toString().contains("achievements.xboxlive"));}
 @Test public void refusalStopsWithoutRetryAndSanitizesDiagnostics()throws Exception{Fake f=new Fake();f.reject="device.auth";try{acquire(account(),f);fail();}catch(MobileEventTokens.Failure e){assertTrue(e.getMessage().contains("HTTP 403"));assertFalse(e.getMessage().contains("private-"));}assertEquals(2,f.calls.size());assertTrue(f.rotated);}
 @Test public void identityMismatchPreventsEventRequest()throws Exception{Fake f=new Fake();f.profile="43";try{acquire(account(),f);fail();}catch(MobileEventTokens.Failure expected){}assertFalse(f.calls.toString().contains("http://events.xboxlive.com"));}
 @Test public void eventMismatchOrExpiryRetainsPreviousToken()throws Exception{for(String mode:new String[]{"hash","xuid","expiry","malformed"}){Fake f=new Fake();if(mode.equals("hash"))f.hash="999";if(mode.equals("xuid"))f.xuid="43";if(mode.equals("expiry"))f.expiry="2000-01-01T00:00:00Z";if(mode.equals("malformed"))f.expiry="not-a-date";JSONObject a=account();try{acquire(a,f);fail(mode);}catch(MobileEventTokens.Failure expected){}assertEquals("x:XBL3.0 x=123;old-event",a.getString("eventToken"));}}
 @Test public void nullTokenDoesNotReplaceAValidSavedToken()throws Exception{JSONObject a=account();Fake f=new Fake();f.nullEventToken=true;try{acquire(a,f);fail();}catch(MobileEventTokens.Failure expected){}assertEquals("x:XBL3.0 x=123;old-event",a.getString("eventToken"));}
 @Test public void rotationStorageFailureStopsBeforeXboxRequests()throws Exception{Fake f=new Fake();try{MobileEventTokens.acquire(account(),next->{throw new Exception("Protected storage unavailable");},"14",f);fail();}catch(Exception expected){}assertEquals(1,f.calls.size());}
 @Test public void importedSessionCannotUseProbe()throws Exception{JSONObject a=account();a.remove("refreshToken");Fake f=new Fake();try{acquire(a,f);fail();}catch(MobileEventTokens.Failure expected){}assertTrue(f.calls.isEmpty());}

 @Test public void mobilePresenceKeepsLibraryAndEventCredentialsAndNeverRequestsEvents()throws Exception{
  JSONObject before=account();Fake f=new Fake();f.presence=true;JSONObject result=MobileEventTokens.presence(before,next->{f.rotated=true;},"14",f);
  assertEquals(before.getString("authorization"),result.getString("authorization"));assertEquals(before.getString("eventToken"),result.getString("eventToken"));
  assertEquals("XBL3.0 x=123;identity",result.getString("presenceAuthorization"));assertEquals("2099-01-01T00:00:00Z",result.getString("presenceAuthorizationExpiresAt"));
  assertEquals("title-device-v1",result.getString("presenceAuthorizationFlow"));assertEquals("next-refresh",result.getString("refreshToken"));assertEquals(Core.b64(f.deviceKey.getPrivate().getEncoded()),result.getString("presenceSigningKey"));assertFalse(before.has("presenceAuthorization"));assertEquals(5,f.calls.size());
  assertFalse(f.calls.toString().contains("events.xboxlive"));assertFalse(f.calls.toString().contains("presence-heartbeat"));
 }
 @Test public void mismatchedPresenceIdentityIsNotSaved()throws Exception{JSONObject before=account();Fake f=new Fake();f.presence=true;f.profile="43";try{MobileEventTokens.presence(before,next->{f.rotated=true;},"14",f);fail();}catch(MobileEventTokens.Failure expected){}assertFalse(before.has("presenceAuthorization"));}
 @Test public void fullPresenceRejectsMissingTitleExpiryAndRefusalWithoutFallback()throws Exception{
  for(String mode:new String[]{"missing","empty","expiry","sisu-refused","xsts-refused"}){
   JSONObject before=account();Fake f=new Fake();f.presence=true;f.missingTitle=mode.equals("missing");f.emptyTitle=mode.equals("empty");if(mode.equals("expiry"))f.expiry="2000-01-01T00:00:00Z";if(mode.equals("sisu-refused"))f.reject="sisu";if(mode.equals("xsts-refused"))f.reject="xsts";
   try{MobileEventTokens.presence(before,next->{f.rotated=true;},"14",f);fail(mode);}catch(MobileEventTokens.Failure expected){assertFalse(expected.getMessage().contains("private-"));}
   if(f.missingTitle||f.emptyTitle)assertFalse(f.calls.toString().contains("xsts.auth"));
   assertFalse(before.has("presenceAuthorization"));assertTrue(f.rotated);assertFalse(f.calls.toString().contains("presence-heartbeat"));assertFalse(f.calls.toString().contains("events.xboxlive"));
  }
 }
}
