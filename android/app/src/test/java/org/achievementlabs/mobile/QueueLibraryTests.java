package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;

public class QueueLibraryTests {
 private JSONObject item(String title,String id)throws Exception{return new JSONObject().put("game",new JSONObject().put("titleId",title).put("name","Game "+title)).put("achievementId",id).put("name","Achievement "+id).put("delay",120).put("status","Waiting");}
 @Test public void autoRestoreOnlyForAnIdleDifferentGame()throws Exception{
  JSONObject saved=new JSONObject();JSONArray a=new JSONArray().put(item("7","1")),b=new JSONArray().put(item("8","1"));
  QueueLibrary.capture(saved,new JSONArray(),a);QueueLibrary.capture(saved,new JSONArray(),b);
  assertTrue(QueueLibrary.shouldRestore(saved,a,"8",false));
  assertFalse(QueueLibrary.shouldRestore(saved,a,"7",false));
  assertFalse(QueueLibrary.shouldRestore(saved,a,"8",true));
  assertFalse(QueueLibrary.shouldRestore(saved,a,"9",false));
  JSONArray restored=QueueLibrary.restore(saved.getJSONObject("8"));assertEquals(120,restored.getJSONObject(0).getInt("delay"));
  assertEquals("7/1",QueueLibrary.key(a.getJSONObject(0)));assertTrue(saved.has("7"));
 }
 @Test public void separateQueuesSurviveReloadWithoutReplacingActive()throws Exception{
  JSONObject saved=new JSONObject();JSONArray a=new JSONArray().put(item("7","1")),b=new JSONArray().put(item("8","1"));
  QueueLibrary.capture(saved,new JSONArray(),a);QueueLibrary.capture(saved,new JSONArray(),b);
  JSONObject restored=new JSONObject(saved.toString());assertEquals("7/1",QueueLibrary.key(QueueLibrary.restore(restored.getJSONObject("7")).getJSONObject(0)));assertTrue(restored.has("8"));
  JSONArray combined=QueueLibrary.append(a,b);assertEquals(2,combined.length());assertEquals(1,a.length());assertEquals(2,QueueLibrary.append(combined,b).length());
 }
 @Test public void deleteSurvivesAutosaveAndRestartWithoutRemovingOtherGames()throws Exception{
  JSONObject saved=new JSONObject();JSONArray active=new JSONArray().put(item("7","1")).put(item("8","1"));
  QueueLibrary.capture(saved,new JSONArray(),active);JSONArray previous=QueueLibrary.copy(active);
  active=QueueLibrary.deleteGame(saved,active,"7");QueueLibrary.capture(saved,previous,active);
  assertFalse(saved.has("7"));assertTrue(saved.has("8"));assertEquals("8/1",QueueLibrary.key(active.getJSONObject(0)));
  JSONObject restored=new JSONObject(saved.toString());JSONArray loaded=new JSONArray(active.toString());QueueLibrary.capture(restored,new JSONArray(),loaded);
  assertFalse(restored.has("7"));assertTrue(restored.has("8"));
  loaded=QueueLibrary.deleteGame(restored,loaded,"8");QueueLibrary.capture(restored,active,loaded);assertEquals(0,loaded.length());assertEquals(0,restored.length());
 }
 @Test public void removalUsesIdentityAfterIndicesShift()throws Exception{
  JSONArray items=new JSONArray().put(item("7","1")).put(item("7","2")).put(item("8","1"));
  JSONObject saved=new JSONObject();QueueLibrary.capture(saved,new JSONArray(),items);JSONArray before=QueueLibrary.copy(items);
  items.remove(0);assertEquals(0,QueueLibrary.find(items,"7/2"));items.remove(QueueLibrary.find(items,"7/2"));
  QueueLibrary.capture(saved,before,items);assertTrue(saved.has("7"));assertTrue(saved.has("8"));
  try{QueueLibrary.find(items,"7/2");fail();}catch(Exception expected){}
 }
 @Test public void interruptedProgressAndDelayArePreserved()throws Exception{
  JSONObject saved=new JSONObject();JSONObject i=item("7","1").put("status","Submitting").put("remainingDelay",23);
  QueueLibrary.capture(saved,new JSONArray(),new JSONArray().put(i));JSONObject restored=QueueLibrary.restore(saved.getJSONObject("7")).getJSONObject(0);
  assertTrue(restored.getString("status").startsWith("Interrupted"));assertEquals(23,restored.getInt("remainingDelay"));
  saved.getJSONObject("7").put("titleId","8");try{QueueLibrary.restore(saved.getJSONObject("7"));fail();}catch(Exception expected){}
 }
 @Test public void eventStreamIsCompactAndValuesRemainUnchanged()throws Exception{
  String pretty="{\n \"data\": {\n \"baseData\": {\"titleId\":7,\"serviceConfigId\":\"00000000-0000-0000-0000-000000000001\",\"value\":REPLACEID,\"label\":\"two words\\nnext\"}\n}\n}";
  JSONArray recipe=new JSONArray("[[{\"target\":\"REPLACEID\",\"value\":\"11\"}]]");
  String body=Core.payloads(pretty,recipe,"7","0",1000).get(0);assertFalse(body.contains("\n"));assertFalse(body.contains("\r"));
  JSONObject base=new JSONObject(body).getJSONObject("data").getJSONObject("baseData");assertEquals(11,base.getInt("value"));assertEquals("two words\nnext",base.getString("label"));
 }
 @Test public void transferRoundTripAndAccountChecks()throws Exception{
  JSONObject saved=new JSONObject();QueueLibrary.capture(saved,new JSONArray(),new JSONArray().put(item("7","1").put("remainingDelay",23)));
  String text=QueueTransfer.export("42",saved).toString();assertFalse(text.contains("authorization"));JSONObject parsed=QueueTransfer.parse(text,"42");assertEquals(23,parsed.getJSONArray("games").getJSONObject(0).getJSONArray("items").getJSONObject(0).getInt("remainingDelaySeconds"));
  try{QueueTransfer.parse(text,"43");fail();}catch(Exception expected){}
  parsed.getJSONArray("games").getJSONObject(0).getJSONArray("items").getJSONObject(0).put("delaySeconds",-1);try{QueueTransfer.validate(parsed,"42");fail();}catch(Exception expected){}
  assertEquals("Waiting",QueueTransfer.importedStatus("Waiting",false));assertTrue(QueueTransfer.importedStatus("Accepted",false).startsWith("Needs review"));assertEquals("Already unlocked",QueueTransfer.importedStatus("Failed",true));
 }
 @Test public void failuresRequireReviewAndAreNotCompletion(){assertTrue(QueuePolicy.review("Unconfirmed — connection lost"));assertTrue(QueuePolicy.review("Failed: HTTP 400"));assertFalse(QueuePolicy.terminal("Unconfirmed"));assertTrue(QueuePolicy.terminal("Skipped by user"));assertTrue(QueuePolicy.terminal("Confirmed unlocked"));assertFalse(QueuePolicy.transientFailure(new Net.HttpFailure(403,"Forbidden")));assertTrue(QueuePolicy.transientFailure(new java.io.IOException()));}
}
