package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;
public class TitleSearchTests {
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
