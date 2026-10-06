"""Run Android's real Java scheduling/binding and JNI transport on macOS.

Only the JobScheduler API and Android Context are host doubles. Repository state
and the binding code are production code; this does not replace device testing.
"""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile

PACKAGE = Path(__file__).resolve().parents[2]
NATIVE = PACKAGE / 'Runtime/MediaBackup/Native~'
sys.path.insert(0, str(NATIVE / 'tests'))
from process_windows import Repository

SOURCES = {
    'android/os/Looper.java': '''
package android.os;
public class Looper {static final Looper MAIN=new Looper();public static Looper getMainLooper(){return MAIN;}public static Looper myLooper(){return null;}}
''',
    'android/os/Handler.java': '''
package android.os;
public class Handler {public Handler(Looper looper){}public boolean post(Runnable r){r.run();return true;}}
''',
    'android/os/Build.java': '''
package android.os;
public class Build {public static class VERSION {public static int SDK_INT=34;}}
''',
    'android/util/Log.java': '''
package android.util;
public class Log {
    public static int errors;
    public static int e(String tag,String message){errors++;return 0;}
    public static int e(String tag,String message,Throwable error){errors++;return 0;}
}''',
    'android/content/Context.java': '''
package android.content;
public class Context {
    public static final String JOB_SCHEDULER_SERVICE="jobs",CONNECTIVITY_SERVICE="network",NOTIFICATION_SERVICE="notifications";
    private final java.io.File root;
    public final android.app.job.JobScheduler jobs=new android.app.job.JobScheduler();
    public Context(java.io.File root){this.root=root;}
    public Context getApplicationContext(){return this;}
    public java.io.File getNoBackupFilesDir(){return root;}
    public Object getSystemService(String name){return jobs;}
    public String getPackageName(){return "fixture";}
    public android.content.pm.PackageManager getPackageManager(){throw new UnsupportedOperationException();}
    public ComponentName startService(Intent intent){throw new UnsupportedOperationException();}
    public ComponentName startForegroundService(Intent intent){throw new UnsupportedOperationException();}
}''',
    'android/content/ComponentName.java': '''
package android.content;
public class ComponentName { public ComponentName(Context context,Class<?> type){} }''',
    'android/os/PersistableBundle.java': '''
package android.os;
public class PersistableBundle {
    private final java.util.Map<String,String> values=new java.util.HashMap<>();
    public void putString(String key,String value){values.put(key,value);}
    public String getString(String key){return values.get(key);}
    public void putBoolean(String key,boolean value){values.put(key,Boolean.toString(value));}
    public boolean getBoolean(String key){return Boolean.parseBoolean(values.get(key));}
}''',
    'android/app/job/JobInfo.java': '''
package android.app.job;
public class JobInfo {
    public static final int NETWORK_TYPE_ANY=1,NETWORK_TYPE_UNMETERED=2;
    public int id;public boolean user;public long latency;public android.os.PersistableBundle extras;
    public android.os.PersistableBundle getExtras(){return extras;}
    public static class Builder {
        private final JobInfo value=new JobInfo();
        public Builder(int id,android.content.ComponentName component){value.id=id;}
        public Builder setExtras(android.os.PersistableBundle extras){value.extras=extras;return this;}
        public Builder setRequiredNetworkType(int type){return this;}
        public Builder setPersisted(boolean persisted){return this;}
        public Builder setUserInitiated(boolean user){value.user=user;return this;}
        public Builder setEstimatedNetworkBytes(long down,long up){return this;}
        public Builder setMinimumLatency(long value){this.value.latency=value;return this;}
        public JobInfo build(){return value;}
    }
}''',
    'android/app/job/JobScheduler.java': '''
package android.app.job;
public class JobScheduler {
    public static final int RESULT_SUCCESS=1;
    public int submissions;public boolean reject;
    private final java.util.Map<Integer,JobInfo> jobs=new java.util.HashMap<>();
    public JobInfo getPendingJob(int id){return jobs.get(id);}
    public int schedule(JobInfo job){submissions++;if(reject)return 0;jobs.put(job.id,job);return RESULT_SUCCESS;}
    public void cancel(int id){jobs.remove(id);}
}''',
    'com/zzq/uiframe/media/BindingTest.java': '''
package com.zzq.uiframe.media;
import java.io.*;
import android.content.Context;
public class BindingTest {
    static void check(boolean value,String message){if(!value)throw new AssertionError(message);}
    public static void main(String[] args) throws Exception {
        System.load(args[1]);Context context=new Context(new File(args[0]));String hash="b".repeat(64);
        for(int mode=0;mode<2;mode++) {
            String identity=(mode==0?"a":"b").repeat(64);
            try(BackupRepository repository=new BackupRepository(context,identity)) {
                repository.call(58,"aa");
                repository.call(2,"bb","aa",1L,1L,"cc","asset:photo","v1","photo.jpg","image/jpeg","",0L);
        repository.call(77,"cc","aa",4L,4L,1024L,2L);
                try(FileOutputStream output=new FileOutputStream(new File(BackupRepository.root(context),identity+"/payloads/cc.payload"))){output.write(new byte[]{1,2,3,4});}
                repository.call(3,"cc","aa",4L,hash,"image/jpeg",1024L,2L);
                repository.call(96,"cc","aa",3L);repository.call(9,"cc",1L,0L,4L);
                repository.call(BackupRepository.HANDOFF,"cc",1L,"protected-reference");
                repository.call(99,false,mode);
            }
            try(BackupRepository repository=new BackupRepository(context,identity)) {
                java.lang.reflect.Method schedule=BackupBridge.class.getDeclaredMethod("schedule",Context.class,BackupRepository.class,long.class);
                schedule.setAccessible(true);int before=context.jobs.submissions;
                repository.call(15,1L);schedule.invoke(null,context,repository,0L);
                check(context.jobs.submissions==before,"Paused repository was scheduled");
                repository.call(15,0L);context.jobs.reject=true;
                try{schedule.invoke(null,context,repository,0L);throw new AssertionError("Rejected scheduling reported success");}
                catch(java.lang.reflect.InvocationTargetException expected){check(expected.getCause() instanceof IOException,"Lost scheduling failure");}
                check(!repository.call(7,"cc").get(0).flag("system_scheduled"),"Rejected schedule became successful");
                context.jobs.reject=false;schedule.invoke(null,context,repository,0L);
                check(repository.call(7,"cc").get(0).flag("system_scheduled"),"Accepted job was not recorded");
                check(context.jobs.getPendingJob(BackupBridge.jobId(identity)).user==(mode==1),"Wrong user/automatic scheduler");
                before=context.jobs.submissions;schedule.invoke(null,context,repository,0L);
                check(context.jobs.submissions==before,"Wake replaced a pending job");
                repository.call(58,"aa");
                repository.call(2,"ee","aa",10L,1L,"ff","asset:later","v1","later.jpg","image/jpeg","",0L);
                repository.call(77,"ff","aa",4L,4L,1024L,11L);
                try(FileOutputStream output=new FileOutputStream(new File(BackupRepository.root(context),identity+"/payloads/ff.payload"))){output.write(new byte[]{1,2,3,4});}
                repository.call(3,"ff","aa",4L,hash,"image/jpeg",1024L,11L);repository.call(96,"ff","aa",12L);
                repository.call(9,"ff",1L,0L,13L);repository.call(BackupRepository.HANDOFF,"ff",1L,"protected-reference");
                java.lang.reflect.Field field=BackupBridge.class.getDeclaredField("active");field.setAccessible(true);
                @SuppressWarnings("unchecked") java.util.Map<String,BackupBridge.Run> active=(java.util.Map<String,BackupBridge.Run>)field.get(null);
                BackupBridge.Run run=new BackupBridge.Run(identity);active.put(identity,run);
                try {
                    run.stopped=true;
                    try{schedule.invoke(null,context,repository,0L);throw new AssertionError("Stopping executor accepted new work");}
                    catch(java.lang.reflect.InvocationTargetException expected){check(expected.getCause() instanceof IOException,"Lost admission failure");}
                    check(!repository.call(7,"ff").get(0).flag("system_scheduled"),"Stopped run fabricated admission");
                    run.stopped=false;schedule.invoke(null,context,repository,0L);
                    check(repository.call(7,"ff").get(0).flag("system_scheduled"),"Active executor did not record later admission");
                    check(run.signaled,"Active executor lost the scheduling wake");
                    check(context.jobs.submissions==before,"Active admission replaced the OS job");
                } finally {active.remove(identity);run.transfers.shutdown();}
                repository.call(15,true);
                java.lang.reflect.Method settle=BackupBridge.class.getDeclaredMethod("settle",BackupRepository.class,BackupBridge.Run.class);
                settle.setAccessible(true);settle.invoke(null,repository,null);
                BackupRepository.Row task=repository.call(7,"cc").get(0);
                check(task.number("state")==4 && task.flag("credential_released"),"Idle pause retained credentials");
            }
        }
        String identity="c".repeat(64);
        try(BackupRepository repository=new BackupRepository(context,identity)) {
            repository.call(58,"aa");
            repository.call(2,"bb","aa",1L,2L,"cc","asset:first","v1","first.jpg","image/jpeg",
                "dd","asset:second","v1","second.jpg","image/jpeg","",0L);
            File broken=new File(BackupRepository.root(context),identity+"/payloads/cc.payload");
            File normal=new File(BackupRepository.root(context),identity+"/payloads/dd.payload");
            repository.call(77,"cc","aa",4L,4L,1024L,2L);
            check(broken.mkdir(),"Could not create failing cleanup fixture");
            repository.call(97,"cc","aa","fixture preparation failed",2L);
            repository.call(77,"dd","aa",4L,4L,1024L,2L);
            try(FileOutputStream output=new FileOutputStream(normal)){output.write(new byte[]{1,2,3,4});}
            repository.call(97,"dd","aa","fixture preparation failed",2L);
            java.lang.reflect.Method cleanup=BackupBridge.class.getDeclaredMethod("cleanup",BackupRepository.class);
            cleanup.setAccessible(true);cleanup.invoke(null,repository);
            check(android.util.Log.errors==1,"Isolated failure was not reported once");
            check(broken.exists() && !normal.exists(),"Failed file stopped independent cleanup");
            check(repository.call(BackupRepository.CLEANUP_PAGE,"",100L).isEmpty(),"Failed file remained automatically eligible");
            cleanup.invoke(null,repository);check(android.util.Log.errors==1,"Cleanup automatically retried a failed file");
            repository.call(15,1L);repository.call(15,0L);
            BackupRepository.Row retry=repository.call(21,"cc",3L).get(0);
            check("cc".equals(retry.text("id")),"Retry returned a different file");
            check(broken.delete(),"Could not repair failing fixture");
            repository.call(BackupRepository.CLEANUP_RUN,retry.text("id"),retry.number("updated_utc"),4L);
        }
        java.lang.reflect.Field activeField=BackupBridge.class.getDeclaredField("active");activeField.setAccessible(true);
        @SuppressWarnings("unchecked") java.util.Map<String,BackupBridge.Run> active=(java.util.Map<String,BackupBridge.Run>)activeField.get(null);
        identity="d".repeat(64);
        try(BackupRepository repository=new BackupRepository(context,identity)) {
            prepare(context,repository,hash);
            repository.call(80,"ce",1,BackupBridge.utcTicks(),false,true);
            repository.call(81,"ce");repository.call(82,"ce","actual-system-owner");repository.call(83,"ce");
            BackupBridge.Run run=new BackupBridge.Run(identity);
            BackupBridge.Work work=new BackupBridge.Work(repository.call(104,"ce").get(0),true);
            work.future=new java.util.concurrent.CompletableFuture<Void>();run.work.put(work.id,work);
            java.util.concurrent.CompletableFuture<Void> result=new java.util.concurrent.CompletableFuture<>();
            Thread recovery=new Thread(()->{try{BackupBridge.synchronizeExecutor(repository,false);result.complete(null);}catch(Throwable failure){result.completeExceptionally(failure);}});
            try(BackupBridge.Lifecycle lifecycle=BackupBridge.acquireLifecycle(identity)) {
                synchronized(lifecycle) {
                    recovery.start();long deadline=System.nanoTime()+java.util.concurrent.TimeUnit.SECONDS.toNanos(5);
                    while(recovery.getState()!=Thread.State.BLOCKED && !result.isDone() && System.nanoTime()<deadline)Thread.sleep(1);
                    check(recovery.getState()==Thread.State.BLOCKED,"Recovery did not wait for the lifecycle owner");
                    active.put(identity,run);
                    try(BackupBridge.Lifecycle same=BackupBridge.acquireLifecycle(identity);
                        BackupBridge.Lifecycle other=BackupBridge.acquireLifecycle("e".repeat(64))) {
                        check(same==lifecycle && other!=lifecycle,"Lifecycle locks are not scoped to the repository");
                    }
                }
                result.get(5,java.util.concurrent.TimeUnit.SECONDS);
                check(!work.future.isDone() && !repository.call(104,"ce").get(0).flag("released"),"Recovery released a live control owner");
                check(repository.call(7,"cc").get(0).number("state")==1,"Recovery falsely interrupted live work");
                try{BackupBridge.synchronizeExecutor(repository,true);throw new AssertionError("Explicit recovery accepted a live owner");}
                catch(IllegalStateException expected){}
            } finally {active.remove(identity);run.transfers.shutdown();recovery.join(5000);}
            java.util.concurrent.ExecutorService recoveries=java.util.concurrent.Executors.newFixedThreadPool(2);
            java.util.concurrent.CountDownLatch begin=new java.util.concurrent.CountDownLatch(1);
            try {
                java.util.List<java.util.concurrent.Future<?>> pending=new java.util.ArrayList<>();
                for(int i=0;i<2;i++)pending.add(recoveries.submit(()->{begin.await();BackupBridge.synchronizeExecutor(repository,false);return null;}));
                begin.countDown();for(java.util.concurrent.Future<?> pendingRecovery:pending)pendingRecovery.get(5,java.util.concurrent.TimeUnit.SECONDS);
            } finally {recoveries.shutdownNow();}
            check(repository.call(104,"ce").get(0).flag("released"),"Idle recovery failed to release an ended owner");
            check(repository.call(7,"cc").get(0).number("state")==6,"Unknown outcome was not preserved");
        }
        for(boolean executing:new boolean[]{false,true}) {
            identity=(executing?"f":"e").repeat(64);
            try(BackupRepository repository=new BackupRepository(context,identity)) {
                prepare(context,repository,hash);long past=BackupBridge.utcTicks()-864000000000L-10000000L;
                repository.call(80,"ce",1,past,false,true);repository.call(81,"ce");repository.call(82,"ce","prior");
                repository.call(86,"ce","paused",past,true);repository.call(87,"ce");repository.call(102,"cc",1);
                repository.call(103,"cc","protected",past);
                repository.call(80,"cf",1,past,false,true);
                if(executing){repository.call(81,"cf");repository.call(82,"cf","actual-owner");repository.call(83,"cf");}
                BackupBridge.Work work=new BackupBridge.Work(repository.call(104,"cf").get(0),true);
                work.future=new java.util.concurrent.CompletableFuture<Void>();
                BackupBridge.Run run=new BackupBridge.Run(identity);active.put(identity,run);
                if(executing)run.work.put(work.id,work);
                try {
                    BackupBridge.synchronizeExecutor(repository,false);
                    java.lang.reflect.Method settle=BackupBridge.class.getDeclaredMethod("settle",BackupRepository.class,BackupBridge.Run.class);
                    settle.setAccessible(true);settle.invoke(null,repository,run);
                    if(executing) {
                        check(work.stopped && !work.pausing,"Expired Query did not stop with timeout semantics");
                        check(!repository.call(104,"cf").get(0).flag("released"),"Cancellation fabricated actual release");
                        repository.call(86,"cf","Confirmation deadline reached",BackupBridge.utcTicks(),false);
                        repository.call(87,"cf");run.work.clear();settle.invoke(null,repository,run);
                    }
                    check(repository.call(104,"cf").get(0).flag("released"),"Ended expired Query retained its control file owner");
                    check(repository.call(7,"cc").get(0).number("state")==6,"Expired Query continued automatically");
                } finally {active.remove(identity);run.transfers.shutdown();}
            }
        }
        java.lang.reflect.Field lifecycles=BackupBridge.class.getDeclaredField("lifecycles");lifecycles.setAccessible(true);
        check(((java.util.Map<?,?>)lifecycles.get(null)).isEmpty(),"Lifecycle registry leaked repositories");
        System.out.println("Android scheduling, recovery exclusion, deadline ownership and isolated cleanup passed through production JNI/repository");
    }
    static void prepare(Context context,BackupRepository repository,String hash) throws Exception {
        repository.call(58,"aa");
        repository.call(2,"bb","aa",1L,1L,"cc","asset:photo","v1","photo.jpg","image/jpeg","",0L);
        repository.call(77,"cc","aa",4L,4L,1024L,2L);
        try(FileOutputStream output=new FileOutputStream(new File(BackupRepository.root(context),repository.id+"/payloads/cc.payload"))){output.write(new byte[]{1,2,3,4});}
        repository.call(3,"cc","aa",4L,hash,"image/jpeg",1024L,2L);
        repository.call(96,"cc","aa",3L);repository.call(9,"cc",1L,0L,4L);repository.call(74,"cc",1L,"protected");
    }
}'''
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--jdk', type=Path, required=True)
    parser.add_argument('--android-jar', type=Path, required=True)
    parser.add_argument('--core', type=Path, required=True)
    parser.add_argument('--repository', type=Path, required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='ufbackup-java-') as folder:
        root = Path(folder)
        sources = []
        for relative, content in SOURCES.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)
            sources.append(str(path))
        java = PACKAGE / 'Runtime/Plugins/Android/UIFrameGallery.androidlib/src/main/java/com/zzq/uiframe/media'
        sources += [str(java / (name + '.java')) for name in ('BackupBridge', 'BackupRepository', 'BackupJobService', 'BackupForegroundService', 'BackupPauseReceiver')]
        subprocess.run([str(args.jdk / 'bin/javac'), '-cp', str(args.android_jar), '-d', str(root / 'classes'), *sources], check=True)
        bridge = root / 'libbinding_jni.dylib'
        subprocess.run(['xcrun', 'clang++', '-std=c++17', '-dynamiclib', str(NATIVE / 'src/android.cpp'),
            '-I' + str(NATIVE / 'include'), '-I' + str(args.jdk / 'include'), '-I' + str(args.jdk / 'include/darwin'),
            str(args.repository), '-Wl,-rpath,' + str(args.repository.parent), '-Wl,-rpath,' + str(args.core.parent),
            '-o', str(bridge)], check=True)
        for identity in (letter * 64 for letter in 'abcdef'):
            db = Repository(str(args.core), str(args.repository), root / 'data/UIFrameBackup', True, identity)
            db.close()
        subprocess.run([str(args.jdk / 'bin/java'), '-Djava.library.path=' + os.pathsep.join(map(str, (args.core.parent, args.repository.parent))),
            '-cp', str(root / 'classes') + os.pathsep + str(args.android_jar), 'com.zzq.uiframe.media.BindingTest',
            str(root / 'data'), str(bridge)], check=True)


if __name__ == '__main__':
    main()
