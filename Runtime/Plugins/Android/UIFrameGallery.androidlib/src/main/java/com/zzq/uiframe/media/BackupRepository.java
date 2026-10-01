package com.zzq.uiframe.media;

import android.content.Context;
import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Typed transport only. SQL and task state transitions are in the shared C++ repository. */
final class BackupRepository implements AutoCloseable {
    static { System.loadLibrary("uiframe_sqlite"); System.loadLibrary("uiframe_backup"); }
    static final int INFO=1, TASK=7, SUBMITTED=10, FINISH=12, RELEASE=13, START=22, ATTEMPTS=23,HANDOFF=74,SCHEDULABLE=75,
        CLEANUP_PAGE=18, CLEANUP_RUN=19, ATTEMPT=60, RESTART=63;
    public static final class Failure extends RuntimeException {
        private static final long serialVersionUID=1L;
        final int code, sqlite, phase, committed;
        public Failure(int code,int sqlite,int phase,int committed,String message) {
            super(message);this.code=code;this.sqlite=sqlite;this.phase=phase;this.committed=committed;
        }
    }
    private static native long nativeOpen(String root,String repository);
    private static native int nativeClose(long handle);
    private static native byte[] nativeCall(long handle,int command,byte[] input);
    private long handle;
    final String id;
    static File root(Context context) throws IOException {
        File root=new File(context.getNoBackupFilesDir(),"UIFrameBackup");
        if(!root.isDirectory() && !root.mkdirs()) throw new IOException("Cannot create backup repository root");return root;
    }
    BackupRepository(Context context,String id) throws IOException {
        if(id==null || !id.matches("[a-f0-9]{64}")) throw new IllegalArgumentException("Invalid backup repository ID");
        this.id=id;handle=nativeOpen(root(context).getCanonicalPath(),id);
    }
    private static void number(ByteArrayOutputStream bytes,long n,int size) {
        for(int i=0;i<size;i++) bytes.write((int)(n>>>(8*i))&255);
    }
    private static void string(ByteArrayOutputStream bytes,byte[] value) throws IOException {
        number(bytes,value.length,4);bytes.write(value);
    }
    synchronized List<Row> call(int command,Object... values) throws IOException {
        if(handle==0) throw new IllegalStateException("Repository attachment closed");
        ByteArrayOutputStream bytes=new ByteArrayOutputStream();number(bytes,values.length,4);
        for(Object value:values) {
            if(value==null) bytes.write(0);
            else if(value instanceof Boolean || value instanceof Number) {bytes.write(1);number(bytes,value instanceof Boolean?((Boolean)value?1:0):((Number)value).longValue(),8);}
            else if(value instanceof String) {bytes.write(3);string(bytes,((String)value).getBytes(StandardCharsets.UTF_8));}
            else if(value instanceof byte[]) {bytes.write(4);string(bytes,(byte[])value);}
            else throw new IllegalArgumentException("Unsupported repository argument");
            if(bytes.size()>1024*1024) throw new IllegalArgumentException("Repository input exceeds 1 MiB");
        }
        ByteBuffer input=ByteBuffer.wrap(nativeCall(handle,command,bytes.toByteArray())).order(ByteOrder.LITTLE_ENDIAN);
        if(!input.hasRemaining()) return Collections.emptyList();
        int tables=input.getInt();if(tables<0 || tables>200) throw new IOException("Invalid repository table count");
        List<Row> result=Collections.emptyList();int total=0;
        for(int t=0;t<tables;t++) {
            int columns=input.getInt(),rows=input.getInt();input.getLong();
            if(columns<0 || columns>1024 || rows<0 || (total+=rows)>200) throw new IOException("Invalid repository dimensions");
            List<String> names=new ArrayList<>(columns);for(int c=0;c<columns;c++) names.add(new String(blob(input),StandardCharsets.UTF_8));
            result=new ArrayList<>(rows);
            for(int r=0;r<rows;r++) {
                Object[] items=new Object[columns];
                for(int c=0;c<columns;c++) switch(input.get()) {
                    case 0:break;case 1:items[c]=input.getLong();break;case 2:items[c]=input.getDouble();break;
                    case 3:items[c]=new String(blob(input),StandardCharsets.UTF_8);break;case 4:items[c]=blob(input);break;
                    default:throw new IOException("Invalid repository value");
                }
                result.add(new Row(names,items));
            }
        }
        if(input.hasRemaining()) throw new IOException("Trailing repository bytes");return result;
    }
    private static byte[] blob(ByteBuffer input) throws IOException {
        int count=input.getInt();if(count<0 || count>input.remaining()) throw new IOException("Invalid repository field length");
        byte[] value=new byte[count];input.get(value);return value;
    }
    static final class Row {
        private final List<String> names;private final Object[] values;
        Row(List<String> names,Object[] values){this.names=names;this.values=values;}
        Object get(String name){int index=names.indexOf(name);if(index<0) throw new IllegalArgumentException("Unknown repository column: "+name);return values[index];}
        String text(String name){return (String)get(name);}
        long number(String name){Object value=get(name);return value==null?0:((Number)value).longValue();}
        boolean flag(String name){return number(name)!=0;}
    }
    @Override public synchronized void close(){if(handle==0)return;long old=handle;handle=0;nativeClose(old);}
}
