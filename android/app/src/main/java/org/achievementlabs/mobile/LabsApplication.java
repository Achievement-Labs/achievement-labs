package org.achievementlabs.mobile;
import android.app.Application;
public final class LabsApplication extends Application {
 private SessionEngine runtime;
 synchronized SessionEngine peekEngine(){return runtime;}
 synchronized SessionEngine engine(boolean preview,String page){if(runtime==null)runtime=new SessionEngine(this,preview,page);return runtime;}
}

