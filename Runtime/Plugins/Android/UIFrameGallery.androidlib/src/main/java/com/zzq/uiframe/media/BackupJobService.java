package com.zzq.uiframe.media;
import android.app.job.*;
import android.os.Build;
import java.util.*;
public final class BackupJobService extends JobService {
    private final Map<Integer,BackupBridge.Run> runs=new HashMap<>();
    @Override public boolean onStartJob(JobParameters parameters) {
        String identity=parameters.getExtras().getString("repository");
        if(Build.VERSION.SDK_INT>=34 && parameters.getExtras().getBoolean("user"))
            setNotification(parameters,BackupBridge.jobId(identity),BackupBridge.notification(this,identity),JOB_END_NOTIFICATION_POLICY_REMOVE);
        BackupBridge.Run run=BackupBridge.start(this,identity,completed->{
            if(runs.get(parameters.getJobId())==completed){runs.remove(parameters.getJobId());jobFinished(parameters,false);}
        });
        if(run==null)return false;
        runs.put(parameters.getJobId(),run);return true;
    }
    @Override public boolean onStopJob(JobParameters parameters) {
        BackupBridge.Run run=runs.remove(parameters.getJobId());
        if(run!=null)run.stop("Android stopped job (reason "+(Build.VERSION.SDK_INT>=31?parameters.getStopReason():-1)+")");
        return false;
    }
    @Override public void onDestroy(){for(BackupBridge.Run run:runs.values())run.stop("Job service destroyed");runs.clear();super.onDestroy();}
}
