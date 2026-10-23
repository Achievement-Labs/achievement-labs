package org.achievementlabs.mobile;
import org.json.*;
import java.util.*;

final class TitleSearch {
 static JSONObject progress(JSONObject game,JSONArray rows)throws Exception{
  JSONObject result=new JSONObject(game.toString());if(rows.length()==0)return result;
  int unlocked=0;for(int i=0;i<rows.length();i++)if(rows.getJSONObject(i).optBoolean("isUnlocked"))unlocked++;
  String platforms=result.optJSONArray("devices")==null?"":result.getJSONArray("devices").toString();
  JSONObject previous=result.optJSONObject("achievement");
  if((platforms.contains("Xbox360")||platforms.contains("GFWL"))&&previous!=null&&unlocked<previous.optInt("currentAchievements"))return result;
  return result.put("achievement",new JSONObject().put("currentAchievements",unlocked).put("totalAchievements",rows.length()));
 }
 static String query(String input)throws Exception{
  String q=input.trim();if(q.isEmpty()||q.length()>200)throw new Exception("Enter a game name or decimal Xbox Title ID (up to 200 characters).");
  if(q.matches("[0-9]+")){try{long id=Long.parseLong(q);if(id<1||id>4294967295L)throw new Exception();}catch(Exception e){throw new Exception("Enter a Title ID from 1 to 4294967295.");}}
  return q;
 }
 static JSONObject direct(String input)throws Exception{
  String q=query(input);if(!q.matches("[0-9]+"))return null;String id=Long.toString(Long.parseLong(q));
  return new JSONObject().put("titleId",id).put("name","Title "+id).put("devices",new JSONArray().put("XboxOne"));
 }
 static String url(String input)throws Exception{return "https://dbox.tools/api/title_ids/?name="+Net.enc(query(input))+"&limit=100&offset=0";}
 static JSONArray parse(String text)throws Exception{
  if(Core.utf(text).length>2*1024*1024)throw new Exception("Game search response is too large.");
  JSONArray items=new JSONObject(text).getJSONArray("items");if(items.length()>100)throw new Exception("Too many search results.");
  Set<String> seen=new HashSet<>();List<JSONObject> results=new ArrayList<>();
  for(int n=0;n<items.length();n++){
   JSONObject item=items.getJSONObject(n);String hex=item.optString("title_id",item.optString("titleId")),name=item.optString("name").trim();
   if(!hex.matches("[0-9a-fA-F]{1,8}")||name.isEmpty()||name.length()>300)continue;
   long id=Long.parseLong(hex,16);if(id==0||!seen.add(Long.toString(id)))continue;
   JSONArray systems=item.optJSONArray("systems"),devices=new JSONArray();boolean pc=false,legacy=false;
   if(systems!=null)for(int i=0;i<systems.length();i++)switch(systems.optString(i).toUpperCase(Locale.ROOT)){
    case "XBOXONE":devices.put("XboxOne");break;case "XBOXSERIES":case "SCARLETT":devices.put("XboxSeries");break;
    case "XBOX360":devices.put("Xbox360");legacy=true;break;case "PC":case "WINDOWS":devices.put("PC");pc=true;break;default:break;
   }
   if(pc&&legacy)devices.put("GFWL");
   results.add(new JSONObject().put("titleId",Long.toString(id)).put("name",name).put("devices",devices));
  }
  results.sort((a,b)->a.optString("name").compareToIgnoreCase(b.optString("name")));return new JSONArray(results);
 }
 static JSONArray search(String input)throws Exception{
  JSONObject direct=direct(input);if(direct!=null)return new JSONArray().put(direct);
  return parse(Net.read(url(input),"GET",null,null,null,null,2*1024*1024));
 }
}
