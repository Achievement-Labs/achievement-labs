package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import org.json.*;
public class StoreArtworkTests {
 private JSONArray fixture()throws Exception{return new JSONArray().put(new JSONObject().put("purpose","Poster").put("width",1440).put("height",2160).put("url","https://store-images.s-microsoft.com/poster")).put(new JSONObject().put("purpose","SuperHeroArt").put("width",1920).put("height",1080).put("url","https://store-images.s-microsoft.com/hero"));}
 @Test public void orientationAndSplitViewChooseDifferentAssets()throws Exception{assertEquals("Poster",StoreArtwork.best(fixture(),390,844).getString("purpose"));assertEquals("SuperHeroArt",StoreArtwork.best(fixture(),1024,768).getString("purpose"));assertEquals("Poster",StoreArtwork.best(fixture(),400,768).getString("purpose"));}
 @Test public void missingShapeFallsBackAndInvalidWindowHasNoSelection()throws Exception{assertNotNull(StoreArtwork.best(new JSONArray().put(fixture().get(1)),390,844));assertNull(StoreArtwork.best(new JSONArray(),390,844));assertNull(StoreArtwork.best(fixture(),0,0));}
 @Test public void onlySecureMicrosoftArtworkIsAccepted(){assertEquals("https://store-images.s-microsoft.com/image",StoreArtwork.secure("//store-images.s-microsoft.com/image"));for(String bad:new String[]{"http://store-images.s-microsoft.com/x","https://user:secret@store-images.s-microsoft.com/x","https://microsoft.com.attacker.test/x","file:///x","javascript:bad"})assertEquals("",StoreArtwork.secure(bad));}
}
