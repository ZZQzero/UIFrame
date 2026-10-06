package com.zzq.uiframe.media;
import android.app.Service;
import android.content.Intent;
import android.os.IBinder;
import java.util.*;
/** Explicit user transfers on Android 25–33 only; never a scheduling fallback. */
public final class BackupForegroundService extends Service {
    private final Map<String,BackupBridge.Run> runs=new HashMap<>();
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if(intent==null){stopSelf(startId);return START_NOT_STICKY;}
        String identity=intent.getStringExtra("repository");
        try {
            startForeground(BackupBridge.jobId(identity),BackupBridge.notification(this,identity));
            BackupBridge.Run run=BackupBridge.start(this,identity,completed->{
                if(runs.get(identity)==completed)runs.remove(identity);
                ((android.app.NotificationManager)getSystemService(NOTIFICATION_SERVICE)).cancel(BackupBridge.jobId(identity));
                if(runs.isEmpty()){stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();}
                else {String next=runs.keySet().iterator().next();startForeground(BackupBridge.jobId(next),BackupBridge.notification(this,next));}
            });
            if(run==null && !runs.containsKey(identity))throw new IllegalStateException("Native executor did not accept foreground work");
            if(run!=null)runs.put(identity,run);
            try(BackupRepository repository=new BackupRepository(this,identity)){repository.call(BackupRepository.SYSTEM_SCHEDULED,1);}
            BackupBridge.foregroundStarted(identity,null);
        } catch(Exception error){BackupBridge.foregroundStarted(identity,error);if(runs.isEmpty()){stopForeground(STOP_FOREGROUND_REMOVE);stopSelf(startId);}}
        return START_NOT_STICKY;
    }
    @Override public void onTimeout(int startId,int fgsType){for(BackupBridge.Run run:runs.values())run.stop("Foreground transfer time limit reached");stopSelf();}
    @Override public void onDestroy(){for(BackupBridge.Run run:runs.values()){run.stop("Foreground service destroyed");((android.app.NotificationManager)getSystemService(NOTIFICATION_SERVICE)).cancel(BackupBridge.jobId(run.repository));}runs.clear();super.onDestroy();}
    @Override public IBinder onBind(Intent intent){return null;}
}
