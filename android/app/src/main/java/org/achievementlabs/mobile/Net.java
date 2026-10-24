package org.achievementlabs.mobile;
import org.json.*;
import java.net.*;
import java.io.*;
import java.util.*;
public final class Net {
 static final class HttpFailure extends Exception { final int status; HttpFailure(int code,String message){super(message);status=code;} }
 static String read(String url,String method,String authorization,String version,String body,Map<String,String> extra,int limit)throws Exception{
  URL target=new URL(url);String host=target.getHost();if(!target.getProtocol().equals("https")||target.getUserInfo()!=null)throw new Exception("Invalid secure endpoint.");
  boolean xbox=host.endsWith(".xboxlive.com"),events=host.equals("v20.events.data.microsoft.com"),login=host.equals("login.live.com"),publicData=host.equals("displaycatalog.mp.microsoft.com")||host.equals("raw.githubusercontent.com")||host.equals("api.github.com")||(host.equals("dbox.tools")&&target.getPath().equals("/api/title_ids/"));
  if(!xbox&&!events&&!login&&!publicData)throw new Exception("Endpoint is not allowed.");if(publicData&&authorization!=null)throw new Exception("Public requests cannot contain account credentials.");
  HttpURLConnection c=(HttpURLConnection)target.openConnection();c.setInstanceFollowRedirects(false);c.setConnectTimeout(45000);c.setReadTimeout(45000);c.setRequestMethod(method);c.setRequestProperty("Accept","application/json");c.setRequestProperty("User-Agent","AchievementLabs-Android/0.1");c.setRequestProperty("Accept-Language","en-US");if(authorization!=null)c.setRequestProperty("Authorization",authorization);if(version!=null)c.setRequestProperty("x-xbl-contract-version",version);c.setRequestProperty("Content-Type","application/json");if(extra!=null)for(Map.Entry<String,String> e:extra.entrySet())c.setRequestProperty(e.getKey(),e.getValue());
  try {if(body!=null){c.setDoOutput(true);byte[] data=Core.utf(body);c.setFixedLengthStreamingMode(data.length);try(OutputStream out=c.getOutputStream()){out.write(data);}}
   int code=c.getResponseCode();if(code<200||code>=300)throw new HttpFailure(code,"HTTP "+code+" · "+host+(code==403&&host.startsWith("presence-heartbeat")?". Use full PC sign-in QR; event-only sync does not change heartbeat access.":""));
   try(InputStream in=c.getInputStream();ByteArrayOutputStream out=new ByteArrayOutputStream()){byte[] b=new byte[16384];int n;while((n=in.read(b))!=-1){if(out.size()+n>limit)throw new Exception("Response exceeds size limit.");out.write(b,0,n);}return Core.text(out.toByteArray());}
  } finally {c.disconnect();}
 }
 static String enc(String s){try{return URLEncoder.encode(s,"UTF-8").replace("+","%20");}catch(Exception e){throw new IllegalArgumentException();}}
 static JSONObject xbox(String url,String auth,String version,String method,JSONObject body)throws Exception{String s=read(url,method,auth,version,body==null?null:body.toString(),null,128*1024*1024);return s.isEmpty()?new JSONObject():new JSONObject(s);}
 static JSONObject connect(String auth)throws Exception{
  auth=Core.token(auth,false);JSONObject p=xbox("https://profile.xboxlive.com/users/me/profile/settings?settings=Gamertag,GameDisplayPicRaw,Gamerscore",auth,"2","GET",null).getJSONArray("profileUsers").getJSONObject(0);JSONObject a=new JSONObject().put("authorization",auth).put("xuid",Core.numeric(p.getString("id"))).put("eventToken","").put("gamertag","Xbox account");JSONArray settings=p.getJSONArray("settings");for(int i=0;i<settings.length();i++){JSONObject s=settings.getJSONObject(i);String id=s.getString("id");if(id.equals("Gamertag"))a.put("gamertag",s.getString("value"));if(id.equals("Gamerscore"))a.put("gamerscore",s.getString("value"));if(id.equals("GameDisplayPicRaw"))a.put("gamerpic",s.getString("value"));}return a;
 }
 static JSONArray games(JSONObject a)throws Exception{return xbox("https://titlehub.xboxlive.com/users/xuid("+Core.numeric(a.getString("xuid"))+")/titles/titleHistory/decoration/TitleHistory,Achievement,detail,scid?maxItems=10000",a.getString("authorization"),"2","GET",null).getJSONArray("titles");}
 // Android org.json converts JSONObject.NULL to the string "null" in optString.
 // A null final token must end pagination, never become another request.
 static String nextPage(JSONObject paging)throws Exception{
  if(paging==null||paging.isNull("continuationToken"))return "";
  Object value=paging.get("continuationToken");
  if(!(value instanceof String))throw new Exception("Invalid achievement pagination token.");
  String token=(String)value;return token.equals("null")?"":token;
 }
 static JSONArray achievements(JSONObject a,JSONObject game,String target)throws Exception{
  String id=Core.numeric(game.getString("titleId"));Core.numeric(target);boolean legacy=game.optJSONArray("devices")!=null&&(game.getJSONArray("devices").toString().contains("Xbox360")||game.getJSONArray("devices").toString().contains("GFWL"));JSONArray all=new JSONArray();Set<String> seen=new HashSet<>();String continuation="";
  do {if(Thread.currentThread().isInterrupted())throw new Exception("Cancelled.");JSONObject result=xbox("https://achievements.xboxlive.com/users/xuid("+target+")/"+(legacy?"titleachievements":"achievements")+"?titleId="+id+"&maxItems=1000"+(continuation.isEmpty()?"":"&continuationToken="+enc(continuation)),a.getString("authorization"),legacy?"3":"4","GET",null);JSONArray rows=result.getJSONArray("achievements");for(int i=0;i<rows.length();i++)all.put(rows.getJSONObject(i));JSONObject paging=result.optJSONObject("pagingInfo");continuation=nextPage(paging);if(!continuation.isEmpty()&&!seen.add(continuation))throw new Exception("Repeated achievement page.");if(all.length()>100000)throw new Exception("Too many achievements.");}while(!continuation.isEmpty());return all;
 }
 static JSONObject selectedTitle(JSONArray titles,String id)throws Exception{Core.numeric(id);for(int i=0;i<titles.length();i++){JSONObject game=titles.getJSONObject(i);if(id.equals(game.optString("titleId")))return game;}throw new JSONException("Xbox did not return the selected title.");}
 static JSONObject game(JSONObject a,String id)throws Exception{Core.numeric(id);return selectedTitle(xbox("https://titlehub.xboxlive.com/users/xuid("+a.getString("xuid")+")/titles/batch/decoration/TitleHistory,Achievement,detail,scid",a.getString("authorization"),"2","POST",new JSONObject().put("Pfns",JSONObject.NULL).put("TitleIds",new JSONArray().put(id))).getJSONArray("titles"),id);}
 static void presence(JSONObject a,String title)throws Exception{xbox("https://presence-heartbeat.xboxlive.com/users/xuid("+a.getString("xuid")+")/devices/current/",a.getString("authorization"),"3",title==null?"DELETE":"POST",title==null?null:new JSONObject().put("titles",new JSONArray().put(new JSONObject().put("id",Core.numeric(title)).put("expiration",600).put("state","active").put("sandbox","RETAIL"))));}
 static Double minutes(JSONObject a,String title)throws Exception{JSONObject j=xbox("https://userstats.xboxlive.com/batch",a.getString("authorization"),"2","POST",new JSONObject().put("ArrangeByField","xuid").put("Xuids",new JSONArray().put(a.getString("xuid"))).put("Stats",new JSONArray().put(new JSONObject().put("Name","MinutesPlayed").put("TitleId",Core.numeric(title)))));return findMinutes(j);}
 static Double findMinutes(Object v)throws Exception{if(v instanceof JSONObject){JSONObject j=(JSONObject)v;if(j.optString("name",j.optString("Name")).equalsIgnoreCase("MinutesPlayed")){try{double n=Double.parseDouble(j.optString("value",j.optString("Value")));if(Double.isFinite(n)&&n>=0)return n;}catch(Exception ignored){}}for(Iterator<String> it=j.keys();it.hasNext();){Double r=findMinutes(j.get(it.next()));if(r!=null)return r;}}else if(v instanceof JSONArray){JSONArray j=(JSONArray)v;for(int i=0;i<j.length();i++){Double r=findMinutes(j.get(i));if(r!=null)return r;}}return null;}
}

