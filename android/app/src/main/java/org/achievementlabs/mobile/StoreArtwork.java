package org.achievementlabs.mobile;
import org.json.*;
import java.net.URI;
import java.util.*;

/** Anonymous Store artwork metadata, independent of session/account requests. */
final class StoreArtwork {
 private static final Map<String,JSONArray> CACHE=new LinkedHashMap<>();
 static String secure(String value){
  try{URI u=URI.create(value.startsWith("//")?"https:"+value:value);String h=u.getHost();if(!"https".equals(u.getScheme())||u.getUserInfo()!=null||h==null)return "";h=h.toLowerCase(Locale.ROOT);return h.equals("store-images.s-microsoft.com")||h.endsWith(".xboxlive.com")||h.endsWith(".microsoft.com")?u.toString():"";}catch(Exception e){return "";}
 }
 static JSONArray images(JSONObject product)throws Exception{
  JSONArray result=new JSONArray(),locales=product.optJSONArray("LocalizedProperties");if(locales==null)return result;
  Set<String> seen=new HashSet<>();
  for(int l=0;l<locales.length();l++){JSONArray images=locales.getJSONObject(l).optJSONArray("Images");if(images==null)continue;for(int n=0;n<images.length();n++){JSONObject image=images.getJSONObject(n);double w=image.optDouble("Width",0),h=image.optDouble("Height",0);String url=secure(image.optString("Uri"));if(w>=300&&h>=300&&!url.isEmpty()&&seen.add(url))result.put(new JSONObject().put("url",url).put("width",w).put("height",h).put("purpose",image.optString("ImagePurpose")));}}
  return result;
 }
 static JSONObject best(JSONArray images,double width,double height)throws Exception{
  if(width<=0||height<=0)return null;double ratio=width/height;List<String> purposes=ratio<1?Arrays.asList("Poster","BoxArt","TitledHeroArt","SuperHeroArt"):Arrays.asList("SuperHeroArt","TitledHeroArt","Hero","FeaturePromotionalArt","BoxArt","Poster");JSONObject best=null;double bestScore=Double.POSITIVE_INFINITY;
  for(int i=0;i<images.length();i++){JSONObject image=images.getJSONObject(i);int priority=purposes.indexOf(image.optString("purpose"));double w=image.optDouble("width",0),h=image.optDouble("height",0);if(priority<0||w<=0||h<=0)continue;double r=w/h;boolean shape=ratio<1?r<1:r>=1.4;double score=(shape?0:100)+Math.abs(Math.log(r/ratio))*10+priority*0.01-Math.min(w,3840)*0.000001;if(score<bestScore){bestScore=score;best=image;}}
  return best;
 }
 static JSONArray lookup(String title)throws Exception{
  long id=Long.parseLong(Core.numeric(title));if(id<1||id>4294967295L)throw new Exception("Invalid title.");
  synchronized(CACHE){if(CACHE.containsKey(title))return CACHE.get(title);}
  JSONObject root=read("https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=XboxTitleId&Value="+id+"&market=US&languages=en-US");JSONArray products=root.optJSONArray("Products"),all=new JSONArray();
  if(products!=null)for(int i=0;i<Math.min(4,products.length());i++){JSONObject product=products.getJSONObject(i);JSONArray found=images(product);String productId=product.optString("ProductId");if(found.length()<3&&productId.matches("[A-Za-z0-9]{1,32}")){try{JSONObject full=read("https://displaycatalog.mp.microsoft.com/v7.0/products/"+productId+"?market=US&languages=en-US").optJSONObject("Product");if(full!=null)found=images(full);}catch(Exception ignored){}}for(int n=0;n<found.length();n++)all.put(found.getJSONObject(n));}
  synchronized(CACHE){if(CACHE.size()>=24)CACHE.clear();CACHE.put(title,all);}return all;
 }
 private static JSONObject read(String url)throws Exception{return new JSONObject(Net.read(url,"GET",null,null,null,null,4*1024*1024));}
}
