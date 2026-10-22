package org.achievementlabs.mobile;
import android.app.*;import android.content.*;import android.content.pm.PackageManager;import android.content.pm.ServiceInfo;import android.os.*;
import java.util.concurrent.*;
public final class SessionService extends Service {
 static volatile SessionService active;static volatile CountDownLatch starting;static volatile String failure;
 static final int ID=9001;static final long LIMIT=6*60*60*1000L;
 SessionEngine engine;PowerManager.WakeLock wake;final Handler main=new Handler(Looper.getMainLooper());long deadline;boolean ending;
 static synchronized void ensure(SessionEngine e)throws Exception{
  if(!e.foreground)throw new Exception("Start sessions while the app is open.");
  if(Build.VERSION.SDK_INT>=33&&e.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED){e.requestNotifications();throw new Exception("Allow notifications, then press Start again to show background status.");}
  if(active!=null&&e.backgroundReady)return;
  starting=new CountDownLatch(1);failure=null;e.startForegroundService(new Intent(e,SessionService.class));
  if(!starting.await(8,TimeUnit.SECONDS)||!e.backgroundReady)throw new Exception(failure==null?"Could not start background session service.":failure);
 }
 static void refresh(SessionEngine e){e.main.post(()->{SessionService service=active;if(service==null||service.ending)return;if(!e.queueRunning&&!e.spoofRunning&&!e.queueGate.busy()&&!e.spoofGate.busy())service.stopSelf();else service.update();});}
 @Override public void onCreate(){super.onCreate();engine=((LabsApplication)getApplication()).engine(false,"home");active=this;deadline=SystemClock.elapsedRealtime()+LIMIT;
  try{getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel("sessions","Active sessions",NotificationManager.IMPORTANCE_LOW));
   if(Build.VERSION.SDK_INT>=29)startForeground(ID,notification(),ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);else startForeground(ID,notification());
   PowerManager power=getSystemService(PowerManager.class);wake=power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"AchievementLabs:Sessions");wake.setReferenceCounted(false);wake.acquire(LIMIT+10000);engine.backgroundReady=true;main.postDelayed(tick,15000);
  }catch(Exception e){failure="Android could not start a visible background session.";stopSelf();}finally{if(starting!=null)starting.countDown();}
 }
 @Override public int onStartCommand(Intent intent,int flags,int id){if(intent!=null&&"stopAll".equals(intent.getAction()))stopSessions("Sessions stopped from the notification.");else if(intent!=null&&"pauseQueue".equals(intent.getAction())){engine.pause();refresh(engine);}return START_NOT_STICKY;}
 Notification notification(){
  String queue=engine.queueRunning?"Auto Unlock · "+engine.queue.length()+" queued" : engine.queueGate.finishing()?"Finishing current request":"Auto Unlock not running";
  String spoof=engine.spoofRunning?"Spoofing "+engine.spoofTitle:"Spoofing stopped";
  PendingIntent open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP|Intent.FLAG_ACTIVITY_CLEAR_TOP),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
  PendingIntent stop=PendingIntent.getService(this,1,new Intent(this,SessionService.class).setAction("stopAll"),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
  PendingIntent pause=PendingIntent.getService(this,2,new Intent(this,SessionService.class).setAction("pauseQueue"),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
  String next=engine.queueRunning?engine.nextQueueGame+" · "+engine.nextQueueName+"\n"+(engine.nextAt>0?"Next action "+android.text.format.DateFormat.format("HH:mm:ss",engine.nextAt):"Checking / submitting…"):"";
  Notification.Builder b=new Notification.Builder(this,"sessions").setSmallIcon(android.R.drawable.stat_notify_sync).setContentTitle(engine.preview?"Achievement Labs · offline background test":"Achievement Labs is running").setContentText(queue+" · "+spoof).setStyle(new Notification.BigTextStyle().bigText(queue+"\n"+spoof+"\n"+next)).setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true).setCategory(Notification.CATEGORY_SERVICE).setVisibility(Notification.VISIBILITY_PRIVATE).addAction(new Notification.Action.Builder(null,"Stop all",stop).build());
  if(engine.queueRunning&&engine.nextAt>System.currentTimeMillis())b.setWhen(engine.nextAt).setUsesChronometer(true).setChronometerCountDown(true);
  else if(engine.spoofRunning&&engine.spoofStarted>0)b.setWhen(engine.spoofStarted).setUsesChronometer(true);
  if(engine.queueRunning)b.addAction(new Notification.Action.Builder(null,"Pause queue",pause).build());return b.build();
 }
 void update(){try{getSystemService(NotificationManager.class).notify(ID,notification());}catch(SecurityException e){stopSessions("Notification permission was removed; sessions paused.");}}
 final Runnable tick=new Runnable(){public void run(){if(ending)return;if(SystemClock.elapsedRealtime()>=deadline){stopSessions("Background session reached the six-hour limit. Open the app to review and resume.");return;}if(!engine.queueRunning&&!engine.spoofRunning&&!engine.queueGate.busy()&&!engine.spoofGate.busy()){stopSelf();return;}update();main.postDelayed(this,15000);}};
 void stopSessions(String reason){if(ending)return;ending=true;engine.backgroundReady=false;engine.pause();engine.stopSpoof();engine.notice=reason;engine.notifyUser("Achievement Labs paused",reason);engine.push();stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();}
 @Override public void onTimeout(int startId,int type){stopSessions("Android's background time limit was reached. Open the app to review and resume.");}
 @Override public void onTaskRemoved(Intent root){/* Explicit foreground sessions continue when the task is dismissed. */}
 @Override public void onDestroy(){ending=true;main.removeCallbacksAndMessages(null);engine.backgroundReady=false;if(engine.queueRunning||engine.spoofRunning){engine.pause();engine.stopSpoof();}if(wake!=null&&wake.isHeld())wake.release();if(active==this)active=null;SessionWidget.refresh(this);super.onDestroy();}
 @Override public IBinder onBind(Intent i){return null;}
}

