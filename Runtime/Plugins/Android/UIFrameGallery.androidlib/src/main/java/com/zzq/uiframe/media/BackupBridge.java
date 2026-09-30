package com.zzq.uiframe.media;

import android.app.job.*;
import android.content.*;
import android.net.*;
import android.os.PersistableBundle;
import android.security.keystore.*;
import android.util.Base64;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.util.*;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;

/** Scheduling, streaming HTTP and encrypted credentials; all facts live in BackupRepository. */
public final class BackupBridge {
    private static final Object LOCK=new Object();
    private static final String KEY="UIFrame.Backup.Token.v1";
    private static final Map<String,Run> active=new HashMap<>();
    private static final Map<String,Exception> failures=new HashMap<>();
    static final class Run {
        final String repository;
        volatile boolean stopped, wifiOnly;
        boolean constraintWait;
        volatile HttpURLConnection connection;
        volatile String task;
        Run(String repository){this.repository=repository;}
        void stop(){stopped=true;HttpURLConnection value=connection;if(value!=null)value.disconnect();}
    }
    private static String hash(String text) throws Exception {
        byte[] bytes=MessageDigest.getInstance("SHA-256").digest(text.getBytes(StandardCharsets.UTF_8));
        StringBuilder result=new StringBuilder(64);for(byte b:bytes)result.append(String.format(java.util.Locale.ROOT,"%02x",b&255));return result.toString();
    }
    private static SecretKey secret() throws Exception {
        KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);
        if(!store.containsAlias(KEY)) {
            KeyGenerator generator=KeyGenerator.getInstance("AES","AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(KEY,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());generator.generateKey();
        }
        return (SecretKey)store.getKey(KEY,null);
    }
    private static String encrypt(String value) throws Exception {
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,secret());
        return Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP)+":"+Base64.encodeToString(cipher.doFinal(value.getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP);
    }
    private static String decrypt(String value) throws Exception {
        String[] parts=value.split(":",-1);if(parts.length!=2)throw new IOException("Invalid encrypted credential");
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,secret(),new GCMParameterSpec(128,Base64.decode(parts[0],Base64.NO_WRAP)));
        return new String(cipher.doFinal(Base64.decode(parts[1],Base64.NO_WRAP)),StandardCharsets.UTF_8);
    }
    private static int jobId(String repository){return 0x20000000|(int)(Long.parseLong(repository.substring(0,7),16)&0x0fffffff);}
    private static void schedule(Context context,BackupRepository repository) throws Exception {
        synchronized(LOCK){if(failures.containsKey(repository.id))throw failures.get(repository.id);}
        if(repository.call(BackupRepository.INFO).get(0).flag("paused"))return;
        long cursor=0;boolean pending=false,any=false;
        for(;;) {
            List<BackupRepository.Row> page=repository.call(BackupRepository.ATTEMPTS,cursor,1,100);
            for(BackupRepository.Row row:page) {
                cursor=row.number("sequence");
                if(row.number("state")==1 && row.number("execution_state")<=1 && row.number("desired_action")==0) {pending=true;any|=!row.flag("wifi_only");}
            }
            if(page.size()<100)break;
        }
        if(!pending)return;
        JobScheduler scheduler=(JobScheduler)context.getSystemService(Context.JOB_SCHEDULER_SERVICE);int id=jobId(repository.id);
        JobInfo previous=scheduler.getPendingJob(id);
        if(previous!=null && !repository.id.equals(previous.getExtras().getString("repository")))throw new IllegalStateException("Backup scheduler identifier already belongs to another job");
        PersistableBundle extras=new PersistableBundle();extras.putString("repository",repository.id);
        JobInfo job=new JobInfo.Builder(id,new ComponentName(context,BackupJobService.class)).setExtras(extras)
            .setRequiredNetworkType(any?JobInfo.NETWORK_TYPE_ANY:JobInfo.NETWORK_TYPE_UNMETERED).setPersisted(true).build();
        if(scheduler.schedule(job)!=JobScheduler.RESULT_SUCCESS)throw new IOException("Android refused to schedule background backup");
    }
    private static void release(BackupRepository repository,BackupRepository.Row task) throws IOException {
        repository.call(BackupRepository.RELEASE,task.text("id"),task.number("current_generation"),true,true);
    }
    private static void finish(BackupRepository repository,BackupRepository.Row task,int outcome,String backup,String error) throws IOException {
        repository.call(BackupRepository.FINISH,task.text("id"),task.number("current_generation"),outcome,backup,error,utcTicks(),task.number("byte_count"),task.text("sha256"));
    }
    private static void settleIdle(Context context,BackupRepository repository) throws Exception {
        Run running=active.get(repository.id);boolean paused=repository.call(BackupRepository.INFO).get(0).flag("paused");long cursor=0;
        for(;;) {
            List<BackupRepository.Row> page=repository.call(BackupRepository.ATTEMPTS,cursor,1,100);
            for(BackupRepository.Row task:page) {
                cursor=task.number("sequence");if(running!=null && task.text("id").equals(running.task))continue;
                if(task.number("execution_state")>=2)release(repository,task);
                else if(task.number("execution_state")==1) {
                    finish(repository,task,3,"","Previous executor ended; reconcile the server result");release(repository,task);
                }
                else if(paused || task.number("desired_action")!=0) {
                    finish(repository,task,task.number("execution_state")==0?4:3,"","Stopped before network execution");release(repository,task);
                }
            }
            if(page.size()<100)break;
        }
    }
    public static String call(Context context,String json) {
        Context app=context.getApplicationContext();
        try {
            JSONObject request=new JSONObject(json);String op=request.getString("op");
            if(op.equals("root"))return new JSONObject().put("exists",true).put("root",BackupRepository.root(app).getCanonicalPath()).toString();
            synchronized(LOCK) {
                String identity=request.getString("repository");
                if(op.equals("recover")) {
                    if(active.containsKey(identity))throw new IllegalStateException("Await native task release before recovering repository");
                    failures.remove(identity);
                } else if(failures.containsKey(identity))throw failures.get(identity);
                try(BackupRepository repository=new BackupRepository(app,request.getString("repository"))) {
                    String id=request.optString("id","");Run running=active.get(repository.id);
                    if(op.equals("submit")) {
                        BackupRepository.Row task=repository.call(BackupRepository.TASK,id).get(0);
                        if(task.number("current_generation")!=request.getLong("generation"))throw new IllegalStateException("Obsolete backup submission generation");
                        String token=request.getString("token");if(token.trim().isEmpty())throw new IllegalArgumentException("Missing backup credential");
                        String credential;
                        if(task.number("submission_state")==1) {
                            credential=task.text("credential_reference");
                            if(!token.equals(decrypt(credential)))throw new IllegalStateException("Submission credential changed for the same generation");
                        } else credential=encrypt(token);
                        repository.call(BackupRepository.SUBMITTED,id,task.number("current_generation"),Integer.toString(jobId(repository.id)),credential);
                        if(running==null)schedule(app,repository);
                    } else if(op.equals("wake") || op.equals("sync") || op.equals("recover")) {
                        if(running!=null && running.task!=null) {
                            List<BackupRepository.Row> current=repository.call(BackupRepository.TASK,running.task);
                            if(!current.isEmpty() && current.get(0).number("desired_action")!=0)running.stop();
                        }
                        settleIdle(app,repository);if(running==null)schedule(app,repository);
                    } else if(op.equals("pause")) {
                        if(running!=null)running.stop();((JobScheduler)app.getSystemService(Context.JOB_SCHEDULER_SERVICE)).cancel(jobId(repository.id));settleIdle(app,repository);
                    } else if(op.equals("stop")) {
                        if(running!=null && id.equals(running.task))running.stop();settleIdle(app,repository);
                    } else throw new IllegalArgumentException("Unknown native backup adapter command");
                    return new JSONObject().put("exists",true).toString();
                }
            }
        } catch(Exception error) {return "{\"exists\":false,\"error\":"+JSONObject.quote(error.toString())+"}";}
    }
    static Run start(BackupJobService service,JobParameters parameters) {
        String identity=parameters.getExtras().getString("repository");final Run run;
        synchronized(LOCK) {
            if(active.containsKey(identity))return null;
            if(failures.containsKey(identity)){android.util.Log.e("UIFrameBackup","Repository needs explicit recovery",failures.get(identity));return null;}
            if(active.size()>=16) {android.util.Log.e("UIFrameBackup","Native backup concurrency capacity reached");return null;}
            run=new Run(identity);active.put(identity,run);
        }
        new Thread(()->{
            Exception failure=null;
            try(BackupRepository repository=new BackupRepository(service,identity); NetworkPolicy network=new NetworkPolicy(service,run)) {
                long after=0;
                for(;;) {
                    if(run.stopped || repository.call(BackupRepository.INFO).get(0).flag("paused"))break;
                    List<BackupRepository.Row> page=repository.call(BackupRepository.ATTEMPTS,after,1,100);
                    for(BackupRepository.Row candidate:page) {
                        after=candidate.number("sequence");if(run.stopped)break;
                        synchronized(LOCK) {
                            run.task=candidate.text("id");
                            if(candidate.number("execution_state")>=2) {release(repository,candidate);run.task=null;continue;}
                            if(candidate.number("desired_action")!=0) {finish(repository,candidate,candidate.number("execution_state")==0?4:3,"","Stopped before execution");release(repository,candidate);run.task=null;continue;}
                            if(!network.allowed(candidate.flag("wifi_only"))) {run.constraintWait=true;run.task=null;continue;}
                            if(candidate.number("execution_state")==1) {finish(repository,candidate,3,"","Previous executor ended; reconcile the server result");release(repository,candidate);run.task=null;continue;}
                            List<BackupRepository.Row> started=repository.call(BackupRepository.START,candidate.text("id"),candidate.number("current_generation"));
                            if(started.isEmpty()) {finish(repository,candidate,4,"","Stopped before network admission");release(repository,candidate);run.task=null;continue;}
                            candidate=started.get(0);
                        }
                        transfer(service,repository,run,network,candidate);run.task=null;
                    }
                    if(page.size()<100)break;
                }
                cleanup(repository);
            } catch(Exception error) {failure=error;synchronized(LOCK){failures.put(identity,error);}android.util.Log.e("UIFrameBackup","Repository execution stopped",error);}
            finally {

                // Application failures are persisted or surfaced; they never ask
                // JobScheduler to retry the failed transfer implicitly.
                service.complete(parameters,run,failure==null && run.constraintWait && !run.stopped);
                synchronized(LOCK) {active.remove(identity);}
                if(failure==null && !run.constraintWait) {
                    try(BackupRepository repository=new BackupRepository(service,identity)){schedule(service,repository);}
                    catch(Exception error){synchronized(LOCK){failures.put(identity,error);}android.util.Log.e("UIFrameBackup","Scheduling remaining work failed",error);}
                }
            }
        },"UIFrameBackup").start();return run;
    }
    static boolean stop(Run run){if(run!=null)run.stop();return false;}
    private static final class NetworkPolicy implements AutoCloseable {
        final ConnectivityManager manager;
        final ConnectivityManager.NetworkCallback callback;
        final Run run;
        volatile boolean wifi,any;
        volatile RuntimeException failure;
        NetworkPolicy(Context context,Run run) {
            this.run=run;manager=(ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
            callback=new ConnectivityManager.NetworkCallback() {
                @Override public void onAvailable(Network value){changed();}
                @Override public void onLost(Network value){changed();}
                @Override public void onCapabilitiesChanged(Network value,NetworkCapabilities capabilities){changed();}
            };
            manager.registerDefaultNetworkCallback(callback);
            try{refresh();}catch(RuntimeException error){try{manager.unregisterNetworkCallback(callback);}catch(RuntimeException cleanup){error.addSuppressed(cleanup);}throw error;}
        }
        void changed(){try{refresh();if(!allowed(run.wifiOnly)){HttpURLConnection value=run.connection;if(value!=null)value.disconnect();}}catch(RuntimeException error){failure=error;HttpURLConnection connection=run.connection;if(connection!=null)connection.disconnect();}}
        void refresh(){
            NetworkCapabilities value=manager.getNetworkCapabilities(manager.getActiveNetwork());
            any=value!=null && value.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && value.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
            wifi=any && value.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && value.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
        }
        boolean allowed(boolean wifiOnly){if(failure!=null)throw failure;return wifiOnly?wifi:any;}
        @Override public void close(){manager.unregisterNetworkCallback(callback);}
    }
    private static void transfer(Context context,BackupRepository repository,Run run,NetworkPolicy network,BackupRepository.Row task) throws Exception {
        run.wifiOnly=task.flag("wifi_only");boolean begun=false;JSONObject response=null;Exception primary=null;int outcome=2;HttpURLConnection connection=null;
        try {
            if(run.stopped) {outcome=4;throw new InterruptedIOException("Stopped before network execution");}
            BackupRepository.Row info=repository.call(BackupRepository.INFO).get(0);
            File payload=new File(new File(BackupRepository.root(context),repository.id),task.text("relative_path"));
            if(!payload.isFile() || payload.length()!=task.number("byte_count"))throw new IOException("Staged payload size changed");
            connection=(HttpURLConnection)new URL(info.text("server")+"/v1/uploads/"+task.text("idempotency_key")+"/background").openConnection();run.connection=connection;
            connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(30000);connection.setReadTimeout(120000);
            connection.setRequestMethod("PUT");connection.setDoOutput(true);connection.setFixedLengthStreamingMode(task.number("byte_count"));
            connection.setRequestProperty("Authorization","Bearer "+decrypt(task.text("credential_reference")));
            connection.setRequestProperty("X-Backup-Account-SHA256",hash(info.text("account")));connection.setRequestProperty("Content-Type","application/octet-stream");
            begun=true;
            try(InputStream input=new FileInputStream(payload);OutputStream output=connection.getOutputStream()) {
                byte[] bytes=new byte[128*1024];int count;
                while((count=input.read(bytes))!=-1) {
                    if(run.stopped || !network.allowed(task.flag("wifi_only"))) {outcome=3;throw new InterruptedIOException("Execution or network constraint changed");}
                    output.write(bytes,0,count);
                }
            }
            int status=connection.getResponseCode();if(status!=200)throw new IOException("Backup HTTP "+status);
            try(InputStream input=connection.getInputStream();ByteArrayOutputStream bytes=new ByteArrayOutputStream()) {
                byte[] buffer=new byte[4096];int count;
                while((count=input.read(buffer))!=-1) {if(bytes.size()+count>65536)throw new IOException("Oversized server response");bytes.write(buffer,0,count);}
                response=new JSONObject(bytes.toString("UTF-8"));
            }
            if(!response.getBoolean("completed") || !task.text("idempotency_key").equals(response.getString("uploadId"))
                || !task.text("sha256").equals(response.getString("sha256")) || response.getLong("size")!=task.number("byte_count")
                || response.getLong("offset")!=task.number("byte_count") || response.getString("backupId").isEmpty())throw new IOException("Server backup confirmation mismatch");
            outcome=1;
        } catch(Exception error) {primary=error;if(begun)outcome=3;}
        finally {run.connection=null;if(connection!=null)connection.disconnect();}
        // Completion and receipt become one business fact before releasing the
        // actual platform resources; C# does not adjudicate this response again.
        try {finish(repository,task,outcome,outcome==1?response.getString("backupId"):"",primary==null?"":primary.toString());}
        catch(Exception persistence) {if(primary!=null) {primary.addSuppressed(persistence);throw primary;}throw persistence;}
        release(repository,task);
    }
    private static void cleanup(BackupRepository repository) throws IOException {
        String cursor="";int processed=0;
        while(processed<200) {
            List<BackupRepository.Row> rows=repository.call(BackupRepository.CLEANUP_PAGE,cursor,Math.min(100,200-processed));if(rows.isEmpty())break;
            for(BackupRepository.Row row:rows){cursor=row.text("id");repository.call(BackupRepository.CLEANUP_RUN,cursor,row.number("updated_utc"),utcTicks());processed++;}
        }
    }
    private static long utcTicks(){return 621355968000000000L+System.currentTimeMillis()*10000L;}
}
