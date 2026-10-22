package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;import java.util.*;
public class QueueQRTests {
 @Test public void portableQueuesAndScannerIsolation()throws Exception {
  JSONObject transfer=new JSONObject("{\"schemaVersion\":1,\"kind\":\"AchievementLabsQueueTransfer\",\"ownerXuid\":\"42\",\"games\":[{\"titleId\":\"7\",\"name\":\"Unicode 🎮\",\"items\":[{\"achievementId\":\"1\",\"name\":\"First\",\"delaySeconds\":120,\"remainingDelaySeconds\":25,\"status\":\"Waiting\"}]}]}");
  ArrayList<String> frames=QueueQR.frames(transfer,"42");Collections.reverse(frames);Core.Collector collector=new Core.Collector(true);JSONObject result=null;for(String frame:frames)result=collector.add(frame);assertNotNull(result);QueueTransfer.validate(result.getJSONObject("queue"),"42");assertEquals(25,result.getJSONObject("queue").getJSONArray("games").getJSONObject(0).getJSONArray("items").getJSONObject(0).getInt("remainingDelaySeconds"));
  JSONObject large=new JSONObject(transfer.toString());JSONArray items=large.getJSONArray("games").getJSONObject(0).getJSONArray("items");for(int i=2;i<=30;i++)items.put(new JSONObject(items.getJSONObject(0).toString()).put("achievementId",""+i));ArrayList<String> many=QueueQR.frames(large,"42");assertTrue(many.size()>1);Collections.reverse(many);Core.Collector multiple=new Core.Collector(true);JSONObject assembled=null;for(String frame:many){JSONObject value=multiple.add(frame);if(value!=null)assembled=value;multiple.add(frame);}assertNotNull(assembled);assertEquals(30,assembled.getJSONObject("queue").getJSONArray("games").getJSONObject(0).getJSONArray("items").length());
  try{new Core.Collector().add(frames.get(0));fail();}catch(Exception expected){}
  try{QueueTransfer.validate(result.getJSONObject("queue"),"43");fail();}catch(Exception expected){}
  try{new Core.Collector(true).add("ALQ1|"+"0".repeat(64)+"|0|1|e30=");fail();}catch(Exception expected){}
 }
 @Test public void signInOnlyCrossPlatformVector()throws Exception {
  JSONObject p=Core.openTransfer(Core.utf("{\"schemaVersion\":1,\"nonce\":\"AAECAwQFBgcICQoL\",\"ciphertext\":\"PCCleK2Ar3rbJOX42IYWT7nnqxabEjEYGl3H9nQObvtvMoLe17R7/FaeXdm6pQQajywU5TWkyqBe40N2dsHPzKh+ikn94QYZIWWYXdT8d4ZM4qwWHwB+CZ6e8bkWhJuh6Z0vg8o1nHX1eVCQclcDU5gCGtuKt0DpDIt197XWXiKZM3Wy3Li5OnKAPMhStAm6Rp2RUEZQ52i2MZ0blq8=\",\"tag\":\"2FgnXkL3zIZxo5ImJtQlhA==\"}"),"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",100);
  assertEquals("signIn",p.getString("kind"));assertEquals("",p.getString("eventToken"));assertEquals("XBL3.0 x=123;synthetic-account",p.getString("authorization"));
 }
}
