package org.achievementlabs.mobile;
import org.json.*;
import java.util.*;

/** Account partitioning is supplied by SessionEngine's protected storage. */
final class QueueLibrary {
 static String key(JSONObject item)throws Exception{return Core.numeric(item.getJSONObject("game").getString("titleId"))+"/"+Core.numeric(item.getString("achievementId"));}
 static boolean shouldRestore(JSONObject saved,JSONArray current,String title,boolean active)throws Exception{
  Core.numeric(title);if(active||!saved.has(title))return false;
  for(int i=0;i<current.length();i++)if(title.equals(current.getJSONObject(i).getJSONObject("game").getString("titleId")))return false;
  return true;
 }
 static JSONArray copy(JSONArray items)throws Exception{return new JSONArray(items.toString());}
 static JSONArray append(JSONArray current,JSONArray incoming)throws Exception{
  JSONArray result=copy(current);Set<String> ids=new HashSet<>();for(int i=0;i<result.length();i++)ids.add(key(result.getJSONObject(i)));
  for(int i=0;i<incoming.length();i++){JSONObject item=incoming.getJSONObject(i);if(ids.add(key(item)))result.put(new JSONObject(item.toString()));}return result;
 }
 static void capture(JSONObject saved,JSONArray previous,JSONArray current)throws Exception{
  Set<String> titles=new HashSet<>();for(JSONArray list:new JSONArray[]{previous,current})for(int i=0;i<list.length();i++)titles.add(list.getJSONObject(i).getJSONObject("game").getString("titleId"));
  for(String title:titles){JSONArray group=new JSONArray();String name="Game";for(int i=0;i<current.length();i++){JSONObject item=current.getJSONObject(i);key(item);if(item.getJSONObject("game").getString("titleId").equals(title)){name=item.getJSONObject("game").optString("name","Game");group.put(new JSONObject(item.toString()));}}
   if(group.length()>0)saved.put(title,new JSONObject().put("titleId",title).put("name",name).put("items",group).put("updated",System.currentTimeMillis()));
  }
 }
 static JSONArray restore(JSONObject snapshot)throws Exception{
  String title=Core.numeric(snapshot.getString("titleId"));JSONArray result=copy(snapshot.getJSONArray("items"));Set<String> ids=new HashSet<>();
  for(int i=0;i<result.length();i++){JSONObject item=result.getJSONObject(i);double delay=item.optDouble("delay",60);if(!ids.add(key(item))||!item.getJSONObject("game").getString("titleId").equals(title)||!Double.isFinite(delay)||delay<0||delay>31536000)throw new Exception("Invalid saved queue; original retained.");if("Submitting".equals(item.optString("status")))item.put("status","Interrupted — refresh before retry");}
  return result;
 }
 static JSONArray deleteGame(JSONObject saved,JSONArray current,String title)throws Exception{
  Core.numeric(title);JSONArray remaining=new JSONArray();
  for(int i=0;i<current.length();i++){JSONObject item=current.getJSONObject(i);if(!item.getJSONObject("game").getString("titleId").equals(title))remaining.put(new JSONObject(item.toString()));}
  saved.remove(title);return remaining;
 }
 static JSONArray summaries(JSONObject saved)throws Exception{JSONArray result=new JSONArray();List<String> ids=new ArrayList<>();for(Iterator<String> it=saved.keys();it.hasNext();)ids.add(it.next());ids.sort(Comparator.comparing(id->saved.optJSONObject(id).optString("name"),String.CASE_INSENSITIVE_ORDER));for(String id:ids){JSONObject entry=saved.getJSONObject(id);result.put(new JSONObject().put("titleId",id).put("name",entry.optString("name")).put("count",entry.getJSONArray("items").length()).put("updated",entry.optLong("updated")));}return result;}
 static int find(JSONArray items,String id)throws Exception{for(int i=0;i<items.length();i++)if(key(items.getJSONObject(i)).equals(id))return i;throw new Exception("Queue item no longer exists. Refresh the queue.");}
}
