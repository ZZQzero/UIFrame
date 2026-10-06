"""Measure the real BackupRepository open/page/summary commands with bounded seeding."""
import argparse
import ctypes as C
import json
from pathlib import Path
import resource
import sqlite3
import statistics
import struct
import subprocess
import sys
import tempfile
import time

PACKAGE=Path(__file__).resolve().parents[4]
sys.path.insert(0,str(PACKAGE/'Runtime/Sqlite/Native~/tests'))
from process_recovery import Store,commands

class Status(C.Structure):
    _fields_=[('size',C.c_uint32),('abi',C.c_uint32),('error',C.c_int32),('sqlite',C.c_int32),('committed',C.c_int32),('phase',C.c_uint32),('length',C.c_uint32),('message',C.c_char*256)]


def encode(values):
    data=struct.pack('<I',len(values))
    for value in values:
        if isinstance(value,int):data+=b'\x01'+struct.pack('<q',value)
        else:
            text=value.encode();data+=b'\x03'+struct.pack('<I',len(text))+text
    return data


def worker(core,repository,count):
    # Load the engine at its production path before loading its dependent plugin.
    native_core=C.CDLL(core,mode=C.RTLD_GLOBAL);native=C.CDLL(repository)
    native.ufbackup_open.argtypes=[C.c_char_p]*4+[C.c_int,C.POINTER(C.c_uint64),C.POINTER(Status)]
    native.ufbackup_call.argtypes=[C.c_uint64,C.c_uint32,C.c_char_p,C.c_uint32,C.c_void_p,C.c_uint32,C.POINTER(Status)]
    native.ufbackup_close.argtypes=[C.c_uint64,C.POINTER(Status)]
    with tempfile.TemporaryDirectory(prefix='ufbackup-benchmark-') as temporary:
        identity='a'*64;folder=Path(temporary)/identity;folder.mkdir();path=folder/'catalog.sqlite'
        db=Store(core,path,0);pending='';schema=[]
        for line in (PACKAGE/'Runtime/MediaBackup/Native~/schema/catalog.sql').read_text().splitlines(True):
            pending+=line
            if sqlite3.complete_statement(pending):schema.append(pending);pending=''
        db.sql(*schema)
        db.sql("INSERT INTO store_settings(singleton,store_id,server,account,paused) VALUES(1,'"+identity+"','https://test.invalid','test',0)")
        started=time.perf_counter()
        for offset in range(0,count,200):
            page=f'WITH RECURSIVE page(n) AS (VALUES({offset+1}) UNION ALL SELECT n+1 FROM page WHERE n<{min(offset+200,count)}) '
            db.sql(page+"INSERT INTO file_records SELECT printf('%032x',n),'payloads/'||printf('%032x',n)||'.payload',1024,zeroblob(32),3,'seed',NULL,1 FROM page",
                   page+"INSERT INTO tasks(id,batch_id,source_id,content_version,state,file_id,created_utc,updated_utc) SELECT printf('%032x',n),'seed','file:'||n,'v1',3,printf('%032x',n),1,1 FROM page",
                   page+"INSERT INTO task_metadata(task_id,name,mime) SELECT printf('%032x',n),'photo.png' ,'image/png' FROM page")
        seed=time.perf_counter()-started;db.close()
        h=C.c_uint64();s=Status(size=C.sizeof(Status),abi=2);start=time.perf_counter()
        code=native.ufbackup_open(temporary.encode(),identity.encode(),b'https://test.invalid',b'test',0,C.byref(h),C.byref(s));assert code==0,s.message
        opened=(time.perf_counter()-start)*1000
        output=C.create_string_buffer(1024*1024)
        def measure(command,args):
            data=encode(args);samples=[]
            for i in range(120):
                s=Status(size=C.sizeof(Status),abi=2);start=time.perf_counter()
                code=native.ufbackup_call(h,command,data,len(data),output,len(output),C.byref(s));assert code==0,s.message
                if i>=20:samples.append((time.perf_counter()-start)*1000)
            return dict(p50_ms=statistics.median(samples),p95_ms=sorted(samples)[94],result_bytes=s.length)
        result=dict(rows=count,seed_seconds=seed,open_ms=opened,page=measure(6,[count//2,count,3,100]),summary=measure(8,[]),max_rss_bytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss)
        s=Status(size=C.sizeof(Status),abi=2);assert native.ufbackup_close(h,C.byref(s))==0
        result['database_bytes']=path.stat().st_size
        print(json.dumps(result))


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--core',required=True);parser.add_argument('--repository',required=True);parser.add_argument('--rows',nargs='+',type=int,default=[10000,100000]);parser.add_argument('--worker',type=int);parser.add_argument('--output',type=Path);args=parser.parse_args()
    if args.worker is not None:return worker(args.core,args.repository,args.worker)
    results=[]
    for count in args.rows:
        process=subprocess.run([sys.executable,__file__,'--core',args.core,'--repository',args.repository,'--worker',str(count)],text=True,capture_output=True,check=True)
        value=json.loads(process.stdout);results.append(value);print(json.dumps(value),flush=True)
    if args.output:args.output.write_text(json.dumps(results,indent=2)+'\n')

if __name__=='__main__':main()
