package org.achievementlabs.mobile;
import java.net.URI;import java.io.*;import java.security.*;import java.security.spec.PKCS8EncodedKeySpec;import java.util.Base64;
final class PresenceSignature {
 static String header(String method,String url,String authorization,String body,String encoded,long millis)throws Exception{
  URI u=new URI(url);if(!"https".equals(u.getScheme())||!"presence-heartbeat.xboxlive.com".equals(u.getHost())||u.getUserInfo()!=null||u.getRawQuery()!=null||u.getFragment()!=null||(u.getPort()!=-1&&u.getPort()!=443)||!(method.equals("POST")||method.equals("DELETE"))||Core.utf(body).length>8192)throw new Exception("Invalid presence signing request.");
  PrivateKey key=KeyFactory.getInstance("EC").generatePrivate(new PKCS8EncodedKeySpec(Base64.getDecoder().decode(encoded)));
  long stamp=(millis/1000+11644473600L)*10000000L;ByteArrayOutputStream message=new ByteArrayOutputStream();DataOutputStream d=new DataOutputStream(message);
  d.writeInt(1);d.writeByte(0);d.writeLong(stamp);d.writeByte(0);d.write(Core.utf(method+"\0"+u.getRawPath()+"\0"+authorization+"\0"));d.write(Core.utf(body));d.writeByte(0);
  Signature signature=Signature.getInstance("SHA256withECDSA");signature.initSign(key);signature.update(message.toByteArray());ByteArrayOutputStream bytes=new ByteArrayOutputStream();DataOutputStream h=new DataOutputStream(bytes);h.writeInt(1);h.writeLong(stamp);h.write(OAuth.rawSignature(signature.sign()));return Core.b64(bytes.toByteArray());
 }
}
