package org.achievementlabs.mobile;
import org.json.*;
import java.security.*;
import java.security.interfaces.ECPublicKey;
import java.security.spec.ECGenParameterSpec;
import java.time.Instant;
import java.util.*;

/** Explicit authentication-only probe. Never calls presence, achievements or the event collector. */
final class MobileEventTokens {
 interface Transport {
  JSONObject refresh(String token)throws Exception;
  JSONObject post(String url,JSONObject body,KeyPair proof)throws Exception;
  JSONObject connect(String authorization)throws Exception;
 }
 static final Transport LIVE=new Transport(){
  public JSONObject refresh(String token)throws Exception{return OAuth.token("grant_type=refresh_token&refresh_token="+Net.enc(token));}
  public JSONObject post(String url,JSONObject body,KeyPair proof)throws Exception{
   return proof!=null?OAuth.signed(url,body,proof):new JSONObject(Net.read(url,"POST",null,"1",body.toString(),null,1024*1024));
  }
  public JSONObject connect(String authorization)throws Exception{return Net.connect(authorization);}
 };
 static final class Failure extends Exception {Failure(String message){super(message);}}
 static JSONObject post(Transport t,String url,JSONObject body,KeyPair proof,String stage)throws Exception{
  try{return t.post(url,body,proof);}catch(Net.HttpFailure e){throw new Failure(stage+" refused (HTTP "+e.status+"). Existing event token retained.");}
  catch(Exception e){throw new Failure(stage+" could not complete. Existing event token retained.");}
 }
 static JSONObject claim(JSONObject response)throws Exception{
  JSONObject claims=response.getJSONObject("DisplayClaims");Object value=claims.get("xui");
  if(value instanceof JSONObject)return (JSONObject)value;
  JSONArray rows=(JSONArray)value;if(rows.length()!=1)throw new Failure("Ambiguous Xbox identity. Existing event token retained.");return rows.getJSONObject(0);
 }
 static String token(JSONObject response)throws Exception{Object raw=response.opt("Token");String value=raw instanceof String?(String)raw:"";if(value.isEmpty()||value.length()>131072)throw new Failure("Missing or oversized Xbox authorization.");return value;}
 static JSONObject acquire(JSONObject previous,OAuth.Rotation rotation,String version,Transport t)throws Exception{
  String refresh=previous.optString("refreshToken");if(refresh.isEmpty()||!"Microsoft browser".equals(previous.optString("loginProfile")))throw new Failure("Sign in with Microsoft on this device first. PC QR/manual sessions cannot run this test.");
  JSONObject oauth;
  try{oauth=t.refresh(refresh);}catch(Net.HttpFailure e){throw new Failure("Microsoft renewal refused (HTTP "+e.status+"). Sign in again. Existing event token retained.");}
  catch(Exception e){throw new Failure("Microsoft renewal could not complete. Existing event token retained.");}
  Object rawRefresh=oauth.opt("refresh_token");String next=rawRefresh instanceof String&&!((String)rawRefresh).isEmpty()?(String)rawRefresh:refresh;
  rotation.save(next); // Microsoft may invalidate the previous refresh token even if Xbox refuses the probe.
  Object rawAccess=oauth.opt("access_token");String access=rawAccess instanceof String?(String)rawAccess:"";if(access.isEmpty())throw new Failure("Microsoft returned no access token.");
  KeyPairGenerator gen=KeyPairGenerator.getInstance("EC");gen.initialize(new ECGenParameterSpec("secp256r1"));KeyPair key=gen.generateKeyPair();ECPublicKey pub=(ECPublicKey)key.getPublic();
  JSONObject proof=new JSONObject().put("use","sig").put("alg","ES256").put("kty","EC").put("crv","P-256").put("x",OAuth.url64(OAuth.fixed(pub.getW().getAffineX()))).put("y",OAuth.url64(OAuth.fixed(pub.getW().getAffineY())));
  JSONObject device=post(t,"https://device.auth.xboxlive.com/device/authenticate",new JSONObject().put("RelyingParty","http://auth.xboxlive.com").put("TokenType","JWT").put("Properties",new JSONObject().put("AuthMethod","ProofOfPossession").put("Id","{"+UUID.randomUUID()+"}").put("DeviceType","Android").put("Version",version).put("ProofKey",proof).put("SerialNumber","{"+UUID.randomUUID()+"}")),key,"Xbox mobile device authorization");
  JSONObject user=post(t,"https://user.auth.xboxlive.com/user/authenticate",new JSONObject().put("RelyingParty","http://auth.xboxlive.com").put("TokenType","JWT").put("Properties",new JSONObject().put("AuthMethod","RPS").put("SiteName","user.auth.xboxlive.com").put("RpsTicket","t="+access)),null,"Xbox user authorization");
  JSONObject properties=new JSONObject().put("SandboxId","RETAIL").put("UserTokens",new JSONArray().put(token(user))).put("DeviceToken",token(device));
  JSONObject identity=post(t,"https://xsts.auth.xboxlive.com/xsts/authorize",new JSONObject().put("RelyingParty","http://xboxlive.com").put("TokenType","JWT").put("Properties",properties),null,"Xbox identity authorization");
  JSONObject id=claim(identity);String hash=id.optString("uhs");if(hash.isEmpty())throw new Failure("Xbox returned no user identity.");
  JSONObject verified;
  try{verified=t.connect(Core.token("XBL3.0 x="+hash+";"+token(identity),false));}
  catch(Net.HttpFailure e){throw new Failure("Xbox profile verification refused (HTTP "+e.status+"). Existing event token retained.");}
  catch(Exception e){throw new Failure("Xbox profile verification could not complete. Existing event token retained.");}
  if(!previous.getString("xuid").equals(verified.getString("xuid"))||(!id.optString("xid").isEmpty()&&!previous.getString("xuid").equals(id.optString("xid"))))throw new Failure("Xbox account mismatch. Existing account and event token retained.");
  JSONObject events=post(t,"https://xsts.auth.xboxlive.com/xsts/authorize",new JSONObject().put("RelyingParty","http://events.xboxlive.com").put("TokenType","JWT").put("Properties",properties),null,"Xbox event-token authorization");
  JSONObject eventId=claim(events);if(!hash.equals(eventId.optString("uhs"))||(!eventId.optString("xid").isEmpty()&&!previous.getString("xuid").equals(eventId.optString("xid"))))throw new Failure("Event account mismatch. Existing event token retained.");
  Instant expiry;try{expiry=Instant.parse(events.getString("NotAfter"));}catch(Exception e){throw new Failure("Event-token expiry missing or invalid. Existing token retained.");}
  if(!expiry.isAfter(Instant.now().plusSeconds(60)))throw new Failure("Event token already expiring. Existing token retained.");
  JSONObject result=new JSONObject(previous.toString());result.put("refreshToken",next).put("authorization",verified.getString("authorization")).put("authorizationExpiresAt",identity.optString("NotAfter")).put("eventToken",Core.token("x:XBL3.0 x="+hash+";"+token(events),true)).put("eventTokenExpiresAt",expiry.toString());result.put("eventTokenExpiryHash",EventTokenLifetime.fingerprint(result.getString("eventToken")));return result;
 }
}
