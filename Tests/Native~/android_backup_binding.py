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
    'android/content/Context.java': '''
package android.content;
public class Context {
    public static final String JOB_SCHEDULER_SERVICE="jobs",CONNECTIVITY_SERVICE="network";
    private final java.io.File root;
    public final android.app.job.JobScheduler jobs=new android.app.job.JobScheduler();
    public Context(java.io.File root){this.root=root;}
    public Context getApplicationContext(){return this;}
    public java.io.File getNoBackupFilesDir(){return root;}
    public Object getSystemService(String name){return jobs;}
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
}''',
    'android/app/job/JobInfo.java': '''
package android.app.job;
public class JobInfo {
    public static final int NETWORK_TYPE_ANY=1,NETWORK_TYPE_UNMETERED=2;
    public int id;public android.os.PersistableBundle extras;
    public android.os.PersistableBundle getExtras(){return extras;}
    public static class Builder {
        private final JobInfo value=new JobInfo();
        public Builder(int id,android.content.ComponentName component){value.id=id;}
        public Builder setExtras(android.os.PersistableBundle extras){value.extras=extras;return this;}
        public Builder setRequiredNetworkType(int type){return this;}
        public Builder setPersisted(boolean persisted){return this;}
        public JobInfo build(){return value;}
    }
}''',
    'android/app/job/JobScheduler.java': '''
package android.app.job;
public class JobScheduler {
    public static final int RESULT_SUCCESS=1;
    public int submissions;
    private final java.util.Map<Integer,JobInfo> jobs=new java.util.HashMap<>();
    public JobInfo getPendingJob(int id){return jobs.get(id);}
    public int schedule(JobInfo job){submissions++;jobs.put(job.id,job);return RESULT_SUCCESS;}
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
                repository.call(2,"bb","aa",1L,1L,"cc","asset:photo","v1","photo.jpg","image/jpeg",4L);
                try(FileOutputStream output=new FileOutputStream(new File(BackupRepository.root(context),identity+"/payloads/cc.payload"))){output.write(new byte[]{1,2,3,4});}
                repository.call(3,"cc","aa",4L,hash,hash,"image/jpeg",1024L,2L);
                repository.call(4,"bb","aa",3L);repository.call(9,"cc",1L,0L,4L,"aa");
                repository.call(BackupRepository.HANDOFF,"cc",1L,"protected-reference");
            }
            try(BackupRepository repository=new BackupRepository(context,identity)) {
                BackupRepository.Row task=repository.call(BackupRepository.SCHEDULABLE,0L,1L,100L).get(0);
                check(task.number("submission_state")==1,"Fixture did not reopen the interrupted handoff");
                if(mode==0) {
                    java.lang.reflect.Method schedule=BackupBridge.class.getDeclaredMethod("schedule",Context.class,BackupRepository.class);
                    schedule.setAccessible(true);
                    repository.call(15,1L);schedule.invoke(null,context,repository);
                    check(context.jobs.submissions==0,"Paused repository was scheduled");
                    repository.call(15,0L);schedule.invoke(null,context,repository);
                    check(context.jobs.submissions==1,"Recovered task was not scheduled");
                } else BackupBridge.bindSystemTask(repository,task);
                task=repository.call(BackupRepository.TASK,"cc").get(0);
                check(task.number("submission_state")==2 && task.number("current_generation")==1,"Binding changed or lost execution generation");
                check("protected-reference".equals(task.text("credential_reference")),"Binding replaced the credential");
                BackupBridge.bindSystemTask(repository,task);
                check(repository.call(BackupRepository.START,"cc",1L).size()==1,"Recovered binding could not start");
                repository.call(BackupRepository.FINISH,"cc",1L,4L,"","fixture stopped",5L,4L,hash);
                repository.call(BackupRepository.RELEASE,"cc",1L,true,true);
            }
        }
        System.out.println("Android scheduling and worker binding paths passed through production JNI/repository");
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
        sources += [str(java / (name + '.java')) for name in ('BackupBridge', 'BackupRepository', 'BackupJobService')]
        subprocess.run([str(args.jdk / 'bin/javac'), '-cp', str(args.android_jar), '-d', str(root / 'classes'), *sources], check=True)
        bridge = root / 'libbinding_jni.dylib'
        subprocess.run(['xcrun', 'clang++', '-std=c++17', '-dynamiclib', str(NATIVE / 'src/android.cpp'),
            '-I' + str(NATIVE / 'include'), '-I' + str(args.jdk / 'include'), '-I' + str(args.jdk / 'include/darwin'),
            str(args.repository), '-Wl,-rpath,' + str(args.repository.parent), '-Wl,-rpath,' + str(args.core.parent),
            '-o', str(bridge)], check=True)
        for identity in ('a' * 64, 'b' * 64):
            db = Repository(str(args.core), str(args.repository), root / 'data/UIFrameBackup', True, identity)
            db.close()
        subprocess.run([str(args.jdk / 'bin/java'), '-Djava.library.path=' + os.pathsep.join(map(str, (args.core.parent, args.repository.parent))),
            '-cp', str(root / 'classes') + os.pathsep + str(args.android_jar), 'com.zzq.uiframe.media.BindingTest',
            str(root / 'data'), str(bridge)], check=True)


if __name__ == '__main__':
    main()
