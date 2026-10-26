package org.achievementlabs.mobile;
import android.app.*;import android.content.*;import android.content.pm.*;import android.net.Uri;import android.os.*;import android.provider.Settings;import androidx.core.content.FileProvider;
import org.json.*;import java.io.*;import java.util.*;import java.util.concurrent.*;
final class Updater {
 static final String REPOSITORY="https://github.com/thevampirebat/achievement-labs";
 static final String RELEASES_API="https://api.github.com/repos/thevampirebat/achievement-labs/releases?per_page=100";
 static volatile JSONObject candidate;static volatile boolean downloading,ready;static volatile String status="";
 static final ExecutorService worker=Executors.newSingleThreadExecutor();
 static JSONObject snapshot()throws Exception{return new JSONObject().put("downloading",downloading).put("ready",ready).put("status",status);}
 static JSONObject choose(JSONArray releases)throws Exception{
  JSONObject best=null;
  for(int i=0;i<releases.length();i++){JSONObject release=releases.getJSONObject(i);if(release.optBoolean("draft"))continue;JSONArray assets=release.optJSONArray("assets");if(assets==null)continue;
   for(int z=0;z<assets.length();z++){JSONObject asset=assets.getJSONObject(z);String name=asset.optString("name"),prefix="AchievementLabs-Android-";if(!name.matches("AchievementLabs-Android-\\d+\\.\\d+\\.\\d+\\.apk"))continue;String version=name.substring(prefix.length(),name.length()-4);String page=release.optString("html_url"),url=asset.optString("browser_download_url"),digest=asset.optString("digest");long size=asset.optLong("size");
    if(!page.startsWith(REPOSITORY+"/releases/tag/")||!url.startsWith(REPOSITORY+"/releases/download/")||!url.endsWith("/"+name)||size<1||size>128*1024*1024)continue;
    if(best==null||Core.newer(version,best.getString("version")))best=new JSONObject().put("version",version).put("url",page).put("download",url).put("sha256",digest.matches("sha256:[0-9a-fA-F]{64}")?digest.substring(7).toLowerCase(Locale.ROOT):"").put("size",size);
   }
  }return best;
 }
 static JSONObject check(SessionEngine e)throws Exception{
  JSONArray releases=new JSONArray(Net.read(RELEASES_API,"GET",null,null,null,null,4*1024*1024));
  candidate=choose(releases);boolean newer=candidate!=null&&Core.newer(candidate.getString("version"),BuildConfig.VERSION_NAME);
  e.notice=candidate==null?"No published Android release yet; Windows and iOS releases ignored.":newer?"Android "+candidate.getString("version")+" is available.":"Android app is up to date.";
  return new JSONObject().put("message",e.notice).put("url",newer?candidate.getString("url"):"").put("canInstall",newer&&!candidate.getString("sha256").isEmpty());
 }
 static JSONObject download(SessionEngine e)throws Exception{
  if(downloading)throw new Exception("An update is already downloading.");if(!e.foreground)throw new Exception("Start the update from the open app.");
  if(e.queueGate.busy()||e.spoofGate.busy())throw new Exception("Pause sessions before updating the app.");
  if(candidate==null||!Core.newer(candidate.getString("version"),BuildConfig.VERSION_NAME))throw new Exception("Check for updates first.");
  final JSONObject item=new JSONObject(candidate.toString());if(item.getString("sha256").isEmpty())throw new Exception("This release has no SHA-256 digest. Open its release page for a manual download.");
  downloading=true;ready=false;status="Downloading Android "+item.getString("version")+"…";
  worker.execute(()->{DownloadManager dm=e.getSystemService(DownloadManager.class);long id=-1;File part=null;try{
   File directory=new File(e.getFilesDir(),"updates");if(!directory.exists()&&!directory.mkdirs())throw new Exception("Could not prepare update storage.");part=new File(directory,"download.part");
   DownloadManager.Request request=new DownloadManager.Request(Uri.parse(item.getString("download"))).setTitle("Achievement Labs update").setDescription("Android "+item.getString("version")).setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED).setAllowedOverMetered(true);
   id=dm.enqueue(request);long deadline=SystemClock.elapsedRealtime()+10*60*1000;boolean complete=false;
   while(SystemClock.elapsedRealtime()<deadline){try(android.database.Cursor cursor=dm.query(new DownloadManager.Query().setFilterById(id))){if(cursor==null||!cursor.moveToFirst())throw new Exception("Update download was removed.");int result=cursor.getInt(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS));if(result==DownloadManager.STATUS_FAILED)throw new Exception("Android could not download the update.");if(result==DownloadManager.STATUS_SUCCESSFUL){complete=true;break;}}
    Thread.sleep(1000);
   }if(!complete)throw new Exception("Update download timed out.");
   try(ParcelFileDescriptor descriptor=dm.openDownloadedFile(id);InputStream input=new FileInputStream(descriptor.getFileDescriptor());OutputStream output=new FileOutputStream(part)){byte[] bytes=new byte[32768];int count;long total=0;while((count=input.read(bytes))!=-1){total+=count;if(total>item.getLong("size")||total>128*1024*1024)throw new Exception("Update size does not match the release.");output.write(bytes,0,count);}if(total!=item.getLong("size"))throw new Exception("Incomplete update download.");}
   java.security.MessageDigest hash=java.security.MessageDigest.getInstance("SHA-256");try(InputStream input=new FileInputStream(part)){byte[] bytes=new byte[32768];int count;while((count=input.read(bytes))!=-1)hash.update(bytes,0,count);}
   StringBuilder hex=new StringBuilder();for(byte b:hash.digest())hex.append(String.format(Locale.ROOT,"%02x",b&255));if(!hex.toString().equals(item.getString("sha256")))throw new Exception("Update checksum did not match. The APK was rejected.");
   verify(e,part,item.getString("version"));File apk=new File(directory,"AchievementLabs.apk");if(apk.exists()&&!apk.delete())throw new Exception("Could not replace the previous download.");if(!part.renameTo(apk))throw new Exception("Could not finish saving the update.");
   ready=true;status="Verified update ready. Tap Install downloaded update in Settings → App.";e.notifyUser("Achievement Labs update ready",status);
  }catch(Exception ex){status=e.safe(ex);ready=false;if(part!=null)part.delete();e.notifyUser("Update not installed",status);}finally{if(id!=-1)dm.remove(id);downloading=false;e.notice=status;try{e.emit("external",e.publicState(true),null);}catch(Exception ignored){}}});
  return new JSONObject().put("message",status);
 }
 @SuppressWarnings("deprecation") static void verify(Context context,File apk,String version)throws Exception{
  PackageManager pm=context.getPackageManager();int flags=Build.VERSION.SDK_INT>=28?PackageManager.GET_SIGNING_CERTIFICATES:PackageManager.GET_SIGNATURES;
  PackageInfo installed=pm.getPackageInfo(context.getPackageName(),flags),downloaded=pm.getPackageArchiveInfo(apk.getAbsolutePath(),flags);
  if(downloaded==null||!context.getPackageName().equals(downloaded.packageName)||!version.equals(downloaded.versionName))throw new Exception("The downloaded APK is not the expected app version.");
  long current=Build.VERSION.SDK_INT>=28?installed.getLongVersionCode():installed.versionCode,next=Build.VERSION.SDK_INT>=28?downloaded.getLongVersionCode():downloaded.versionCode;
  if(next<=current)throw new Exception("The downloaded APK is not a newer build.");
  android.content.pm.Signature[] a=Build.VERSION.SDK_INT>=28?installed.signingInfo.getApkContentsSigners():installed.signatures,b=Build.VERSION.SDK_INT>=28?downloaded.signingInfo.getApkContentsSigners():downloaded.signatures;
  Set<String> own=new HashSet<>(),other=new HashSet<>();if(a!=null)for(android.content.pm.Signature s:a)own.add(Core.hash(s.toByteArray()));if(b!=null)for(android.content.pm.Signature s:b)other.add(Core.hash(s.toByteArray()));
  if(own.isEmpty()||!own.equals(other))throw new Exception("Update signing key differs from this installation. A matching permanent release key is required; existing data was retained.");
 }
 static void install(MainActivity activity){try{
  File apk=new File(activity.getFilesDir(),"updates/AchievementLabs.apk");if(!ready||!apk.isFile())throw new Exception("No verified update is ready. Check for updates first.");
  // Recheck the package and signer immediately before handing the APK to Android.
  PackageInfo info=activity.getPackageManager().getPackageArchiveInfo(apk.getAbsolutePath(),0);if(info==null)throw new Exception("Update file cannot be read.");verify(activity,apk,info.versionName);
  if(!activity.getPackageManager().canRequestPackageInstalls()){new AlertDialog.Builder(activity).setMessage("Allow Achievement Labs to install updates, then return and tap Install downloaded update again.").setPositiveButton("Open Android settings",(d,w)->activity.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+activity.getPackageName())))).setNegativeButton("Cancel",null).show();return;}
  Uri uri=FileProvider.getUriForFile(activity,activity.getPackageName()+".updates",apk);activity.startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(uri,"application/vnd.android.package-archive").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION));
 }catch(Exception ex){new AlertDialog.Builder(activity).setMessage(activity.engine.safe(ex)).setPositiveButton("OK",null).show();}}
}
