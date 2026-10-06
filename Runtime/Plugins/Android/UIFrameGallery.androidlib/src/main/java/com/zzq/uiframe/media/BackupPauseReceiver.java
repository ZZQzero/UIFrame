package com.zzq.uiframe.media;
import android.content.*;
public final class BackupPauseReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context,Intent intent) {
        PendingResult result=goAsync();
        new Thread(()->{
            try(BackupRepository repository=new BackupRepository(context,intent.getStringExtra("repository"))) {
                repository.call(15,true);
                org.json.JSONObject response=new org.json.JSONObject(BackupBridge.call(context,new org.json.JSONObject().put("op","pause").put("repository",repository.id).toString()));
                if(!response.getBoolean("exists"))throw new IllegalStateException(response.getString("error"));
            } catch(Exception error){android.util.Log.e("UIFrameBackup","Pause failed",error);}
            finally{result.finish();}
        },"UIFrameBackupPause").start();
    }
}

