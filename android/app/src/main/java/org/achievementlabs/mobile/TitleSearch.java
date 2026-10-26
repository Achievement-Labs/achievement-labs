package org.achievementlabs.mobile;
import org.json.*;
import java.util.*;
import java.util.regex.*;

final class TitleSearch {
 static final String[] ROMANS={"","I","II","III","IV","V","VI","VII","VIII","IX","X","XI","XII","XIII","XIV","XV","XVI","XVII","XVIII","XIX","XX","XXI","XXII","XXIII","XXIV","XXV","XXVI","XXVII","XXVIII","XXIX","XXX"};
 private static final Pattern WORDS=Pattern.compile("[\\p{L}\\p{N}]+");
 private static String alternate(String word,int start){
  if(word.matches("[1-9][0-9]?")){int n=Integer.parseInt(word);if(n<=30)return ROMANS[n];}
  for(int n=1;n<ROMANS.length;n++)if(ROMANS[n].equalsIgnoreCase(word)&&!(n==1&&start==0))return Integer.toString(n);
  return null;
 }
 static List<String> variants(String input)throws Exception{
  String value=query(input);List<String> result=new ArrayList<>();result.add(value);
  Matcher words=WORDS.matcher(value);StringBuilder combined=new StringBuilder();int last=0,count=0;
  while(words.find()){
   String replacement=alternate(words.group(),words.start());
   combined.append(value,last,words.start()).append(replacement==null?words.group():replacement);last=words.end();
   if(replacement!=null&&count++<6)addVariant(result,value.substring(0,words.start())+replacement+value.substring(words.end()));
  }
  combined.append(value.substring(last));addVariant(result,combined.toString());return result;
 }
 private static void addVariant(List<String> result,String value){if(value.length()<=200&&result.size()<8&&result.stream().noneMatch(x->x.equalsIgnoreCase(value)))result.add(value);}
 interface Reader {String read(String url)throws Exception;}
 static final class SearchResult {final JSONArray items;final boolean partial;SearchResult(JSONArray items,boolean partial){this.items=items;this.partial=partial;}}
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
  return new JSONObject().put("titleId",id).put("name","Title "+id).put("devices",new JSONArray());
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
    case "XBOX360":devices.put("Xbox360");legacy=true;break;case "PC":case "WINDOWS":case "WINDOWS10":devices.put("PC");pc=true;break;
    case "WIN32":devices.put("Win32");break;case "WIN8":case "WINDOWS8":case "WINDOWS81":devices.put("Windows8");break;
    case "MOBILE":devices.put("Mobile");break;case "WINDOWSPHONE":devices.put("WindowsPhone");break;case "IOS":devices.put("iOS");break;case "ANDROID":devices.put("Android");break;
    default:devices.put(systems.optString(i));break;
   }
   if(pc&&legacy)devices.put("GFWL");
   results.add(new JSONObject().put("titleId",Long.toString(id)).put("name",name).put("devices",devices));
  }
  results.sort((a,b)->a.optString("name").compareToIgnoreCase(b.optString("name")));return new JSONArray(results);
 }
 static JSONArray search(String input)throws Exception{
  return searchResults(input).items;
 }
 static SearchResult searchResults(String input)throws Exception{return searchResults(input,u->Net.read(u,"GET",null,null,null,null,2*1024*1024));}
 static SearchResult searchResults(String input,Reader reader)throws Exception{
  JSONObject direct=direct(input);if(direct!=null)return new SearchResult(new JSONArray().put(direct),false);
  Map<String,JSONObject> found=new LinkedHashMap<>();int succeeded=0,consecutiveFailures=0;Exception failure=null;
  for(String variant:variants(input))try{
   JSONArray items=parse(reader.read(url(variant)));succeeded++;consecutiveFailures=0;
   for(int i=0;i<items.length()&&found.size()<300;i++){JSONObject item=items.getJSONObject(i);found.putIfAbsent(item.getString("titleId"),item);}
  }catch(Exception e){if(Thread.currentThread().isInterrupted())throw e;failure=e;if(++consecutiveFailures>=2)break;}
  if(succeeded==0)throw failure==null?new Exception("Game search is unavailable."):failure;
  List<JSONObject> sorted=new ArrayList<>(found.values());sorted.sort((a,b)->a.optString("name").compareToIgnoreCase(b.optString("name")));
  return new SearchResult(new JSONArray(sorted),failure!=null);
 }
}
