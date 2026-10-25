package org.achievementlabs.mobile;
import org.json.*;import java.security.MessageDigest;import java.time.Instant;
final class EventTokenLifetime {
 static String fingerprint(String token)throws Exception{return Core.b64(MessageDigest.getInstance("SHA-256").digest(Core.utf(token)));}
 static String expiry(JSONObject a)throws Exception{if(a==null||a.optString("eventToken").isEmpty()||!fingerprint(a.optString("eventToken")).equals(a.optString("eventTokenExpiryHash")))return "";try{return Instant.parse(a.getString("eventTokenExpiresAt")).toString();}catch(Exception e){return "";}}
 static void sanitize(JSONObject a)throws Exception{if(a!=null&&expiry(a).isEmpty()){a.remove("eventTokenExpiresAt");a.remove("eventTokenExpiryHash");}}
}
