package org.achievementlabs.mobile;
import org.json.*;
import java.util.*;
final class QueueTransfer {
 static JSONObject parse(String text,String owner)throws Exception{
  if(Core.utf(text).length>8*1024*1024)throw new Exception("Queue sync file exceeds 8 MB.");JSONObject value=new JSONObject(text);validate(value,owner);return value;
 }
 static void validate(JSONObject value,String owner)throws Exception{
  Core.numeric(owner);if(value.getInt("schemaVersion")!=1||!value.getString("kind").equals("AchievementLabsQueueTransfer")||!value.getString("ownerXuid").equals(owner))throw new Exception("Queue transfer account or version does not match.");
  JSONArray games=value.getJSONArray("games");if(games.length()>500)throw new Exception("Too many queues.");Set<String> titles=new HashSet<>();int count=0;
  for(int g=0;g<games.length();g++){JSONObject game=games.getJSONObject(g);if(!titles.add(Core.numeric(game.getString("titleId")))||game.getString("name").length()>256)throw new Exception("Invalid transferred game.");JSONArray items=game.getJSONArray("items");count+=items.length();if(items.length()==0||count>50000)throw new Exception("Invalid queue size.");Set<String> ids=new HashSet<>();for(int i=0;i<items.length();i++){JSONObject item=items.getJSONObject(i);if(!ids.add(Core.numeric(item.getString("achievementId")))||item.getString("name").length()>256||item.getString("status").length()>512)throw new Exception("Invalid transferred achievement.");delay(item.getDouble("delaySeconds"));if(!item.isNull("remainingDelaySeconds"))delay(item.getDouble("remainingDelaySeconds"));}}
 }
 static void delay(double value)throws Exception{if(!Double.isFinite(value)||value<0||value>31536000)throw new Exception("Invalid queue delay.");}
 static JSONObject export(String owner,JSONObject saved)throws Exception{
  JSONArray games=new JSONArray();for(Iterator<String> it=saved.keys();it.hasNext();){JSONObject old=saved.getJSONObject(it.next());JSONArray items=new JSONArray(),source=old.getJSONArray("items");for(int i=0;i<source.length();i++){JSONObject row=source.getJSONObject(i);items.put(new JSONObject().put("achievementId",row.getString("achievementId")).put("name",row.getString("name")).put("delaySeconds",row.optDouble("delay",60)).put("remainingDelaySeconds",row.has("remainingDelay")?row.get("remainingDelay"):JSONObject.NULL).put("status",row.optString("status","Waiting")));}if(items.length()>0)games.put(new JSONObject().put("titleId",old.getString("titleId")).put("name",old.getString("name")).put("items",items));}
  JSONObject result=new JSONObject().put("schemaVersion",1).put("kind","AchievementLabsQueueTransfer").put("ownerXuid",owner).put("games",games);validate(result,owner);return result;
 }
 static String importedStatus(String status,boolean unlocked){if(unlocked)return "Already unlocked";if(status.equals("Waiting")||status.equals("Skipped by user"))return status;return "Needs review — transferred progress; refresh before retrying";}
}
