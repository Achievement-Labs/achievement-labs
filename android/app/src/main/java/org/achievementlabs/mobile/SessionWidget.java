package org.achievementlabs.mobile;
import android.app.*;import android.appwidget.*;import android.content.*;import android.os.*;import android.view.View;import android.widget.RemoteViews;

/** Display-only widget: starting/resuming always opens the app for review. */
public final class SessionWidget extends AppWidgetProvider {
 static final String PAUSE="org.achievementlabs.mobile.widget.PAUSE",STOP="org.achievementlabs.mobile.widget.STOP",REFRESH="org.achievementlabs.mobile.widget.REFRESH";
 @Override public void onUpdate(Context context,AppWidgetManager manager,int[] ids){refresh(context);}
 @Override public void onReceive(Context context,Intent intent){
  super.onReceive(context,intent);String action=intent.getAction();
  if(REFRESH.equals(action)){refresh(context);return;}
  if(!PAUSE.equals(action)&&!STOP.equals(action))return;
  SessionEngine engine=((LabsApplication)context.getApplicationContext()).peekEngine();
  if(engine==null){refresh(context);return;}
  PendingResult pending=goAsync();engine.work.execute(()->{try{if(PAUSE.equals(action))engine.pause();else engine.stopSpoof();engine.push();}finally{pending.finish();}});
 }
 static PendingIntent action(Context context,String action,int code){return PendingIntent.getBroadcast(context,code,new Intent(context,SessionWidget.class).setAction(action),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);}
 static void pin(MainActivity ui){AppWidgetManager manager=AppWidgetManager.getInstance(ui);if(manager.isRequestPinAppWidgetSupported())manager.requestPinAppWidget(new ComponentName(ui,SessionWidget.class),null,null);else new AlertDialog.Builder(ui).setMessage("Long-press an empty area of your home screen, choose Widgets, then Achievement Labs.").setPositiveButton("OK",null).show();}
 static void refresh(Context context){
  Context app=context.getApplicationContext();new Handler(Looper.getMainLooper()).post(()->render(app));
 }
 static void render(Context context){
  AppWidgetManager manager=AppWidgetManager.getInstance(context);int[] ids=manager.getAppWidgetIds(new ComponentName(context,SessionWidget.class));if(ids.length==0)return;
  SessionEngine engine=((LabsApplication)context.getApplicationContext()).peekEngine();
  boolean spoof=engine!=null&&engine.spoofRunning,queue=engine!=null&&engine.queueRunning;
  long now=System.currentTimeMillis(),uptime=SystemClock.elapsedRealtime(),next=queue?engine.nextAt:0;
  RemoteViews views=views(context,engine);
  manager.updateAppWidget(ids,views);
  // A deferred display check does not start the runtime or contact Xbox after process death.
  long check=next>now?next+120000:spoof?now+600000:0;
  AlarmManager alarms=context.getSystemService(AlarmManager.class);PendingIntent refresh=action(context,REFRESH,53);
  if(check>0)alarms.set(AlarmManager.ELAPSED_REALTIME,uptime+Math.max(1000,check-now),refresh);else alarms.cancel(refresh);
 }
 static RemoteViews views(Context context,SessionEngine engine){
  boolean spoof=engine!=null&&engine.spoofRunning,queue=engine!=null&&engine.queueRunning;
  long now=System.currentTimeMillis(),uptime=SystemClock.elapsedRealtime(),next=queue?engine.nextAt:0;
  RemoteViews views=new RemoteViews(context.getPackageName(),R.layout.session_widget);
  views.setTextViewText(R.id.widget_spoof_title,spoof?"Spoofing · "+engine.spoofTitle:"Spoofing stopped");
  views.setChronometer(R.id.widget_spoof_timer,uptime-(spoof&&engine.spoofStarted>0?Math.max(0,now-engine.spoofStarted):0),null,spoof&&engine.spoofStarted>0);
  views.setViewVisibility(R.id.widget_spoof_timer,spoof?View.VISIBLE:View.GONE);
  views.setTextViewText(R.id.widget_queue_title,queue?"Auto Unlock · "+engine.nextQueueGame:"Auto Unlock paused");
  views.setTextViewText(R.id.widget_next_name,queue?engine.nextQueueName+(next>now?"":" · checking / submitting"):"Open the app to review and resume");
  views.setChronometerCountDown(R.id.widget_unlock_timer,true);views.setChronometer(R.id.widget_unlock_timer,uptime+Math.max(0,next-now),null,queue&&next>now);
  views.setViewVisibility(R.id.widget_unlock_timer,queue&&next>now?View.VISIBLE:View.GONE);
  views.setTextViewText(R.id.widget_updated,"Updated "+android.text.format.DateFormat.format("HH:mm:ss",now)+" · tap app to confirm status");
  Intent open=new Intent(context,MainActivity.class).putExtra("widgetPage","queue").addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP|Intent.FLAG_ACTIVITY_CLEAR_TOP);
  PendingIntent show=PendingIntent.getActivity(context,50,open,PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
  views.setOnClickPendingIntent(R.id.widget_root,show);views.setOnClickPendingIntent(R.id.widget_open,show);views.setOnClickPendingIntent(R.id.widget_pause,action(context,PAUSE,51));views.setOnClickPendingIntent(R.id.widget_stop,action(context,STOP,52));
  views.setBoolean(R.id.widget_pause,"setEnabled",queue);views.setBoolean(R.id.widget_stop,"setEnabled",spoof);
  return views;
 }
 static void selfTest(MainActivity ui,SessionEngine engine){
  if(!BuildConfig.DEBUG||!engine.preview)return;
  try{
   long now=System.currentTimeMillis();engine.queueRunning=true;engine.spoofRunning=true;engine.spoofTitle="7";engine.spoofStarted=now-90000;engine.nextAt=now+120000;engine.nextQueueGame="Offline game";engine.nextQueueName="Second achievement";
   android.view.View root=views(ui,engine).apply(ui,new android.widget.FrameLayout(ui));
   android.widget.Chronometer spoof=root.findViewById(R.id.widget_spoof_timer),unlock=root.findViewById(R.id.widget_unlock_timer);
   if(spoof.getBase()>=SystemClock.elapsedRealtime()||unlock.getBase()<=SystemClock.elapsedRealtime()||!unlock.isCountDown())throw new Exception("Widget timer direction/base invalid.");
   if(!((android.widget.TextView)root.findViewById(R.id.widget_next_name)).getText().toString().contains("Second achievement"))throw new Exception("Widget next achievement missing.");
   engine.nextQueueName="Third achievement";engine.nextAt=now+180000;
   android.view.View advanced=views(ui,engine).apply(ui,new android.widget.FrameLayout(ui));
   if(((android.widget.Chronometer)advanced.findViewById(R.id.widget_unlock_timer)).getBase()<=unlock.getBase())throw new Exception("Next achievement countdown did not advance.");
   engine.queueRunning=false;engine.spoofRunning=false;engine.nextAt=0;engine.spoofStarted=0;
   android.view.View paused=views(ui,engine).apply(ui,new android.widget.FrameLayout(ui));
   if(paused.findViewById(R.id.widget_unlock_timer).getVisibility()!=View.GONE||paused.findViewById(R.id.widget_spoof_timer).getVisibility()!=View.GONE)throw new Exception("Paused widget kept timers running.");
   try(java.io.FileOutputStream out=ui.openFileOutput("widget-self-test.json",Context.MODE_PRIVATE)){out.write(Core.utf("{\"passed\":true,\"network\":false,\"timerTransitions\":true}"));}
   android.widget.FrameLayout host=new android.widget.FrameLayout(ui);host.setBackgroundColor(0xff000000);android.widget.FrameLayout.LayoutParams params=new android.widget.FrameLayout.LayoutParams(-1,(int)(230*ui.getResources().getDisplayMetrics().density),android.view.Gravity.CENTER);params.setMargins(12,0,12,0);host.addView(root,params);ui.setContentView(host);
  }catch(Exception error){engine.notice=engine.safe(error);try(java.io.FileOutputStream out=ui.openFileOutput("widget-self-test.json",Context.MODE_PRIVATE)){out.write(Core.utf("{\"passed\":false}"));}catch(Exception ignored){}}
 }
}
