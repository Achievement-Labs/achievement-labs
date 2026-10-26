package org.achievementlabs.mobile;
import org.json.JSONObject;
/** Discard obsolete experimental presence data while preserving the account and event token. */
final class PresenceState {
 static void clear(JSONObject account) {
  if(account==null)return;
  for(String field:new String[]{"presenceAuthorizationFlow","presenceAuthorization","presenceAuthorizationExpiresAt","presenceSigningKey","presenceDeviceToken","presenceDeviceExpiresAt"})account.remove(field);
 }
}
