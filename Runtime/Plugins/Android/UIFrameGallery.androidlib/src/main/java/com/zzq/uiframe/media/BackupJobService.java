package com.zzq.uiframe.media;
import android.app.job.JobParameters;
import android.app.job.JobService;
import java.util.HashMap;
import java.util.Map;
public final class BackupJobService extends JobService {
    private final Map<Integer,BackupBridge.Run> runs=new HashMap<>();
    @Override public boolean onStartJob(JobParameters parameters) {
        BackupBridge.Run run=BackupBridge.start(this,parameters);if(run==null)return false;
        runs.put(parameters.getJobId(),run);return true;
    }
    void complete(JobParameters parameters,BackupBridge.Run run,boolean reschedule) {
        java.util.concurrent.CountDownLatch finished=new java.util.concurrent.CountDownLatch(1);
        new android.os.Handler(android.os.Looper.getMainLooper()).post(()->{
            if(runs.get(parameters.getJobId())==run)runs.remove(parameters.getJobId());
            try { jobFinished(parameters,reschedule); } finally { finished.countDown(); }
        });
        try { finished.await(); } catch(InterruptedException error) { Thread.currentThread().interrupt(); throw new IllegalStateException("Interrupted while releasing Android job",error); }
    }
    @Override public boolean onStopJob(JobParameters parameters) {return BackupBridge.stop(runs.remove(parameters.getJobId()));}
    @Override public void onDestroy(){for(BackupBridge.Run run:runs.values())BackupBridge.stop(run);runs.clear();super.onDestroy();}
}
