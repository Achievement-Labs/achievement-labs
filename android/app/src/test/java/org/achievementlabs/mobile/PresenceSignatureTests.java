package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;import java.security.*;import java.security.spec.ECGenParameterSpec;import java.io.*;import java.util.*;
public class PresenceSignatureTests {
 @Test public void postAndDeleteSignExactWireBytes()throws Exception{
  KeyPairGenerator g=KeyPairGenerator.getInstance("EC");g.initialize(new ECGenParameterSpec("secp256r1"));KeyPair key=g.generateKeyPair();String url="https://presence-heartbeat.xboxlive.com/users/xuid(42)/devices/current/",auth="XBL3.0 x=1;synthetic";
  for(String method:new String[]{"POST","DELETE"}){
   String body=method.equals("POST")?"{\"titles\":[{\"id\":\"7\"}]}":"";
   byte[] header=Core.un64(PresenceSignature.header(method,url,auth,body,Core.b64(key.getPrivate().getEncoded()),1700000000000L));assertEquals(76,header.length);
   ByteArrayOutputStream m=new ByteArrayOutputStream();m.write(header,0,4);m.write(0);m.write(header,4,8);m.write(0);m.write(Core.utf(method+"\0/users/xuid(42)/devices/current/\0"+auth+"\0"+body+"\0"));
   byte[] raw=Arrays.copyOfRange(header,12,76);byte[] der=der(raw);Signature verify=Signature.getInstance("SHA256withECDSA");verify.initVerify(key.getPublic());verify.update(m.toByteArray());assertTrue(verify.verify(der));
   byte[] bad=m.toByteArray();bad[bad.length-1]=1;verify.initVerify(key.getPublic());verify.update(bad);assertFalse(verify.verify(der));
  }
 }
 static byte[] der(byte[] raw)throws Exception{ByteArrayOutputStream ints=new ByteArrayOutputStream();for(int offset:new int[]{0,32}){byte[] n=new java.math.BigInteger(1,Arrays.copyOfRange(raw,offset,offset+32)).toByteArray();ints.write(2);ints.write(n.length);ints.write(n);}ByteArrayOutputStream out=new ByteArrayOutputStream();out.write(48);out.write(ints.size());out.write(ints.toByteArray());return out.toByteArray();}
 @Test public void cannotSignForAnUnrelatedHost()throws Exception{try{PresenceSignature.header("POST","https://example.com/","synthetic","","",0);fail();}catch(Exception expected){}}
 @Test public void expiryMetadataIsBoundToExactToken()throws Exception{JSONObject a=new JSONObject().put("eventToken","one").put("eventTokenExpiresAt","2099-01-01T00:00:00Z").put("eventTokenExpiryHash",EventTokenLifetime.fingerprint("one"));assertFalse(EventTokenLifetime.expiry(a).isEmpty());a.put("eventToken","two");assertEquals("",EventTokenLifetime.expiry(a));EventTokenLifetime.sanitize(a);assertFalse(a.has("eventTokenExpiresAt"));}
}
