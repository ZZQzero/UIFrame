package com.zzq.uiframe.media;

import android.app.job.JobParameters;
import android.app.job.JobService;

public final class BackupJobService extends JobService {
    private BackupBridge.Run run;
    @Override public boolean onStartJob(JobParameters parameters) {
        run=BackupBridge.start(this,parameters); return run!=null;
    }
    @Override public boolean onStopJob(JobParameters parameters) {
        BackupBridge.stop(run); return true; // OS interruption; no application-error retry.
    }
}
