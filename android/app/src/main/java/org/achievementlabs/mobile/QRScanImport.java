package org.achievementlabs.mobile;
import android.app.*;
import org.json.*;
/** Shared scanner imports while the 360 editor stays open and retains its private working copy. */
final class QRScanImport {
 static void accept(Activity screen,SessionEngine engine,JSONObject qr){engine.work.execute(()->{try{
  if(engine.queueGate.busy()||engine.spoofGate.busy())throw new Exception("Pause sessions before importing QR codes.");
  if(qr.has("queue")){
   JSONObject transfer=qr.getJSONObject("queue");String owner=engine.account==null?"":engine.account.getString("xuid");QueueTransfer.validate(transfer,owner);
   screen.runOnUiThread(()->{if(screen.isFinishing()||screen.isDestroyed())return;new AlertDialog.Builder(screen).setTitle("Import saved game queues?").setMessage("Matching saved queues will be replaced after Xbox verifies achievements. Nothing starts automatically.").setNegativeButton("Cancel",null).setPositiveButton("Import",(d,w)->engine.work.execute(()->{try{
    if(engine.spoofGate.busy())throw new Exception("Pause spoofing before importing queues.");
    Core.sameOwner(owner,engine.account==null?"":engine.account.getString("xuid"));engine.importQueues(transfer.toString());message(screen,"Saved queues imported.");
   }catch(Exception e){message(screen,engine.safe(e));}})).show();});
  }else{
   engine.verifyTransfer(qr);JSONObject verified=engine.pending;
   boolean eventOnly=verified.optBoolean("eventOnly"),signInOnly=verified.optBoolean("signInOnly");
   screen.runOnUiThread(()->{
    if(screen.isFinishing()||screen.isDestroyed())return;
    AlertDialog.Builder dialog=new AlertDialog.Builder(screen).setTitle("Verified "+verified.optString("gamertag")).setMessage(eventOnly?"Import this account's event token?":"Connect this account from your PC?").setNegativeButton("Cancel",(d,w)->engine.work.execute(()->{if(engine.pending==verified)engine.pending=null;}));
    dialog.setPositiveButton(eventOnly?"Import event token":"Connect",(d,w)->confirm(screen,engine,verified,eventOnly));
    if(!eventOnly&&!signInOnly&&engine.account!=null&&engine.account.optString("xuid").equals(verified.optString("xuid")))dialog.setNeutralButton("Event token only",(d,w)->confirm(screen,engine,verified,true));
    dialog.show();
   });
  }
 }catch(Exception e){message(screen,engine.safe(e));}});}
 static void confirm(Activity screen,SessionEngine engine,JSONObject verified,boolean only){engine.work.execute(()->{try{if(engine.pending!=verified)throw new Exception("Scan this QR code again.");engine.handle("confirmPC",new JSONObject().put("eventOnly",only));message(screen,"QR import complete.");}catch(Exception e){message(screen,engine.safe(e));}});}
 static void message(Activity screen,String text){screen.runOnUiThread(()->{if(!screen.isFinishing()&&!screen.isDestroyed())new AlertDialog.Builder(screen).setMessage(text).setPositiveButton("Close",null).show();});}
}
