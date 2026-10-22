package org.achievementlabs.mobile;
import org.json.*;
import java.util.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import javax.crypto.*;
import javax.crypto.spec.*;
import java.time.*;

public final class Core {
 static byte[] utf(String s){return s.getBytes(StandardCharsets.UTF_8);}
 static String text(byte[] b){String value=new String(b,StandardCharsets.UTF_8);return value.startsWith("\ufeff")?value.substring(1):value;}
 static String b64(byte[] b){return Base64.getEncoder().encodeToString(b);}
 static byte[] un64(String s){return Base64.getDecoder().decode(s);}
 static String hash(byte[] b)throws Exception{return hex(MessageDigest.getInstance("SHA-256").digest(b));}
 static String hex(byte[] b){StringBuilder s=new StringBuilder();for(byte v:b)s.append(String.format(Locale.ROOT,"%02x",v));return s.toString();}
 static final class SessionGate {
  private long generation;private boolean active,executing;
  synchronized long start()throws Exception{if(active||executing)throw new Exception("The previous request is still finishing. Wait before resuming.");active=true;return ++generation;}
  synchronized boolean begin(long token){if(!active||token!=generation)return false;executing=true;return true;}
  synchronized boolean running(long token){return active&&token==generation;}
  synchronized void pause(){active=false;generation++;}
  synchronized void finish(long token){active=false;executing=false;}
  synchronized boolean busy(){return active||executing;}
  synchronized boolean finishing(){return executing&&!active;}
 }
 static void sameOwner(String expected,String actual)throws InterruptedException{if(expected.isEmpty()||!expected.equals(actual))throw new InterruptedException("Account changed; session stopped.");}
 static String uuid(String value)throws Exception{if(!UUID.fromString(value).toString().equalsIgnoreCase(value))throw new Exception("Invalid service configuration ID.");return value;}
 static String numeric(String s)throws Exception{if(!s.matches("[0-9]{1,20}"))throw new Exception("Invalid numeric identifier.");return s;}
 static String token(String s,boolean event)throws Exception{
  s=s.trim();if(event){if(s.startsWith("XBL3.0 "))s="x:"+s;}else if(s.startsWith("x:XBL3.0 "))s=s.substring(2);
  if(s.length()>65536 || !s.matches((event?"x:":"")+"XBL3\\.0 x=[^;\\s]+;[^\\s]+"))throw new Exception("Enter a complete Xbox "+(event?"event token":"authorization")+".");return s;
 }
 static boolean eventRequirements(JSONArray requirements){if(requirements==null)return false;for(int i=0;i<requirements.length();i++){JSONObject r=requirements.optJSONObject(i);String id=r==null?"":r.optString("id");if(!id.isEmpty()&&!id.equals("00000000-0000-0000-0000-000000000000"))return true;}return false;}
 static boolean eventOnly(JSONObject p){return "eventToken".equals(p.optString("kind"));}
 static JSONObject openTransfer(byte[] bytes,String key,long now)throws Exception{
  if(bytes.length>128*1024)throw new Exception("Transfer too large.");JSONObject envelope=new JSONObject(text(bytes));
  byte[] k=Base64.getUrlDecoder().decode(key),nonce=un64(envelope.getString("nonce")),tag=un64(envelope.getString("tag")),encrypted=un64(envelope.getString("ciphertext"));
  if(envelope.getInt("schemaVersion")!=1||k.length!=32||nonce.length!=12||tag.length!=16)throw new Exception("Invalid encrypted transfer.");
  byte[] all=Arrays.copyOf(encrypted,encrypted.length+16);System.arraycopy(tag,0,all,encrypted.length,16);
  Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.DECRYPT_MODE,new SecretKeySpec(k,"AES"),new GCMParameterSpec(128,nonce));c.updateAAD(utf("AchievementLabs-PC-Transfer-v1"));
  byte[] clear=c.doFinal(all);Arrays.fill(k,(byte)0);JSONObject p=new JSONObject(text(clear));Arrays.fill(clear,(byte)0);
  numeric(p.getString("xuid"));long expiry=p.getLong("expiresAt");if(p.getInt("schemaVersion")!=1||expiry<=now||expiry>now+900)throw new Exception("Transfer expired. Generate fresh PC codes.");
  if(eventOnly(p)){if(!p.optString("authorization").isEmpty()||p.optString("eventToken").isEmpty())throw new Exception("Invalid event-only transfer.");}
  else{if(!p.optString("kind","account").equals("account")&&!p.optString("kind").equals("signIn"))throw new Exception("Unsupported transfer type.");token(p.getString("authorization"),false);}
  if(p.optString("kind").equals("signIn")&&!p.optString("eventToken").isEmpty())throw new Exception("Invalid sign-in-only transfer.");
  if(!p.optString("eventToken").isEmpty())token(p.getString("eventToken"),true);return p;
 }
 static final class Collector {
  final boolean queueOnly; Collector(){this(false);} Collector(boolean queueOnly){this.queueOnly=queueOnly;}
  String digest;int total;Map<Integer,String> pieces=new HashMap<>();
  String progress(){return pieces.size()+" / "+total+" codes collected";}
  JSONObject add(String s)throws Exception{
   if(s.length()>1000)throw new Exception("Code too large.");String[] a=s.split("\\|",-1);
   if(a.length!=5||!a[0].equals(queueOnly?"ALQ1":"ALPC1")||!a[1].matches("[a-f0-9]{64}"))throw new Exception("Scan an Achievement Labs PC code.");
   int i=Integer.parseInt(a[2]),n=Integer.parseInt(a[3]);if(n<1||n>256||i<0||i>=n||a[4].isEmpty()||a[4].length()>800)throw new Exception("Invalid code.");
   if(digest!=null&&(!digest.equals(a[1])||total!=n))throw new Exception("Different transfer. Reset the scanner.");digest=a[1];total=n;
   if(pieces.containsKey(i)&&!pieces.get(i).equals(a[4]))throw new Exception("Conflicting code. Reset scanner.");pieces.put(i,a[4]);if(pieces.size()!=total)return null;
   StringBuilder joined=new StringBuilder();for(int x=0;x<total;x++)joined.append(pieces.get(x));byte[] data=un64(joined.toString());
   if(data.length>180*1024||!hash(data).equals(digest))throw new Exception("QR checksum failed.");JSONObject p=new JSONObject(text(data));if(queueOnly){if(data.length>128*1024)throw new Exception("Queue QR too large.");return new JSONObject().put("queue",p);}if(p.getString("key").length()!=43||un64(p.getString("file")).length>128*1024)throw new Exception("Invalid QR transfer.");return p;
  }
 }
 static List<String> payloads(String template,JSONArray requests,String title,String xuid,long now)throws Exception{
  numeric(title);numeric(xuid);if(requests.length()<1||requests.length()>1000)throw new Exception("Invalid mapping.");List<String> result=new ArrayList<>();String stamp=Instant.ofEpochMilli(now).toString(),session=UUID.randomUUID().toString();
  for(int n=0;n<requests.length();n++){
   String body=template.startsWith("\ufeff")?template.substring(1):template;JSONArray ops=requests.getJSONArray(n);if(ops.length()<1||ops.length()>100)throw new Exception("Invalid mapping operations.");
   for(int i=0;i<ops.length();i++){JSONObject op=ops.getJSONObject(i);String target=op.getString("target"),value=op.getString("value");if(target.isEmpty()||!body.contains(target)||value.length()>1024*1024)throw new Exception("Mapping does not match template.");int count=(body.length()-body.replace(target,"").length())/target.length();if((long)body.length()+(long)count*(value.length()-target.length())>2*1024*1024)throw new Exception("Payload expansion limit.");body=body.replace(target,value);}
   body=body.replace("REPLACESEQ",Long.toString(now+n)).replace("REPLACETIME",stamp).replace("REPLACEXUID",xuid).replace("REPLACESESSIONGUID",session);
   if(body.matches("(?s).*REPLACE[A-Z0-9_]+.*"))throw new Exception("Unresolved event placeholder.");JSONObject base=new JSONObject(body).getJSONObject("data").getJSONObject("baseData");if(!base.optString("titleId").equals(title))throw new Exception("Wrong title in mapping.");uuid(base.getString("serviceConfigId"));result.add(new JSONObject(body).toString());
  }return result;
 }
 static void collectorAck(String body)throws Exception{if(body.isEmpty())return;JSONObject j=new JSONObject(body);Object fail=j.opt("TokenCrackingFailure");if((fail!=null&&fail!=JSONObject.NULL&&!Boolean.FALSE.equals(fail))||j.optInt("rej",0)>0||(j.has("acc")&&j.optInt("acc",0)==0))throw new Exception("Collector rejected the event; refresh your event token.");JSONObject efi=j.optJSONObject("efi");if(efi!=null)for(Iterator<String> it=efi.keys();it.hasNext();){Object v=efi.get(it.next());if("all".equals(v)||(v instanceof JSONArray&&((JSONArray)v).length()>0))throw new Exception("Collector rejected the event.");}}
 static boolean newer(String a,String b){try{String[] x=a.split("\\."),y=b.split("\\.");if(x.length!=3||y.length!=3)return false;for(int i=0;i<3;i++){int v=Integer.parseInt(x[i]),w=Integer.parseInt(y[i]);if(v!=w)return v>w;}return false;}catch(Exception e){return false;}}
}

