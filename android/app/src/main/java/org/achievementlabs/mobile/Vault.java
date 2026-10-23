package org.achievementlabs.mobile;
import android.content.Context;
import android.security.keystore.*;
import android.util.AtomicFile;
import java.security.*;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;
import java.io.*;
import java.util.*;
import org.json.*;
final class Vault {
 private final AtomicFile file;private final String alias="AchievementLabs.Account.v1";
 Vault(Context c){file=new AtomicFile(new File(c.getFilesDir(),"protected-state.bin"));}
 private SecretKey key()throws Exception{KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);if(store.containsAlias(alias))return (SecretKey)store.getKey(alias,null);KeyGenerator gen=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");gen.init(new KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());return gen.generateKey();}
 synchronized JSONObject read()throws Exception{if(!file.getBaseFile().exists())return new JSONObject();byte[] b=file.readFully();if(b.length<29||b.length>64*1024*1024)throw new Exception("Invalid protected storage.");Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,Arrays.copyOfRange(b,0,12)));return new JSONObject(Core.text(c.doFinal(Arrays.copyOfRange(b,12,b.length))));}
 synchronized void save(JSONObject state)throws Exception{Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.ENCRYPT_MODE,key());byte[] encrypted=c.doFinal(Core.utf(state.toString()));FileOutputStream out=null;try{out=file.startWrite();out.write(c.getIV());out.write(encrypted);file.finishWrite(out);}catch(Exception e){file.failWrite(out);throw e;}}
}
