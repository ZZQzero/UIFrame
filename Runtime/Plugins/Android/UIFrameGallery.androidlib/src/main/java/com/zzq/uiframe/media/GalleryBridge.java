package com.zzq.uiframe.media;

import android.Manifest;
import android.app.Activity;
import android.content.ContentResolver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.ImageDecoder;
import android.graphics.Matrix;
import android.graphics.Canvas;
import android.graphics.Color;
import android.os.CancellationSignal;
import android.media.ExifInterface;
import android.net.Uri;
import android.os.Build;
import android.provider.MediaStore;
import android.provider.OpenableColumns;
import android.provider.DocumentsContract;
import android.webkit.MimeTypeMap;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicReference;

/** JNI entry points. No Unity activity subclass or AndroidX dependency is required. */
public final class GalleryBridge {
    public static boolean isUnmeteredWifi(Activity activity) {
        android.net.ConnectivityManager manager=(android.net.ConnectivityManager)activity.getSystemService(Context.CONNECTIVITY_SERVICE);
        android.net.Network network=manager.getActiveNetwork();
        android.net.NetworkCapabilities capabilities=manager.getNetworkCapabilities(network);
        return capabilities != null && capabilities.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI)
            && capabilities.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_VALIDATED)
            && capabilities.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
    }
    static final ExecutorService workers = Executors.newFixedThreadPool(2);
    static final ConcurrentHashMap<String, Job> jobs = new ConcurrentHashMap<>();
    static final AtomicReference<String> window = new AtomicReference<>();
    static Context context;
    static final class MediaFailure extends IOException {
        final String code;
        MediaFailure(String code, String message) { super(message); this.code=code; }
    }
    static final class Job {
        final JSONObject request;
        final String id;
        volatile boolean canceled;
        boolean finished;
        String result;
        JSONArray pages; int offset;
        final CancellationSignal signal = new CancellationSignal();
        Closeable input;
        volatile GalleryActivity activity;
        Job(JSONObject request) throws Exception { this.request = request; id = request.getString("id"); }
        synchronized InputStream track(InputStream stream) throws IOException {
            if(canceled) { if(stream!=null) stream.close(); throw new InterruptedIOException("Canceled"); }
            input=stream; return stream;
        }
        void check() throws InterruptedIOException { if (canceled) throw new InterruptedIOException("Canceled"); }
    }
    public static void start(Activity parent, String json) {
        final Job job;
        try { job = new Job(new JSONObject(json)); } catch (Exception e) { throw new IllegalArgumentException(e); }
        context = parent.getApplicationContext(); jobs.put(job.id, job);
        String op = job.request.optString("op");
        if (op.equals("pick") || op.equals("pickDirectory") || op.equals("requestAccess")) {
            if (!window.compareAndSet(null, job.id)) { fail(job, "PickerBusy", "Another media window is open."); return; }
            parent.runOnUiThread(() -> {
                try {
                    if (job.canceled) { window.compareAndSet(job.id, null); complete(job, response("canceled")); return; }
                    Intent intent = new Intent(parent, GalleryActivity.class);
                    intent.putExtra("request", job.id); parent.startActivity(intent);
                } catch (Exception e) { window.compareAndSet(job.id, null); fail(job, "PresentationFailed", e.toString()); }
            });
        } else workers.execute(() -> {
            try {
                job.check(); JSONObject result;
                switch (op) {
                    case "access": result = response("ok").put("access", access()); break;
                    case "albums": result = library(job, true); break;
                    case "images": result = library(job, false); break;
                    case "directory": result = directory(job); break;
                    case "export": result = export(job); break;
                    case "preview": result = preview(job); break;
                    default: throw new UnsupportedOperationException(op);
                }
                complete(job, result);
            } catch (OutOfMemoryError e) { fail(job, "MemoryLimitExceeded", "Native image allocation failed."); }
              catch (MediaFailure e) { fail(job, e.code, e.toString()); }
              catch (SecurityException e) { fail(job, "PermissionDenied", e.toString()); }
              catch (Exception e) { fail(job, "ReadFailed", e.toString()); }
        });
    }
    public static String poll(String id) {
        Job job = jobs.get(id); if (job == null) return null;
        synchronized(job) {
            if (!job.finished) return null;
            if(job.pages!=null) {
                try {
                    JSONArray page=new JSONArray(); int end=Math.min(job.pages.length(),job.offset+200);
                    for(;job.offset<end;job.offset++) page.put(job.pages.get(job.offset));
                    boolean more=end<job.pages.length(); if(!more) jobs.remove(id,job);
                    return response("ok").put("items",page).put("more",more).toString();
                } catch(Exception error) { throw new IllegalStateException(error); }
            }
            jobs.remove(id, job); return job.result;
        }
    }
    public static void cancel(String id) {
        Job job = jobs.get(id); if (job == null) return;
        synchronized(job) {
            job.canceled = true;
            if (job.finished) { clean(job); jobs.remove(id, job); }
        }
        job.signal.cancel();
        Closeable input; synchronized(job) { input=job.input; job.input=null; }
        if(input!=null) try { input.close(); } catch(IOException error) { android.util.Log.e("UIFrameGallery","Cancel stream close failed",error); }
        GalleryActivity activity = job.activity;
        if (activity != null) activity.runOnUiThread(activity::finish);
    }
    public static boolean pending(String id) { return jobs.containsKey(id); }
    static JSONObject response(String status) {
        JSONObject value = new JSONObject(); try { value.put("status", status); } catch(Exception e) { throw new IllegalStateException(e); }
        return value;
    }
    static void fail(Job job, String code, String error) {
        try { complete(job, response("error").put("code", code).put("error", error)); }
        catch (Exception e) { throw new IllegalStateException(e); }
    }
    static void complete(Job job, JSONObject response) {
        synchronized(job) {
            if (job.finished) return;
            job.finished = true;
            if (job.canceled || !response.optString("status").equals("ok")) clean(job);
            if (job.canceled) jobs.remove(job.id, job);
            else if(response.optString("status").equals("ok") && Arrays.asList("images","albums","directory").contains(job.request.optString("op")))
                job.pages=response.optJSONArray("items");
            else job.result = response.toString();
        }
    }
    static void clean(Job job) {
        String output = job.request.optString("output");
        if (!output.isEmpty()) delete(new File(output));
    }
    static void delete(File file) {
        File[] children = file.listFiles(); if (children != null) for (File child : children) delete(child);
        if (file.exists() && !file.delete()) android.util.Log.e("UIFrameGallery", "Unable to remove owned temporary file.");
    }
    static String access() {
        String permission = Build.VERSION.SDK_INT >= 33 ? "android.permission.READ_MEDIA_IMAGES" : Manifest.permission.READ_EXTERNAL_STORAGE;
        if (context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED) return "Authorized";
        if (Build.VERSION.SDK_INT >= 34 && context.checkSelfPermission("android.permission.READ_MEDIA_VISUAL_USER_SELECTED") == PackageManager.PERMISSION_GRANTED) return "Limited";
        return context.getSharedPreferences("uiframe_media", 0).getBoolean("asked", false) ? "Denied" : "NotDetermined";
    }
    static void picked(Job job, List<Uri> uris) {
        workers.execute(() -> {
            try {
                if (uris.size() > job.request.getInt("count")) { fail(job, "SelectionLimitExceeded", "The document provider returned too many images."); return; }
                JSONArray images = new JSONArray();
                for (Uri uri : uris) { job.check(); images.put(copy(job, uri, images.length())); }
                complete(job, response("ok").put("items", images));
            } catch(MediaFailure e) { fail(job, e.code, e.toString()); }
              catch(Exception e) { fail(job, "ReadFailed", e.toString()); }
        });
    }
    static InputStream open(Job job) throws Exception {
        return job.track("file".equals(job.request.optString("source"))
            ? new FileInputStream(job.request.getString("path"))
            : openUri(job,Uri.parse(job.request.getString("path"))));
    }
    static InputStream openUri(Job job, Uri uri) throws Exception {
        android.content.res.AssetFileDescriptor descriptor=context.getContentResolver().openAssetFileDescriptor(uri,"r",job.signal);
        if(descriptor==null) throw new FileNotFoundException("No readable image descriptor.");
        try { return job.track(descriptor.createInputStream()); }
        catch(Exception error) { try { descriptor.close(); } catch(Exception cleanup) { error.addSuppressed(cleanup); } throw error; }
    }
    static JSONObject copy(Job job, Uri uri, int index) throws Exception {
        ContentResolver resolver = context.getContentResolver(); String mime = resolver.getType(uri);
        String name = "image"; try (Cursor cursor = resolver.query(uri, new String[] {OpenableColumns.DISPLAY_NAME}, null, null, null, job.signal)) {
            if (cursor != null && cursor.moveToFirst()) name = cursor.getString(0);
        }
        if (mime == null || !mime.startsWith("image/")) throw new IOException("Provider returned a non-image type.");
        String extension = MimeTypeMap.getSingleton().getExtensionFromMimeType(mime);
        File file = new File(job.request.getString("output"), index + "." + (extension == null ? "bin" : extension));
        try(InputStream input = openUri(job, uri)) { write(job, input, file); }
        return new JSONObject().put("path", file.getAbsolutePath()).put("id", uri.toString()).put("name", name).put("mime", mime).put("size", file.length());
    }
    static void write(Job job, InputStream input, File file) throws Exception {
        if (input == null) throw new FileNotFoundException("No readable image stream.");
        FileOutputStream output;
        try { output=new FileOutputStream(file); }
        catch (IOException e) { throw new MediaFailure("WriteFailed",e.toString()); }
        try (FileOutputStream out = output) {
            byte[] buffer = new byte[131072]; int n; long size=0, limit=job.request.optLong("maxBytes");
            while ((n = input.read(buffer)) != -1) {
                job.check();
                if (limit>0 && n>limit-size) throw new MediaFailure("SizeLimitExceeded","Image exceeds export byte limit");
                try { out.write(buffer, 0, n); } catch (IOException e) { throw new MediaFailure("WriteFailed",e.toString()); }
                size+=n;
            }
            try { out.getFD().sync(); } catch (IOException e) { throw new MediaFailure("WriteFailed",e.toString()); }
            job.check();
        }
    }
    static JSONObject export(Job job) throws Exception {
        JSONObject item;
        if ("file".equals(job.request.optString("source"))) {
            File source = new File(job.request.getString("path"));
            String name = source.getName(); int dot = name.lastIndexOf('.');
            File output = new File(job.request.getString("output"), "image" + (dot < 0 ? ".bin" : name.substring(dot)));
            try(InputStream input = open(job)) { write(job, input, output); }
            item = new JSONObject().put("path", output.getAbsolutePath()).put("name", name).put("size", output.length());
        } else item = copy(job, Uri.parse(job.request.getString("path")), 0);
        return response("ok").put("items", new JSONArray().put(item));
    }
    static JSONObject preview(Job job) throws Exception {
        int edge = job.request.getInt("edge"); Bitmap bitmap;
        if (Build.VERSION.SDK_INT >= 28) {
            ImageDecoder.Source source = "file".equals(job.request.optString("source"))
                ? ImageDecoder.createSource(new File(job.request.getString("path")))
                : ImageDecoder.createSource(context.getContentResolver(), Uri.parse(job.request.getString("path")));
            bitmap = ImageDecoder.decodeBitmap(source, (decoder, info, src) -> {
                int w = info.getSize().getWidth(), h = info.getSize().getHeight();
                double scale = Math.min(1.0, edge / (double)Math.max(w, h));
                decoder.setTargetSize(Math.max(1, (int)(w * scale)), Math.max(1, (int)(h * scale)));
                decoder.setAllocator(ImageDecoder.ALLOCATOR_SOFTWARE);
            });
        } else {
            BitmapFactory.Options options = new BitmapFactory.Options(); options.inJustDecodeBounds = true;
            try (InputStream input = open(job)) { BitmapFactory.decodeStream(input, null, options); }
            if (options.outWidth <= 0 || options.outHeight <= 0) throw new IOException("Unsupported image format.");
            options.inJustDecodeBounds = false; options.inSampleSize = 1;
            while (Math.max(options.outWidth, options.outHeight) / options.inSampleSize > edge * 2) options.inSampleSize *= 2;
            try (InputStream input = open(job)) { bitmap = BitmapFactory.decodeStream(input, null, options); }
            if (bitmap == null) throw new IOException("Image decode failed.");
            int orientation = 1;
            if ("image/jpeg".equals(options.outMimeType)) {
                try (InputStream input = open(job)) { orientation = new ExifInterface(input).getAttributeInt(ExifInterface.TAG_ORIENTATION, 1); }
                catch (Exception error) { bitmap.recycle(); throw error; }
            }
            Matrix matrix = new Matrix();
            switch(orientation) {
                case 2: matrix.setScale(-1,1); break;
                case 3: matrix.setRotate(180); break;
                case 4: matrix.setScale(1,-1); break;
                case 5: matrix.setRotate(90); matrix.postScale(-1,1); break;
                case 6: matrix.setRotate(90); break;
                case 7: matrix.setRotate(-90); matrix.postScale(-1,1); break;
                case 8: matrix.setRotate(-90); break;
            }
            float scale = Math.min(1f, edge / (float)Math.max(bitmap.getWidth(),bitmap.getHeight())); matrix.postScale(scale, scale);
            Bitmap transformed = Bitmap.createBitmap(bitmap, 0, 0, bitmap.getWidth(), bitmap.getHeight(), matrix, true);
            if (transformed != bitmap) bitmap.recycle(); bitmap = transformed;
        }
        boolean jpeg="jpg".equals(job.request.optString("format"));
        if(jpeg) {
            Bitmap flattened=Bitmap.createBitmap(bitmap.getWidth(),bitmap.getHeight(),Bitmap.Config.ARGB_8888);
            try {
                Canvas canvas=new Canvas(flattened);
                int r=(int)Math.round(Math.max(0,Math.min(1,job.request.optDouble("backgroundR",1)))*255);
                int g=(int)Math.round(Math.max(0,Math.min(1,job.request.optDouble("backgroundG",1)))*255);
                int b=(int)Math.round(Math.max(0,Math.min(1,job.request.optDouble("backgroundB",1)))*255);
                canvas.drawColor(Color.rgb(r,g,b)); canvas.drawBitmap(bitmap,0,0,null);
            } catch(Exception error) { flattened.recycle(); bitmap.recycle(); throw error; }
            bitmap.recycle(); bitmap=flattened;
        }
        File file = new File(job.request.getString("output"), jpeg ? "image.jpg" : "preview.png");
        try {
            job.check();
            try(FileOutputStream out = new FileOutputStream(file)) {
                if (!bitmap.compress(jpeg?Bitmap.CompressFormat.JPEG:Bitmap.CompressFormat.PNG, jpeg?job.request.optInt("quality",90):100, out)) throw new IOException("PNG encode failed.");
            }
            return response("ok").put("items", new JSONArray().put(new JSONObject().put("path", file.getAbsolutePath()).put("width", bitmap.getWidth()).put("height", bitmap.getHeight())));
        } finally { bitmap.recycle(); }
    }
    static JSONObject library(Job job, boolean albums) throws Exception {
        String access = access(); if (!access.equals("Authorized") && !access.equals("Limited")) throw new SecurityException("Library access not granted.");
        String[] columns = {"_id", "_display_name", "mime_type", "_size", "width", "height", "date_modified", "bucket_id", "bucket_display_name"};
        String album = job.request.optString("album"); JSONArray result = new JSONArray();
        LinkedHashMap<String, JSONObject> groups = new LinkedHashMap<>();
        try(Cursor cursor = context.getContentResolver().query(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, columns,
            album.isEmpty() ? null : "bucket_id = ?", album.isEmpty() ? null : new String[] {album}, "date_added DESC, _id DESC", job.signal)) {
            if (cursor == null) throw new IOException("MediaStore query returned no cursor.");
            while(cursor.moveToNext()) {
                job.check(); String bucket = cursor.getString(7);
                if (albums) {
                    JSONObject group = groups.get(bucket);
                    if (group == null) { group = new JSONObject().put("id", bucket).put("name", cursor.getString(8)).put("count", 0); groups.put(bucket, group); }
                    group.put("count", group.getInt("count") + 1);
                } else result.put(new JSONObject().put("id", Uri.withAppendedPath(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, cursor.getString(0)).toString())
                    .put("name", cursor.getString(1)).put("mime", cursor.getString(2)).put("size", cursor.getLong(3))
                    .put("width", cursor.getInt(4)).put("height", cursor.getInt(5)).put("version", cursor.getString(6) + ":" + cursor.getLong(3)));
            }
        }
        if (albums) for(JSONObject group : groups.values()) result.put(group);
        return response("ok").put("items", result);
    }
    static JSONObject directory(Job job) throws Exception {
        Uri tree = Uri.parse(job.request.getString("path"));
        if (!DocumentsContract.isTreeUri(tree)) throw new IllegalArgumentException("A granted directory tree URI is required.");
        ArrayDeque<String> pending = new ArrayDeque<>(); pending.add(DocumentsContract.getTreeDocumentId(tree));
        HashSet<String> visited = new HashSet<>(); JSONArray items = new JSONArray();
        String[] columns = {DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.COLUMN_SIZE, DocumentsContract.Document.COLUMN_LAST_MODIFIED};
        while (!pending.isEmpty()) {
            job.check(); String parent = pending.remove(); if(!visited.add(parent)) continue;
            Uri children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, parent);
            try(Cursor cursor = context.getContentResolver().query(children, columns, null, null, null, job.signal)) {
                if(cursor == null) throw new IOException("Directory provider returned no cursor.");
                while(cursor.moveToNext()) {
                    job.check(); String id=cursor.getString(0), mime=cursor.getString(2);
                    if(DocumentsContract.Document.MIME_TYPE_DIR.equals(mime)) { if(job.request.optBoolean("recursive")) pending.add(id); }
                    else if(mime != null && mime.startsWith("image/")) items.put(new JSONObject()
                        .put("id", DocumentsContract.buildDocumentUriUsingTree(tree,id).toString()).put("name",cursor.getString(1))
                        .put("mime",mime).put("size",cursor.isNull(3) ? -1 : cursor.getLong(3)).put("source","directory")
                        .put("version",cursor.getString(4)+":"+cursor.getString(3)));
                }
            }
        }
        return response("ok").put("items",items);
    }
}
