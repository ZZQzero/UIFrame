package com.zzq.uiframe.media;

import android.app.job.*;
import android.content.*;
import android.net.*;
import android.os.Build;
import android.security.keystore.*;
import android.system.Os;
import android.util.Base64;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.util.*;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;

/** One native writer, independent of Unity's lifetime. JobScheduler owns execution opportunities. */
public final class BackupBridge {
    private static final Object LOCK = new Object();
    private static final int JOB = 0x554642;
    private static final String KEY = "UIFrame.Backup.Token.v1";
    private static Run active;
    private static Exception repositoryFailure;
    private static Map<String,JSONObject> cache;
    private static final LinkedHashSet<String> pendingIds=new LinkedHashSet<>();
    private static int queuedWifi, queuedAny, scheduledNetwork = -1;
    private static boolean queued(JSONObject j) throws Exception { return j!=null && (j.getInt("state")==0 || j.getInt("state")==1); }
    private static void count(JSONObject j, int delta) throws Exception {
        if (queued(j)) { if(j.getBoolean("wifiOnly")) queuedWifi+=delta; else queuedAny+=delta; if(delta>0) pendingIds.add(j.getString("id")); else pendingIds.remove(j.getString("id")); }
    }
    private static void checkRepository() throws Exception { if (repositoryFailure!=null) throw repositoryFailure; }
    private static void failRepository(Context c, Exception error) {
        if (repositoryFailure!=null) return;
        repositoryFailure=error;
        if (active!=null) { active.stopped=true; if (active.connection!=null) active.connection.disconnect(); }
        try { ((JobScheduler)c.getSystemService(Context.JOB_SCHEDULER_SERVICE)).cancel(JOB); }
        catch (Exception cleanup) { android.util.Log.e("UIFrameBackup","Scheduler cancellation failed",cleanup); }
        scheduledNetwork=-1;
        android.util.Log.e("UIFrameBackup","Native repository stopped",error);
    }
    private static void load(Context c) throws Exception {
        checkRepository(); if (cache!=null) return;
        try {
            Map<String,JSONObject> loaded=new HashMap<>();
            File[] files=directory(c).listFiles((d,n)->n.endsWith(".json"));
            if(files==null) throw new IOException("Cannot enumerate native backup store");
            for(File file:files) {
                String id=file.getName().substring(0,32);
                loaded.put(id,readDisk(c,id));
            }
            int wifi=0, any=0;
            for(JSONObject j:loaded.values()) if(queued(j)) { if(j.getBoolean("wifiOnly")) wifi++; else any++; }
            cache=loaded; queuedWifi=wifi; queuedAny=any;
            for(JSONObject j:loaded.values()) if(queued(j)) pendingIds.add(j.getString("id"));
        } catch (Exception error) { failRepository(c,error); throw error; }
    }
    private static JSONObject read(Context c, String id) throws Exception {
        load(c); JSONObject j=cache.get(id); return j==null?null:new JSONObject(j.toString());
    }
    static final class Run {
        volatile boolean stopped, taskCanceled;
        String id;
        HttpURLConnection connection;
    }
    private static File directory(Context c) throws IOException {
        File d = new File(c.getNoBackupFilesDir(), "UIFrameTransfers");
        if (!d.isDirectory() && !d.mkdirs()) throw new IOException("Cannot create native backup store");
        return d;
    }
    private static File path(Context c, String id) throws Exception {
        if (!id.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("Invalid backup task ID");
        return new File(directory(c), id + ".json");
    }
    private static JSONObject readDisk(Context c, String id) throws Exception {
        try (InputStream input=new FileInputStream(path(c,id)); ByteArrayOutputStream bytes=new ByteArrayOutputStream()) {
            byte[] buffer=new byte[4096]; int n;
            while ((n=input.read(buffer))!=-1) bytes.write(buffer,0,n);
            return new JSONObject(bytes.toString("UTF-8"));
        }
    }
    private static void save(Context c, JSONObject j) throws Exception {
        load(c);
        try {
            String encoded=j.toString(); JSONObject snapshot=new JSONObject(encoded); String id=snapshot.getString("id");
            File target=path(c,id), temporary=new File(target+".new");
            try (FileOutputStream out=new FileOutputStream(temporary)) {
                out.write(encoded.getBytes(StandardCharsets.UTF_8)); out.getFD().sync();
            }
            // The checked OS rename commits atomically; every failed write/rename is observable.
            Os.rename(temporary.getPath(),target.getPath());
            count(cache.get(id),-1); cache.put(id,snapshot); count(snapshot,1);
        } catch (Exception error) { failRepository(c,error); throw error; }
    }
    private static SecretKey secret() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (!store.containsAlias(KEY)) {
            KeyGenerator g = KeyGenerator.getInstance("AES", "AndroidKeyStore");
            g.init(new KeyGenParameterSpec.Builder(KEY, KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            g.generateKey();
        }
        return (SecretKey)store.getKey(KEY,null);
    }
    private static String encrypt(String value) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE,secret());
        return Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP)+":"+Base64.encodeToString(cipher.doFinal(value.getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP);
    }
    private static String decrypt(String value) throws Exception {
        String[] parts = value.split(":"); Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE,secret(),new GCMParameterSpec(128,Base64.decode(parts[0],Base64.NO_WRAP)));
        return new String(cipher.doFinal(Base64.decode(parts[1],Base64.NO_WRAP)),StandardCharsets.UTF_8);
    }
    private static JSONObject status(JSONObject j) throws Exception {
        if (j == null) return new JSONObject().put("exists",false).put("released",true);
        return new JSONObject().put("exists",true).put("released",j.getBoolean("released"))
            .put("state",j.getInt("state")).put("error",j.optString("error",""))
            .put("backupId",j.optString("backupId","")).put("confirmedBytes",j.optLong("confirmedBytes"));
    }
    public static String call(Context context, String json) {
        Context c = context.getApplicationContext();
        synchronized (LOCK) {
            try {
                JSONObject request = new JSONObject(json); String id=request.getString("id"), op=request.getString("op");
                if (repositoryFailure!=null) {
                    if ((op.equals("pause") || op.equals("cancel")) && active!=null && id.equals(active.id)) {
                        active.taskCanceled=true; if (active.connection!=null) active.connection.disconnect();
                    }
                    throw repositoryFailure;
                }
                JSONObject j=read(c,id);
                // A stopped process has no Java worker. Recover ownership without declaring success.
                if (j!=null && (active==null || !id.equals(active.id)) && !j.getBoolean("released")) {
                    j.put("released",true); if (j.getInt("state")==1) j.put("state",0); save(c,j);
                }
                if (op.equals("submit")) {
                    if (j==null) {
                        File payload=new File(request.getString("payload"));
                        if (!payload.isFile() || payload.length()!=request.getLong("size")) throw new IOException("Staged payload size mismatch");
                        request.put("credential",encrypt(request.getString("token"))); request.remove("token"); request.remove("op");
                        j=request.put("state",0).put("released",true); save(c,j);
                    }
                    schedule(c);
                } else if (op.equals("wake")) {
                    if (j==null) throw new IllegalStateException("Native transfer not found");
                    schedule(c);
                } else if (op.equals("pause") || op.equals("cancel")) {
                    if (j!=null) {
                        int state=j.getInt("state");
                        if (state==3 && op.equals("cancel")) throw new IllegalStateException("A committed backup cannot be canceled");
                        if (state!=3 && state!=8 && (op.equals("cancel") || state==0 || state==1)) {
                            j.put("state",op.equals("cancel")?8:4); j.remove("credential"); save(c,j);
                            if (active!=null && id.equals(active.id)) {
                                active.taskCanceled=true;
                                if (active.connection!=null) active.connection.disconnect();
                            }
                        }
                    }
                } else if (op.equals("forget")) {
                    if (j!=null && (!j.getBoolean("released") || j.getInt("state")==0 || j.getInt("state")==1))
                        throw new IllegalStateException("Transfer still owns its payload");
                    File target=path(c,id), temporary=new File(target+".new");
                    if (temporary.exists() && !temporary.delete()) throw new IOException("Cannot remove native task staging file");
                    if (target.exists() && !target.delete()) throw new IOException("Cannot remove native task record");
                    count(cache.remove(id),-1); j=null;
                } else if (!op.equals("status")) throw new IllegalArgumentException("Unknown backup command");
                return status(j).toString();
            } catch (Exception e) {
                return "{\"exists\":false,\"error\":"+JSONObject.quote(e.toString())+"}";
            }
        }
    }
    private static void schedule(Context c) throws Exception {
        checkRepository(); if (active!=null) return;
        load(c); if (queuedWifi+queuedAny==0) return;
        int network=queuedAny==0?JobInfo.NETWORK_TYPE_UNMETERED:JobInfo.NETWORK_TYPE_ANY;
        if (scheduledNetwork==network) return;
        JobInfo job=new JobInfo.Builder(JOB,new ComponentName(c,BackupJobService.class))
            .setRequiredNetworkType(network).setPersisted(true).setBackoffCriteria(30000,JobInfo.BACKOFF_POLICY_EXPONENTIAL).build();
        if (((JobScheduler)c.getSystemService(Context.JOB_SCHEDULER_SERVICE)).schedule(job)!=JobScheduler.RESULT_SUCCESS)
            throw new IOException("Android refused to schedule background backup");
        scheduledNetwork=network;
    }

    private static boolean network(Context c, boolean wifi) {
        ConnectivityManager m=(ConnectivityManager)c.getSystemService(Context.CONNECTIVITY_SERVICE);
        NetworkCapabilities n=m.getNetworkCapabilities(m.getActiveNetwork());
        return n!=null && n.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && n.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
            && (!wifi || n.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && n.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED));
    }
    static Run start(BackupJobService service, JobParameters parameters) {
        final Run run;
        synchronized (LOCK) { if (repositoryFailure!=null || active!=null) return null; run=new Run(); active=run; scheduledNetwork=-1; }
        new Thread(()-> {
            boolean pending=false;
            try {
                while (!run.stopped) {
                    JSONObject selected=null;
                    boolean wifi=network(service,true), any=network(service,false);
                    synchronized (LOCK) {
                        load(service);
                        for (String id:pendingIds) {
                            JSONObject stored=cache.get(id);
                            if (!(stored.getBoolean("wifiOnly")?wifi:any)) continue;
                            selected=new JSONObject(stored.toString()); break;
                        }
                        if(selected!=null) {
                            run.id=selected.getString("id"); run.taskCanceled=false;
                            selected.put("state",1).put("released",false); save(service,selected);
                        }
                    }
                    if (selected==null) break;
                    transfer(service,run,selected);
                    synchronized (LOCK) { run.id=null; run.connection=null; }
                }
            } catch (Exception error) { synchronized (LOCK) { failRepository(service,error); } }
            finally {
                synchronized (LOCK) {
                    active=null;
                    try {
                        pending=repositoryFailure==null && queuedWifi+queuedAny>0;
                        if (!run.stopped || repositoryFailure!=null) service.jobFinished(parameters,pending);
                        else schedule(service);
                    } catch (Exception error) { failRepository(service,error); }
                }
            }
        },"UIFrameBackup").start();
        return run;
    }
    static boolean stop(Run run) {
        synchronized (LOCK) {
            if (run!=null) { run.stopped=true; if (run.connection!=null) run.connection.disconnect(); }
            return repositoryFailure==null;
        }
    }
    private static void transfer(Context c, Run run, JSONObject original) throws Exception {
        JSONObject response=null; Exception failure=null; int code=0; boolean constraintLost=false;
        ConnectivityManager connectivity=(ConnectivityManager)c.getSystemService(Context.CONNECTIVITY_SERVICE);
        ConnectivityManager.NetworkCallback monitor=null;
        java.util.concurrent.atomic.AtomicBoolean allowed=new java.util.concurrent.atomic.AtomicBoolean(false);
        final boolean wifi=original.getBoolean("wifiOnly");
        try {
            if (android.os.Build.VERSION.SDK_INT>=24) {
                monitor=new ConnectivityManager.NetworkCallback() {
                    private void refresh() { allowed.set(network(c,wifi)); }
                    @Override public void onAvailable(android.net.Network n) { refresh(); }
                    @Override public void onLost(android.net.Network n) { refresh(); }
                    @Override public void onCapabilitiesChanged(android.net.Network n, NetworkCapabilities capabilities) { refresh(); }
                };
                connectivity.registerDefaultNetworkCallback(monitor); allowed.set(network(c,wifi));
            }
            HttpURLConnection connection=(HttpURLConnection)new URL(original.getString("url")).openConnection();
            synchronized (LOCK) {
                run.connection=connection;
                if (run.stopped || run.taskCanceled) throw new InterruptedIOException("Transfer stopped");
            }
            connection.setInstanceFollowRedirects(false); connection.setConnectTimeout(30000); connection.setReadTimeout(120000);
            connection.setRequestMethod("PUT"); connection.setDoOutput(true); connection.setFixedLengthStreamingMode(original.getLong("size"));
            connection.setRequestProperty("Authorization","Bearer "+decrypt(original.getString("credential")));
            connection.setRequestProperty("X-Backup-Account-SHA256",original.getString("account"));
            connection.setRequestProperty("Content-Type","application/octet-stream");
            try (InputStream input=new FileInputStream(original.getString("payload")); OutputStream output=connection.getOutputStream()) {
                byte[] buffer=new byte[131072]; int n;
                while ((n=input.read(buffer))!=-1) {
                    if (run.stopped || run.taskCanceled) throw new InterruptedIOException("Transfer stopped");
                    if (!(monitor==null ? network(c,wifi) : allowed.get())) {
                        constraintLost=true; throw new InterruptedIOException("Network constraint changed");
                    }
                    output.write(buffer,0,n);
                }
            }
            code=connection.getResponseCode();
            if (code!=200) throw new IOException("Backup HTTP "+code);
            try (InputStream input=connection.getInputStream(); ByteArrayOutputStream body=new ByteArrayOutputStream()) {
                byte[] buffer=new byte[4096]; int n;
                while ((n=input.read(buffer))!=-1) { if (body.size()+n>65536) throw new IOException("Oversized backup response"); body.write(buffer,0,n); }
                response=new JSONObject(body.toString("UTF-8"));
            }
            if (!response.getBoolean("completed") || !original.getString("key").equals(response.getString("uploadId"))
                || !original.getString("key").equals(response.getString("backupId")) || !original.getString("sha256").equals(response.getString("sha256"))
                || response.getLong("size")!=original.getLong("size") || response.getLong("offset")!=original.getLong("size"))
                throw new IOException("Server did not confirm the expected verified backup");
        } catch (Exception e) { failure=e; }
        finally {
            if (monitor!=null) try { connectivity.unregisterNetworkCallback(monitor); }
                catch (Exception cleanup) { if (failure==null) failure=cleanup; else android.util.Log.e("UIFrameBackup","Network monitor cleanup failed",cleanup); }
            synchronized (LOCK) { if (run.connection!=null) run.connection.disconnect(); }
        }
        synchronized (LOCK) {
            JSONObject j=read(c,run.id); j.put("released",true);
            if (j.getInt("state")==1) {
                if (failure==null) j.put("state",3).put("backupId",response.getString("backupId")).put("confirmedBytes",j.getLong("size"));
                else if (code==0 && (run.stopped || constraintLost || !network(c,j.getBoolean("wifiOnly")))) j.put("state",0);
                else j.put("state",code==401 || code==403 || code==413 || code==507?6:7).put("error",failure.toString());
            }
            if (j.getInt("state")!=0) j.remove("credential");
            save(c,j);
        }
    }
}
