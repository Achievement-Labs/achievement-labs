package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;
public class EventTokenLifetimeTests {
 @Test public void expiryMetadataIsBoundToExactToken()throws Exception{JSONObject a=new JSONObject().put("eventToken","one").put("eventTokenExpiresAt","2099-01-01T00:00:00Z").put("eventTokenExpiryHash",EventTokenLifetime.fingerprint("one"));assertFalse(EventTokenLifetime.expiry(a).isEmpty());a.put("eventToken","two");assertEquals("",EventTokenLifetime.expiry(a));EventTokenLifetime.sanitize(a);assertFalse(a.has("eventTokenExpiresAt"));}
 @Test public void obsoletePresenceStateDoesNotRemoveAccountOrEventData()throws Exception {
  JSONObject a=new JSONObject().put("authorization","existing").put("eventToken","one").put("refreshToken","renewal").put("eventTokenExpiresAt","2099-01-01T00:00:00Z").put("eventTokenExpiryHash",EventTokenLifetime.fingerprint("one"));
  for(String field:new String[]{"presenceAuthorizationFlow","presenceAuthorization","presenceAuthorizationExpiresAt","presenceSigningKey","presenceDeviceToken","presenceDeviceExpiresAt"})a.put(field,"obsolete");
  PresenceState.clear(a);assertEquals(5,a.length());assertEquals("existing",a.getString("authorization"));assertEquals("renewal",a.getString("refreshToken"));assertFalse(EventTokenLifetime.expiry(a).isEmpty());PresenceState.clear(null);
 }
}
