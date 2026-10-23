package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;
public class CatalogueTests {
 @Test public void offlinePacksRetainBundledTitlesAndRollbackInvalidIdentity()throws Exception{
  Catalogue c=new Catalogue(new JSONObject().put("99",new JSONObject()));
  JSONObject title=new JSONObject().put("template","{\"data\":{\"baseData\":{\"titleId\":\"7\",\"serviceConfigId\":\"00000000-0000-0000-0000-000000000001\",\"id\":REPLACEID}}}");
  title.put("recipes",new JSONObject().put("recipes",new JSONObject().put("1",new JSONArray().put(new JSONArray().put(new JSONObject().put("target","REPLACEID").put("value","1"))))));
  JSONObject feed=new JSONObject().put("schemaVersion",1).put("archiveSHA256","a".repeat(64)).put("generatedAt",java.time.Instant.now().toString()).put("titles",new JSONObject().put("7",title).put("99",title));
  assertEquals(1,c.merge(new JSONObject(feed.toString())));assertFalse(c.synced.has("99"));
  assertEquals(0,c.merge(feed));assertFalse(c.payloads("7","1","0").isEmpty());
  String before=c.synced.toString();feed.getJSONObject("titles").put("8",title);
  try{c.merge(feed);fail("Wrong game identity must reject the pack");}catch(Exception expected){}
  assertEquals(before,c.synced.toString());
 }
}
