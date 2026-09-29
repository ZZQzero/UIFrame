package com.zzq.uiframe.media;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.net.Uri;
import android.provider.MediaStore;
import java.util.ArrayList;

public final class GalleryActivity extends Activity {
    GalleryBridge.Job job;
    boolean returned;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        job = GalleryBridge.jobs.get(getIntent().getStringExtra("request"));
        if(job == null) { GalleryBridge.window.compareAndSet(getIntent().getStringExtra("request"), null); finish(); return; }
        job.activity = this;
        if(job.canceled) { finish(); return; }
        if(state != null) return;
        try {
            if(job.request.optString("op").equals("pickDirectory")) {
                Intent directory = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
                directory.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
                startActivityForResult(directory,73); return;
            }
            if(job.request.optString("op").equals("requestAccess")) {
                String permission = Build.VERSION.SDK_INT >= 33 ? "android.permission.READ_MEDIA_IMAGES" : Manifest.permission.READ_EXTERNAL_STORAGE;
                getSharedPreferences("uiframe_media", 0).edit().putBoolean("asked", true).apply();
                String[] permissions = Build.VERSION.SDK_INT >= 34 ? new String[] {permission, "android.permission.READ_MEDIA_VISUAL_USER_SELECTED"} : new String[] {permission};
                requestPermissions(permissions, 72); return;
            }
            int count = job.request.getInt("count"); Intent intent;
            if(Build.VERSION.SDK_INT >= 33) {
                if(count > MediaStore.getPickImagesMaxLimit()) { GalleryBridge.fail(job, "SelectionLimitExceeded", "Count exceeds system picker maximum."); returned = true; finish(); return; }
                intent = new Intent(MediaStore.ACTION_PICK_IMAGES).setType("image/*");
                if(count > 1) intent.putExtra(MediaStore.EXTRA_PICK_IMAGES_MAX, count);
            } else {
                intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("image/*").addCategory(Intent.CATEGORY_OPENABLE);
                intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, count > 1);
                intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            }
            startActivityForResult(intent, 71);
        } catch(Exception e) { returned = true; GalleryBridge.fail(job, "PresentationFailed", e.toString()); finish(); }
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data); if((request != 71 && request != 73) || job == null) return;
        returned = true;
        if(result != RESULT_OK || data == null) GalleryBridge.complete(job, GalleryBridge.response("canceled"));
        else if(request == 73) {
            try {
                Uri uri = data.getData();
                getContentResolver().takePersistableUriPermission(uri, data.getFlags() & Intent.FLAG_GRANT_READ_URI_PERMISSION);
                GalleryBridge.complete(job, GalleryBridge.response("ok").put("items",new org.json.JSONArray().put(new org.json.JSONObject().put("id",uri.toString()))));
            } catch(Exception e) { GalleryBridge.fail(job,"DirectoryAccessFailed",e.toString()); }
        }
        else {
            ArrayList<Uri> uris = new ArrayList<>();
            if(data.getClipData() != null) for(int i=0;i<data.getClipData().getItemCount();i++) uris.add(data.getClipData().getItemAt(i).getUri());
            else if(data.getData() != null) uris.add(data.getData());
            if(uris.isEmpty()) GalleryBridge.fail(job,"InvalidResult","Provider returned no images."); else GalleryBridge.picked(job, uris);
        }
        finish();
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request,permissions,results); if(request != 72 || job == null) return;
        returned = true;
        try { GalleryBridge.complete(job, GalleryBridge.response("ok").put("access", GalleryBridge.access())); }
        catch(Exception e) { GalleryBridge.fail(job,"PermissionFailed",e.toString()); }
        finish();
    }
    @Override protected void onDestroy() {
        if(isFinishing() && job != null) {
            if(!returned) GalleryBridge.complete(job, GalleryBridge.response("canceled"));
            GalleryBridge.window.compareAndSet(job.id, null); job.activity = null;
        }
        super.onDestroy();
    }
}
