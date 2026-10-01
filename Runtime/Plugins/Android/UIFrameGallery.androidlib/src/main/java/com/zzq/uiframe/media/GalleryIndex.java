package com.zzq.uiframe.media;

import android.content.ContentResolver;
import android.database.ContentObserver;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.provider.MediaStore;
import android.provider.DocumentsContract;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.IOException;
import java.util.*;

/** Bounded metadata delivery and change hints. No database or backup state lives here. */
final class GalleryIndex {
    private static final Map<String,Cursor> scans=new HashMap<>();
    private static final Map<String,Watch> watches=new HashMap<>();
    private static ContentResolver resolver(){return GalleryBridge.context.getContentResolver();}
    private static String[] columns(){
        return Build.VERSION.SDK_INT>=30?new String[]{"_id","_display_name","mime_type","_size","width","height","date_modified","bucket_id","generation_modified"}:
            new String[]{"_id","_display_name","mime_type","_size","width","height","date_modified","bucket_id"};
    }
    private static JSONObject item(Cursor cursor) throws Exception {
        String version=cursor.getString(6)+":"+cursor.getLong(3);
        if(Build.VERSION.SDK_INT>=30)version+=":"+cursor.getLong(8);
        return new JSONObject().put("id",Uri.withAppendedPath(MediaStore.Images.Media.EXTERNAL_CONTENT_URI,cursor.getString(0)).toString())
            .put("name",cursor.getString(1)).put("mime",cursor.getString(2)).put("size",cursor.getLong(3)).put("width",cursor.getInt(4)).put("height",cursor.getInt(5)).put("version",version);
    }
    private static String canonical(Uri uri) {
        if(!"content".equals(uri.getScheme()) || !"media".equals(uri.getAuthority()))return null;
        List<String> parts=uri.getPathSegments();
        if(parts.size()!=4 || !(parts.get(0).equals("external") || parts.get(0).equals("external_primary") || (Build.VERSION.SDK_INT>=29 && MediaStore.getExternalVolumeNames(GalleryBridge.context).contains(parts.get(0))))
            || !parts.get(1).equals("images") || !parts.get(2).equals("media") || !parts.get(3).matches("[0-9]+"))return null;
        return Uri.withAppendedPath(MediaStore.Images.Media.EXTERNAL_CONTENT_URI,parts.get(3)).toString();
    }
    private static void permission() throws IOException {
        String access=GalleryBridge.access();if(!access.equals("Authorized") && !access.equals("Limited"))throw new SecurityException("Photo library read access is unavailable");
    }
    private static final class Watch extends ContentObserver {
        final String album;
        final LinkedHashSet<String> paths=new LinkedHashSet<>();
        boolean reconcile=true,accessChanged;
        long serial;
        String version,boundary,access;
        Watch(String album){super(null);this.album=album;version=version();boundary=boundary();access=GalleryBridge.access();}
        void reset(){reconcile=true;paths.clear();}
        @Override public void onChange(boolean self,Uri uri){synchronized(GalleryIndex.class){
            serial++;if(GalleryBridge.access().equals("Limited"))accessChanged=true;
            if(uri==null || paths.size()>=1024){reset();return;}
            String path=canonical(uri);
            if(path==null){reset();return;}
            if(!reconcile)paths.add(path);
        }}
    }
    private static String version(){
        if(Build.VERSION.SDK_INT<30)return "";
        StringBuilder result=new StringBuilder();
        for(String volume:new TreeSet<>(MediaStore.getExternalVolumeNames(GalleryBridge.context)))
            result.append(volume).append('=').append(MediaStore.getVersion(GalleryBridge.context,volume)).append(';');
        return result.toString();
    }
    private static String boundary(){
        StringBuilder result=new StringBuilder(version());
        if(Build.VERSION.SDK_INT>=30)for(String volume:new TreeSet<>(MediaStore.getExternalVolumeNames(GalleryBridge.context)))result.append(volume).append('@').append(MediaStore.getGeneration(GalleryBridge.context,volume)).append(';');
        return result.toString();
    }
    static synchronized JSONObject call(GalleryBridge.Job job) throws Exception {
        JSONObject request=job.request;String op=request.getString("op"),id=request.optString("path");
        JSONObject result=GalleryBridge.response("ok");
        if(op.equals("imagesClose")){Cursor cursor=scans.remove(id);if(cursor!=null)cursor.close();return result;}
        if(op.equals("unobserve")){Watch watch=watches.remove(id);if(watch!=null)resolver().unregisterContentObserver(watch);return result;}
        if(op.equals("stat"))return result.put("items",new JSONArray().put(stat(request.getString("source"),id,job)));
        permission();
        if(op.equals("imagesOpen")) {
            if(scans.size()>=16 || scans.containsKey(id))throw new IllegalStateException("Photo scan capacity or identity conflict");
            String album=request.optString("album");Cursor cursor=resolver().query(MediaStore.Images.Media.EXTERNAL_CONTENT_URI,columns(),album.isEmpty()?null:"bucket_id=?",album.isEmpty()?null:new String[]{album},"_id ASC",job.signal);
            if(cursor==null)throw new IOException("MediaStore returned no cursor");scans.put(id,cursor);return result;
        }
        if(op.equals("imagesNext")) {
            Cursor cursor=scans.get(id);if(cursor==null)throw new IllegalStateException("Photo scan no longer exists");JSONArray page=new JSONArray();
            while(page.length()<200 && cursor.moveToNext()){job.check();page.put(item(cursor));}
            return result.put("items",page).put("hasNext",page.length()==200);
        }
        if(op.equals("observe")) {
            if(watches.size()>=16 || watches.containsKey(id))throw new IllegalStateException("Photo observer capacity or identity conflict");
            Watch watch=new Watch(request.optString("album"));resolver().registerContentObserver(MediaStore.Images.Media.EXTERNAL_CONTENT_URI,true,watch);watches.put(id,watch);return result;
        }
        if(op.equals("drain")) {
            Watch watch=watches.get(id);if(watch==null)throw new IllegalStateException("Photo observer no longer exists");
            String version=version(),boundary=boundary(),access=GalleryBridge.access();
            if(!version.equals(watch.version) || !access.equals(watch.access) || (!boundary.equals(watch.boundary) && watch.paths.isEmpty()))watch.reset();
            if(!access.equals(watch.access) || (!version.equals(watch.version) && access.equals("Limited")))watch.accessChanged=true;
            watch.version=version;watch.boundary=boundary;watch.access=access;
            boolean reconcile=watch.reconcile;watch.reconcile=false;JSONArray items=new JSONArray();
            Iterator<String> iterator=watch.paths.iterator();
            while(items.length()<32 && iterator.hasNext()) {
                String path=iterator.next();iterator.remove();Uri uri=Uri.parse(path);
                try(Cursor cursor=resolver().query(uri,columns(),null,null,null,job.signal)) {
                    if(cursor==null)throw new IOException("MediaStore returned no cursor");
                    if(!cursor.moveToFirst() || !watch.album.isEmpty() && !watch.album.equals(cursor.getString(7)))items.put(new JSONObject().put("id",path).put("kind",3));
                    else items.put(item(cursor).put("kind",1));
                }
            }
            boolean changed=watch.accessChanged;watch.accessChanged=false;
            return result.put("items",items).put("requiresReconcile",reconcile).put("accessChanged",changed).put("boundary",id+":"+boundary+":"+watch.serial).put("hasNext",!watch.paths.isEmpty());
        }
        throw new IllegalArgumentException("Unknown photo index operation");
    }
    private static JSONObject stat(String source,String path,GalleryBridge.Job job) throws Exception {
        if(source.equals("library")) {
            permission();try(Cursor cursor=resolver().query(Uri.parse(path),columns(),null,null,null,job.signal)) {
                if(cursor==null || !cursor.moveToFirst())throw new GalleryBridge.MediaFailure("SourceUnavailable","Photo no longer accessible");return item(cursor);
            }
        }
        if(source.equals("directory")) {
            String[] fields={DocumentsContract.Document.COLUMN_LAST_MODIFIED,DocumentsContract.Document.COLUMN_SIZE};
            try(Cursor cursor=resolver().query(Uri.parse(path),fields,null,null,null,job.signal)) {
                if(cursor==null || !cursor.moveToFirst())throw new GalleryBridge.MediaFailure("SourceUnavailable","Document no longer accessible");
                if(cursor.isNull(0) || cursor.isNull(1))throw new GalleryBridge.MediaFailure("ContentVersionUnavailable","Document provider has no content version");
                return new JSONObject().put("id",path).put("version",cursor.getString(0)+":"+cursor.getString(1));
            }
        }
        throw new IllegalArgumentException("Unsupported version source");
    }
}
