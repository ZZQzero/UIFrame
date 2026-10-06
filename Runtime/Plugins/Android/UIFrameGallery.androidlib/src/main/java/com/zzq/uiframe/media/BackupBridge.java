package com.zzq.uiframe.media;

import android.app.*;
import android.app.job.*;
import android.content.*;
import android.net.*;
import android.os.*;
import android.security.keystore.*;
import android.util.Base64;
import org.json.*;
import java.io.*;
import java.net.*;
import java.nio.ByteBuffer;
import java.nio.charset.*;
import java.security.*;
import java.util.*;
import java.util.concurrent.*;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;

/** OS scheduling and HTTP only. The shared repository owns the protocol state machine. */
public final class BackupBridge {
    private static final Object LOCK=new Object();
    private static final String KEY="UIFrame.Backup.Credentials";
    private static SecretKey encryptionKey;
    private static final Map<String,Run> active=new HashMap<>();
    private static final Map<String,Exception> failures=new HashMap<>();
    private static final Map<String,CompletableFuture<Void>> foregroundAdmissions=new HashMap<>();
    private static final Map<String,Lifecycle> lifecycles=new HashMap<>();
    // Only recovery/startup sequences use this per-repository monitor. Network
    // streams run outside it; waiting callers retain the monitor until release.
    static final class Lifecycle implements AutoCloseable {
        final String repository;
        int owners;
        Lifecycle(String repository){this.repository=repository;}
        @Override public void close(){synchronized(LOCK){if(--owners==0)lifecycles.remove(repository);}}
    }
    static Lifecycle acquireLifecycle(String identity) {
        synchronized(LOCK) {
            Lifecycle value=lifecycles.computeIfAbsent(identity,Lifecycle::new);value.owners++;return value;
        }
    }
    private static final Handler MAIN=new Handler(Looper.getMainLooper());
    static final String CHANNEL="uiframe_backup";
    interface Completion { void finished(Run run); }
    static final class Work {
        final String id; final boolean control,wifi;
        final BackupRepository.Row row;
        volatile HttpURLConnection connection;
        volatile boolean stopped,pausing;
        Future<?> future;
        Work(BackupRepository.Row row,boolean control) {
            this.row=row;this.control=control;this.id=row.text(control?"id":"task_id");wifi=row.flag("wifi_only");
        }
        void stop(boolean pause) {pausing=pause;stopped=true;HttpURLConnection current=connection;if(current!=null)current.disconnect();}
    }
    static final class Run {
        final String repository;
        final Map<String,Work> work=new ConcurrentHashMap<>();
        final ExecutorService transfers=Executors.newFixedThreadPool(3);
        volatile long confirmed,total;
        volatile boolean notifyProgress;
        volatile boolean stopped;
        boolean signaled;
        volatile String stopReason="System execution stopped";
        Run(String repository){this.repository=repository;}
        synchronized void signal(){signaled=true;notifyAll();}
        void stop(String reason){if(!stopped)stopReason=reason;stopped=true;for(Work w:work.values())w.stop(false);signal();}
    }
    private static SecretKey secret() throws Exception {
        synchronized(LOCK) {
            if(encryptionKey!=null)return encryptionKey;
            KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);
            if(!store.containsAlias(KEY)) {
                KeyGenerator generator=KeyGenerator.getInstance("AES","AndroidKeyStore");
                generator.init(new KeyGenParameterSpec.Builder(KEY,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
                generator.generateKey();
            }
            encryptionKey=(SecretKey)store.getKey(KEY,null);return encryptionKey;
        }
    }
    private static String encrypt(String value) throws Exception {
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,secret());
        return Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP)+":"+Base64.encodeToString(cipher.doFinal(value.getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP);
    }
    private static String decrypt(String value) throws Exception {
        if(value==null)throw new IOException("Protected backup credentials unavailable");
        String[] parts=value.split(":",-1);if(parts.length!=2)throw new IOException("Invalid encrypted credential");
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,secret(),new GCMParameterSpec(128,Base64.decode(parts[0],Base64.NO_WRAP)));
        return new String(cipher.doFinal(Base64.decode(parts[1],Base64.NO_WRAP)),StandardCharsets.UTF_8);
    }
    static int jobId(String repository){return 0x20000000|(int)(Long.parseLong(repository.substring(0,7),16)&0x0fffffff);}
    static long utcTicks(){return 621355968000000000L+System.currentTimeMillis()*10000L;}
    static void foregroundStarted(String identity,Exception error) {
        CompletableFuture<Void> admission;synchronized(LOCK){admission=foregroundAdmissions.remove(identity);}
        if(admission!=null){if(error==null)admission.complete(null);else admission.completeExceptionally(error);}
        else if(error!=null)android.util.Log.e("UIFrameBackup","Foreground admission failed",error);
    }
    static Notification notification(Context context,String repository) {
        NotificationManager manager=(NotificationManager)context.getSystemService(Context.NOTIFICATION_SERVICE);
        if(Build.VERSION.SDK_INT>=26)manager.createNotificationChannel(new NotificationChannel(CHANNEL,"Photo backup",NotificationManager.IMPORTANCE_LOW));
        Notification.Builder builder=Build.VERSION.SDK_INT>=26?new Notification.Builder(context,CHANNEL):new Notification.Builder(context);
        Intent pause=new Intent(context,BackupPauseReceiver.class).putExtra("repository",repository);
        PendingIntent action=PendingIntent.getBroadcast(context,jobId(repository),pause,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Intent launch=context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());
        Run run;synchronized(LOCK){run=active.get(repository);}
        long total=run==null?0:run.total,confirmed=run==null?0:run.confirmed;
        builder.setSmallIcon(android.R.drawable.stat_sys_upload).setContentTitle("Backing up photos")
            .setContentText(total==0?"Preparing transfers":confirmed+" of "+total+" photos confirmed").setOngoing(true).setOnlyAlertOnce(true)
            .setProgress((int)Math.min(Integer.MAX_VALUE,total),(int)Math.min(Integer.MAX_VALUE,confirmed),total==0)
            .addAction(new Notification.Action.Builder(null,"Pause",action).build());
        if(launch!=null)builder.setContentIntent(PendingIntent.getActivity(context,jobId(repository),launch,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE));
        return builder.build();
    }
    private static void schedule(Context context,BackupRepository repository,long earliest) throws Exception {
        synchronized(LOCK) {
            if(failures.containsKey(repository.id))throw failures.get(repository.id);
            Run run=active.get(repository.id);
            if(run!=null) synchronized(run) {
                if(run.stopped)throw new IOException("Android execution is stopping; wait for release before requesting scheduling again");
                repository.call(BackupRepository.SYSTEM_SCHEDULED,1);run.signal();return;
            }
        }
        BackupRepository.Row info=repository.call(BackupRepository.INFO).get(0);
        List<BackupRepository.Row> attempts=repository.call(BackupRepository.ATTEMPTS,0,1,100);
        boolean pending=false,any=false;long bytes=0;
        for(BackupRepository.Row row:attempts) {
            if(row.number("submission_state")>=1 && !row.flag("credential_released") && (row.number("protocol_phase")<3 || row.number("desired_action")==2)) {
                if(info.flag("paused") && row.number("desired_action")!=2)continue;
                pending=true;any|=!row.flag("wifi_only");bytes+=row.number("byte_count");
            }
        }
        if(!pending)return;
        boolean user=info.number("transfer_mode")==1;
        if(user && Build.VERSION.SDK_INT<34) {
            if(Looper.myLooper()==Looper.getMainLooper())throw new IllegalStateException("Foreground backup admission must be requested off the main thread");
            CompletableFuture<Void> admission=new CompletableFuture<>();
            synchronized(LOCK){if(foregroundAdmissions.putIfAbsent(repository.id,admission)!=null)throw new IllegalStateException("Foreground admission is already pending");}
            Intent intent=new Intent(context,BackupForegroundService.class).putExtra("repository",repository.id);
            try {
                if(Build.VERSION.SDK_INT>=26)context.startForegroundService(intent);else context.startService(intent);
                admission.get(10,TimeUnit.SECONDS);
            } finally {synchronized(LOCK){foregroundAdmissions.remove(repository.id,admission);}}
            return;
        }
        JobScheduler scheduler=(JobScheduler)context.getSystemService(Context.JOB_SCHEDULER_SERVICE);
        int id=jobId(repository.id);JobInfo previous=scheduler.getPendingJob(id);
        if(previous!=null) {
            if(!repository.id.equals(previous.getExtras().getString("repository")))throw new IllegalStateException("Backup scheduler identity collision");
            repository.call(BackupRepository.SYSTEM_SCHEDULED,1);return;
        }
        PersistableBundle extras=new PersistableBundle();extras.putString("repository",repository.id);extras.putBoolean("user",user);
        JobInfo.Builder builder=new JobInfo.Builder(id,new ComponentName(context,BackupJobService.class)).setExtras(extras)
            .setRequiredNetworkType(any?JobInfo.NETWORK_TYPE_ANY:JobInfo.NETWORK_TYPE_UNMETERED).setPersisted(true);
        if(user)builder.setUserInitiated(true).setEstimatedNetworkBytes(1024*1024,Math.max(1,bytes));
        else if(earliest>utcTicks())builder.setMinimumLatency((earliest-utcTicks())/10000);
        if(scheduler.schedule(builder.build())!=JobScheduler.RESULT_SUCCESS)throw new IOException("Android refused backup scheduling; user initiated work requires foreground eligibility");
        repository.call(BackupRepository.SYSTEM_SCHEDULED,1);
    }
    public static String call(Context context,String json) {
        Context app=context.getApplicationContext();
        try {
            JSONObject request=new JSONObject(json);String op=request.getString("op");
            if(op.equals("root"))return new JSONObject().put("exists",true).put("root",BackupRepository.root(app).getCanonicalPath()).toString();
            String identity=request.getString("repository");
            synchronized(LOCK) {
                if(!op.equals("recover") && failures.containsKey(identity))throw failures.get(identity);
            }
            try(BackupRepository repository=new BackupRepository(app,identity)) {
                String id=request.optString("id","");
                if(op.equals("accept") || op.equals("resume") || op.equals("reconcile")) {
                    String token=request.getString("token");if(token.trim().isEmpty())throw new IllegalArgumentException("Missing API credential");
                    String credential=encrypt(token);
                    if(op.equals("accept"))repository.call(BackupRepository.HANDOFF,id,request.getLong("generation"),credential);
                    else if(op.equals("resume"))repository.call(BackupRepository.PROTOCOL_RESUME,id,credential,utcTicks());
                    else repository.call(BackupRepository.PROTOCOL_RECONCILE,id,utcTicks(),request.getBoolean("cancel"),credential);
                } else if(op.equals("wake") || op.equals("sync") || op.equals("recover") || op.equals("pause")) {
                    synchronizeExecutor(repository,op.equals("recover"));
                    if(op.equals("pause"))((JobScheduler)app.getSystemService(Context.JOB_SCHEDULER_SERVICE)).cancel(jobId(identity));
                    else schedule(app,repository,0);
                } else throw new IllegalArgumentException("Unknown native backup command");
                return new JSONObject().put("exists",true).toString();
            }
        } catch(Exception error) {return "{\"exists\":false,\"error\":"+JSONObject.quote(error.toString())+"}";}
    }
    static Run start(Context context,String identity,Completion completion) {
        final Run run;
        synchronized(LOCK) {
            if(active.containsKey(identity))return null;
            if(failures.containsKey(identity)){android.util.Log.e("UIFrameBackup","Repository requires explicit recovery",failures.get(identity));return null;}
            if(active.size()>=4){failures.put(identity,new IllegalStateException("Four backup repositories are already executing; explicitly recover this repository after a slot is available"));android.util.Log.e("UIFrameBackup","Backup executor capacity reached for "+identity);return null;}
            run=new Run(identity);active.put(identity,run);
        }
        new Thread(()->{
            Exception failure=null;long next=0;
            try(BackupRepository repository=new BackupRepository(context,identity);NetworkPolicy network=new NetworkPolicy(context,run)) {
                Exception executionFailure=null;
                try {
                // start() can register promptly on the system main thread, but
                // cannot recover or execute while an idle control call owns this gate.
                try(Lifecycle lifecycle=acquireLifecycle(identity)) {
                    synchronized(lifecycle){recoverIdle(repository);repository.call(BackupRepository.SYSTEM_SCHEDULED,1);}
                }
                run.notifyProgress=repository.call(BackupRepository.INFO).get(0).number("transfer_mode")==1;
                while(!run.stopped) {
                    synchronized(run){run.signaled=false;}
                    for(Work work:new ArrayList<>(run.work.values()))if(work.future.isDone()){work.future.get();run.work.remove(work.id);}
                    synchronize(repository,run);settle(repository,run);
                    if(run.stopped)break;
                    List<BackupRepository.Row> controls=repository.call(BackupRepository.CONTROLS,1,"");
                    boolean controlling=false;int photos=0;
                    for(Work work:run.work.values())if(work.control)controlling=true;else photos++;
                    if(!controlling) {
                        BackupRepository.Row ready=null;
                        for(BackupRepository.Row row:controls)if(row.number("state")<=1 && row.number("not_before_utc")<=utcTicks()){ready=row;break;}
                        if(ready==null) {
                            List<BackupRepository.Row> created=repository.call(BackupRepository.CONTROL_CREATE,UUID.randomUUID().toString().replace("-",""),1,utcTicks(),false,false);
                            if(!created.isEmpty() && created.get(0).number("not_before_utc")<=utcTicks())ready=created.get(0);
                        }
                        if(ready!=null && network.allowed(ready.flag("wifi_only")))launch(context,repository,run,network,new Work(ready,true));
                    }
                    for(BackupRepository.Row row:repository.call(BackupRepository.UPLOADS,1,"")) {
                        if(photos>=2)break;
                        if(row.number("execution_state")!=0 || run.work.containsKey(row.text("task_id")) || row.number("upload_expires_utc")<=utcTicks() || !network.allowed(row.flag("wifi_only")))continue;
                        launch(context,repository,run,network,new Work(row,false));photos++;
                    }
                    if(run.notifyProgress) {
                        long confirmed=0,total=0;
                        for(BackupRepository.Row row:repository.call(8)){total+=row.number("count");if(row.number("state")==3)confirmed=row.number("count");}
                        if(total!=run.total || confirmed!=run.confirmed){run.total=total;run.confirmed=confirmed;((NotificationManager)context.getSystemService(Context.NOTIFICATION_SERVICE)).notify(jobId(identity),notification(context,identity));}
                    }
                    controls=repository.call(BackupRepository.CONTROLS,1,"");next=repository.call(BackupRepository.PROTOCOL_WAKE,1).get(0).number("next_utc");
                    for(BackupRepository.Row row:controls)if(row.number("not_before_utc")>utcTicks())next=next==0?row.number("not_before_utc"):Math.min(next,row.number("not_before_utc"));
                    if(run.work.isEmpty()) {
                        boolean finish=repository.call(BackupRepository.ATTEMPTS,0,1,1).isEmpty()
                            || repository.call(BackupRepository.INFO).get(0).number("transfer_mode")==0 && (next==0 || next-utcTicks()>10000000L);
                        if(finish) synchronized(run) {
                            // A wake accepted since this iteration began must be
                            // inspected before an idle executor relinquishes it.
                            if(run.signaled)continue;
                            run.stopped=true;break;
                        }
                    }
                    long wait=next<=utcTicks()?5000:Math.max(1,Math.min(5000,(next-utcTicks())/10000));
                    synchronized(run){if(!run.stopped && !run.signaled && run.work.values().stream().noneMatch(w->w.future.isDone()))run.wait(wait);}
                }
                } catch(Exception error){executionFailure=error;throw error;}
                finally {
                    run.stop("Native execution ended");
                    Exception release=null;
                    for(Work work:run.work.values())try{work.future.get();}catch(Exception error){if(release==null)release=error;else release.addSuppressed(error);}
                    if(release!=null){if(executionFailure==null)throw release;if(release!=executionFailure)executionFailure.addSuppressed(release);}
                }
            } catch(Exception error) {
                failure=error instanceof ExecutionException && error.getCause() instanceof Exception?(Exception)error.getCause():error;
            } finally {
                run.stop("Native execution ended");
                run.transfers.shutdown();
                synchronized(LOCK){active.remove(identity);if(failure!=null)failures.put(identity,failure);}
                if(failure!=null)android.util.Log.e("UIFrameBackup","Repository execution stopped",failure);
                final Exception result=failure;final long due=next;
                MAIN.post(()->{
                    completion.finished(run);
                    // Only untouched automatic work gets another OS opportunity.
                    // Failed requests remain NeedsAttention; user work is not restarted.
                    if(result==null)try(BackupRepository repository=new BackupRepository(context,identity)) {
                        if(repository.call(BackupRepository.INFO).get(0).number("transfer_mode")==0)schedule(context,repository,due);
                    } catch(Exception error){synchronized(LOCK){failures.put(identity,error);}android.util.Log.e("UIFrameBackup","Scheduling remaining work failed",error);}
                });
            }
        },"UIFrameBackupCoordinator").start();
        return run;
    }
    private static void launch(Context context,BackupRepository repository,Run run,NetworkPolicy network,Work work) {
        FutureTask<Void> future=new FutureTask<Void>(()->{execute(context,repository,run,network,work);return null;}) {
            @Override protected void done(){run.signal();}
        };
        work.future=future;
        run.work.put(work.id,work);
        run.transfers.execute(future);
    }
    private static void synchronize(BackupRepository repository,Run run) throws Exception {
        boolean paused=repository.call(BackupRepository.INFO).get(0).flag("paused");
        for(Work work:run.work.values()) {
            if(work.control){if(expired(work.row))work.stop(false);else if(paused && work.row.number("kind")!=2)work.stop(true);continue;}
            BackupRepository.Row row=repository.call(BackupRepository.TASK,work.id).get(0);
            if(paused || row.number("desired_action")!=0)work.stop(row.number("desired_action")!=2);
        }
    }
    static void synchronizeExecutor(BackupRepository repository,boolean explicitRecovery) throws Exception {
        try(Lifecycle lifecycle=acquireLifecycle(repository.id)) {
            synchronized(lifecycle) {
                Run run;
                synchronized(LOCK) {
                    run=active.get(repository.id);
                    if(explicitRecovery) {
                        if(run!=null)throw new IllegalStateException("Await native execution release before recovering");
                        failures.remove(repository.id);
                    } else if(failures.containsKey(repository.id))throw failures.get(repository.id);
                }
                if(run!=null){synchronize(repository,run);run.signal();}
                else {recoverIdle(repository);settle(repository,null);}
            }
        }
    }
    private static boolean expired(BackupRepository.Row control) {
        long deadline=control.number("deadline_utc");return deadline>0 && deadline<=utcTicks();
    }
    private static void recoverIdle(BackupRepository repository) throws Exception {
        String after="";
        for(;;) {
            List<BackupRepository.Row> rows=repository.call(BackupRepository.CONTROLS,1,after);
            for(BackupRepository.Row row:rows) {
                after=row.text("id");if(row.number("state")<=1)continue;
                if(row.number("state")<4)repository.call(BackupRepository.CONTROL_RECOVER,after,"Previous Android control execution ended; reconcile its result",utcTicks(),false);
                repository.call(BackupRepository.CONTROL_RELEASE,after);
            }
            if(rows.size()<32)break;
        }
        long cursor=0;
        for(;;) {
            List<BackupRepository.Row> rows=repository.call(BackupRepository.ATTEMPTS,cursor,1,100);
            for(BackupRepository.Row row:rows) {
                cursor=row.number("sequence");
                if(row.number("execution_state")==1) {
                    repository.call(BackupRepository.UPLOAD_END,row.text("id"),row.number("current_generation"),0,"Previous Android transfer ended; reconcile its result",utcTicks());
                    repository.call(BackupRepository.UPLOAD_RELEASE,row.text("id"),row.number("current_generation"),utcTicks());
                }
            }
            if(rows.size()<100)break;
        }
    }
    private static void settle(BackupRepository repository,Run run) throws Exception {
        boolean paused=repository.call(BackupRepository.INFO).get(0).flag("paused");
        for(BackupRepository.Row row:repository.call(BackupRepository.CONTROLS,1,"")) {
                boolean timeout=expired(row);
                if((timeout || paused && row.number("kind")!=2) && row.number("state")<=1 && (run==null || !run.work.containsKey(row.text("id")))) {
                    repository.call(BackupRepository.CONTROL_FAIL,row.text("id"),timeout?"Confirmation deadline reached":"Paused before control submission",utcTicks(),!timeout);
                    repository.call(BackupRepository.CONTROL_RELEASE,row.text("id"));
                }
        }
        repository.call(BackupRepository.PROTOCOL_ACTIONS,1,utcTicks());
        long cursor=0;
        for(;;) {
            List<BackupRepository.Row> rows=repository.call(BackupRepository.ATTEMPTS,cursor,1,100);
            for(BackupRepository.Row row:rows) {
                cursor=row.number("sequence");String id=row.text("id");long generation=row.number("current_generation");
                if(run!=null && run.work.containsKey(id) || row.text("control_id")!=null)continue;
                boolean ended=row.number("protocol_phase")==3 || row.number("state")==4;
                if(row.text("upload_reference")!=null && (ended || row.number("protocol_phase")==2 || row.number("upload_expires_utc")<=utcTicks()))
                    repository.call(BackupRepository.UPLOAD_RELEASE,id,generation,utcTicks());
                if(ended && (row.number("protocol_phase")==3 || row.number("desired_action")!=2))
                    repository.call(BackupRepository.PROTOCOL_RELEASE,id,generation);
            }
            if(rows.size()<100)break;
        }
        cleanup(repository);
    }
    private static void execute(Context context,BackupRepository repository,Run run,NetworkPolicy network,Work work) throws Exception {
        BackupRepository.Row row=work.row;boolean started=false,settled=false;Exception primary=null;
        try {
            if(work.control && expired(row))throw new InterruptedIOException("Confirmation deadline reached");
            if(run.stopped || work.stopped || !network.allowed(work.wifi))throw new InterruptedIOException("Backup execution constraint changed");
            JSONObject descriptor=null;String url;Map<String,String> headers=new LinkedHashMap<>();
            BackupRepository.Row info=repository.call(BackupRepository.INFO).get(0);
            if(work.control) {
                repository.call(BackupRepository.CONTROL_SEAL,work.id);
                if(!repository.call(BackupRepository.CONTROL_SUBMITTED,work.id,"android:"+work.id).get(0).flag("admitted") ||
                    !repository.call(BackupRepository.CONTROL_START,work.id).get(0).flag("admitted")) {
                    repository.call(BackupRepository.CONTROL_FAIL,work.id,"Admission closed before transfer",utcTicks(),true);settled=true;return;
                }
                started=true;
                String[] paths={"/v2/backup/plans","/v2/backup/status","/v2/backup/cancellations"};
                url=info.text("server")+paths[(int)row.number("kind")];
                headers.put("Authorization","Bearer "+decrypt(row.text("credential_reference")));headers.put("Content-Type","application/json");
            } else {
                descriptor=new JSONObject(decrypt(row.text("upload_reference")));
                started=repository.call(BackupRepository.UPLOAD_START,work.id,row.number("generation"),"android:"+work.id,utcTicks()).get(0).flag("started");
                if(!started)return;
                url=descriptor.getString("url");JSONArray values=descriptor.getJSONArray("headers");
                for(int i=0;i<values.length();i++){JSONObject h=values.getJSONObject(i);headers.put(h.getString("name"),h.getString("value"));}
            }
            File file=new File(new File(BackupRepository.root(context),repository.id),row.text("relative_path"));
            if(!file.isFile() || file.length()!=row.number("byte_count"))throw new IOException("Owned backup file size changed");
            HttpURLConnection connection=(HttpURLConnection)new URL(url).openConnection();work.connection=connection;
            connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(30000);connection.setReadTimeout(120000);
            connection.setRequestMethod(work.control?"POST":"PUT");connection.setDoOutput(true);connection.setFixedLengthStreamingMode(file.length());
            for(Map.Entry<String,String> h:headers.entrySet())connection.setRequestProperty(h.getKey(),h.getValue());
            try(InputStream input=new FileInputStream(file);OutputStream output=connection.getOutputStream()) {
                byte[] buffer=new byte[128*1024];int count;
                while((count=input.read(buffer))!=-1){if(run.stopped || work.stopped || !network.allowed(work.wifi))throw new InterruptedIOException("Backup execution constraint changed");output.write(buffer,0,count);}
            }
            int status=connection.getResponseCode();
            if(work.control) {
                if(status!=200)throw new IOException("Backup control HTTP "+status);
                String body;
                try(InputStream input=connection.getInputStream();ByteArrayOutputStream bytes=new ByteArrayOutputStream()) {
                    byte[] buffer=new byte[8192];int count;
                    while((count=input.read(buffer))!=-1){if(bytes.size()+count>512*1024)throw new IOException("Control response exceeds 512 KiB");bytes.write(buffer,0,count);}
                    body=StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes.toByteArray())).toString();
                }
                List<BackupRepository.Row> values=repository.call(BackupRepository.CONTROL_VALIDATE,work.id,body);
                List<Object> args=new ArrayList<>(Arrays.asList(work.id,body,utcTicks(),values.size()));
                for(BackupRepository.Row value:values){args.add(value.text("task_id"));args.add(encrypt(value.text("descriptor")));}
                repository.call(BackupRepository.CONTROL_APPLY,args.toArray());settled=true;
            } else {
                JSONArray codes=descriptor.getJSONArray("successStatusCodes");boolean success=false;
                for(int i=0;i<codes.length();i++)success|=status==codes.getInt(i);
                if(!success)throw new IOException("Storage PUT HTTP "+status);
                repository.call(BackupRepository.UPLOAD_END,work.id,row.number("generation"),1,"",utcTicks());settled=true;
            }
        } catch(Exception error) {
            primary=error;
            // Persistence failures stop this repository; their commit outcome may
            // be unknown. HTTP failures are recorded per request and are not retried.
            if(error instanceof BackupRepository.Failure && ((BackupRepository.Failure)error).code!=1)throw error;
            try {
                // Network exception messages can contain signed URLs. Persist only
                // their category and the explicit system stop reason.
                boolean timeout=work.control && expired(row);
                String reason=timeout?"Confirmation deadline reached":run.stopped?run.stopReason:error instanceof BackupRepository.Failure?error.getMessage():"Transfer failed: "+error.getClass().getSimpleName();
                if(work.control)repository.call(BackupRepository.CONTROL_FAIL,work.id,reason,utcTicks(),work.pausing && !timeout);
                else if(started)repository.call(BackupRepository.UPLOAD_END,work.id,row.number("generation"),work.pausing?2:0,reason,utcTicks());
                else repository.call(BackupRepository.UPLOAD_REJECT,work.id,row.number("generation"),reason,utcTicks());
                settled=true;
            } catch(Exception persistence){if(persistence!=error)error.addSuppressed(persistence);throw error;}
        } finally {
            HttpURLConnection connection=work.connection;work.connection=null;if(connection!=null)connection.disconnect();
            if(settled)try {
                if(work.control)repository.call(BackupRepository.CONTROL_RELEASE,work.id);
                else repository.call(BackupRepository.UPLOAD_RELEASE,work.id,row.number("generation"),utcTicks());
            } catch(Exception release){if(primary!=null){primary.addSuppressed(release);throw primary;}throw release;}
        }
    }
    private static final class NetworkPolicy implements AutoCloseable {
        final ConnectivityManager manager;final ConnectivityManager.NetworkCallback callback;final Run run;
        volatile boolean wifi,any;volatile RuntimeException failure;
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
        void changed(){try{refresh();for(Work work:run.work.values())if(!allowed(work.wifi))work.stop(false);run.signal();}catch(RuntimeException error){failure=error;run.stop("Network policy failed");}}
        void refresh(){
            NetworkCapabilities value=manager.getNetworkCapabilities(manager.getActiveNetwork());
            any=value!=null && value.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && value.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
            wifi=any && value.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && value.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
        }
        boolean allowed(boolean wifiOnly){if(failure!=null)throw failure;return wifiOnly?wifi:any;}
        @Override public void close(){manager.unregisterNetworkCallback(callback);}
    }
    private static void cleanup(BackupRepository repository) throws IOException {
        repository.call(BackupRepository.PROTOCOL_CLEANUP,utcTicks(),32);
        for(BackupRepository.Row row:repository.call(BackupRepository.CLEANUP_PAGE,"",32))
            try{repository.call(BackupRepository.CLEANUP_RUN,row.text("id"),row.number("updated_utc"),utcTicks());}
            catch(BackupRepository.Failure error){if(error.code!=BackupRepository.CLEANUP_FILE_FAILED)throw error;android.util.Log.e("UIFrameBackup","File cleanup failed: "+row.text("id"),error);}
    }
}
