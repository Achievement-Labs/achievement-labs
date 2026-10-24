package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;
public class TitleSearchTests {
 @Test public void numeralAliasesAreBoundedAndPreserveOfficialNames()throws Exception{
  assertTrue(TitleSearch.variants("Black Ops 3").contains("Black Ops III"));
  assertTrue(TitleSearch.variants("Black Ops iii").contains("Black Ops 3"));
  assertTrue(TitleSearch.variants("Final Fantasy 13-2").contains("Final Fantasy XIII-2"));
  assertEquals(java.util.List.of("I Am Alive"),TitleSearch.variants("I Am Alive"));
  assertEquals(1,TitleSearch.variants("Civilization MIXED 007").size());
  assertTrue(TitleSearch.variants("Game 1 2 3 4 5 6 7 8").size()<=8);
  java.util.List<String> requests=new java.util.ArrayList<>();
  TitleSearch.SearchResult result=TitleSearch.searchResults("Black Ops 3",url->{requests.add(url);return url.contains("Ops%20III")?"{\"items\":[{\"title_id\":\"7\",\"name\":\"Black Ops III\",\"systems\":[\"XBOXONE\"]},{\"title_id\":\"8\",\"name\":\"Black Ops III\",\"systems\":[\"PC\"]}]}":"{\"items\":[]}";});
  assertEquals(2,requests.size());assertEquals(2,result.items.length());assertFalse(result.partial);
  assertEquals("Black Ops III",result.items.getJSONObject(0).getString("name"));
  assertNotEquals(result.items.getJSONObject(0).getString("titleId"),result.items.getJSONObject(1).getString("titleId"));
 }
 @Test public void aliasFailuresKeepMatchesAndDirectIDsNeverRequestNames()throws Exception{
  String row="{\"items\":[{\"title_id\":\"7\",\"name\":\"Game III\",\"systems\":[\"XBOXONE\"]}]}";
  TitleSearch.SearchResult merged=TitleSearch.searchResults("Game 3",url->row);assertEquals(1,merged.items.length());
  TitleSearch.SearchResult partial=TitleSearch.searchResults("Game 3",url->{if(url.contains("%20III"))throw new java.io.IOException("Unavailable");return row;});
  assertTrue(partial.partial);assertEquals(1,partial.items.length());
  try{TitleSearch.searchResults("Game 3",url->{throw new java.io.IOException("Unavailable");});fail("All failures must surface");}catch(java.io.IOException expected){}
  TitleSearch.SearchResult direct=TitleSearch.searchResults("7",url->{fail("No public request for a Title ID");return "";});
  assertEquals("7",direct.items.getJSONObject(0).getString("titleId"));assertEquals(0,direct.items.getJSONObject(0).getJSONArray("devices").length());
 }
 @Test public void discoveredProgressUsesXboxRowsWithoutMutatingTheProfile()throws Exception{
  JSONObject found=TitleSearch.direct("7");JSONArray rows=new JSONArray().put(new JSONObject().put("isUnlocked",true)).put(new JSONObject().put("isUnlocked",false));
  JSONObject updated=TitleSearch.progress(found,rows);assertFalse(found.has("achievement"));
  assertEquals(1,updated.getJSONObject("achievement").getInt("currentAchievements"));assertEquals(2,updated.getJSONObject("achievement").getInt("totalAchievements"));
  assertEquals(updated.toString(),TitleSearch.progress(updated,new JSONArray()).toString());
 }
 @Test public void selectedXboxTitleMustMatchRequestedEdition()throws Exception{
  JSONArray titles=new JSONArray().put(new JSONObject().put("titleId","99")).put(new JSONObject().put("titleId","7"));
  assertEquals("7",Net.selectedTitle(titles,"7").getString("titleId"));
  try{Net.selectedTitle(titles,"8");fail("A different title must never be opened");}catch(JSONException expected){}
 }
 @Test public void boundsAndPublicQuery()throws Exception{
  assertEquals("7",TitleSearch.direct(" 0007 ").getString("titleId"));assertEquals("4294967295",TitleSearch.direct("4294967295").getString("titleId"));
  for(String value:new String[]{"0","4294967296","","x".repeat(201)})try{TitleSearch.direct(value);fail(value);}catch(Exception expected){}
  assertNull(TitleSearch.direct("Halo"));assertTrue(TitleSearch.url("Halo & Gears").contains("name=Halo%20%26%20Gears&limit=100&offset=0"));
  try{Net.read(TitleSearch.url("Halo"),"GET","synthetic-token",null,null,null,100);fail("Credentials must be rejected before opening a connection");}catch(Exception expected){assertTrue(expected.getMessage().contains("Public requests"));}
 }
 @Test public void hexIDsEditionsAndDuplicateFiltering()throws Exception{
  JSONArray rows=new JSONArray().put(new JSONObject().put("title_id","0D174C79").put("name","Halo 5").put("systems",new JSONArray().put("XBOXONE")))
   .put(new JSONObject().put("title_id","0D174C79").put("name","Duplicate"))
   .put(new JSONObject().put("title_id","FFFFFFFF").put("name","Legacy").put("systems",new JSONArray().put("PC").put("XBOX360")))
   .put(new JSONObject().put("title_id","../bad").put("name","Invalid"));
  JSONArray result=TitleSearch.parse(new JSONObject().put("items",rows).toString());assertEquals(2,result.length());
  assertEquals(Long.toString(Long.parseLong("0D174C79",16)),result.getJSONObject(0).getString("titleId"));
  assertTrue(result.getJSONObject(1).getJSONArray("devices").toString().contains("GFWL"));assertFalse(result.getJSONObject(0).has("achievement"));
 }
}
