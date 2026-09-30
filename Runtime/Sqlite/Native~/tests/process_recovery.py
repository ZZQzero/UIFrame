"""Exercise the public C ABI in separate processes; kill between acceptance and result.
Uses the pinned engine to validate reopened data, never a second SQLite backend.
"""
import ctypes as c
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import time

class Completion(c.Structure):
    _fields_=[('size',c.c_uint32),('abi',c.c_uint32),('operation',c.c_uint64),('database',c.c_uint64),
              ('error',c.c_int32),('sqlite_code',c.c_int32),('committed',c.c_int32),('reserved',c.c_int32),
              ('data',c.c_void_p),('data_size',c.c_uint64),('message',c.c_char*256)]

def text(value):
    value=value.encode('utf-8')
    return struct.pack('<I',len(value))+value

def commands(*sql):
    return struct.pack('<I',len(sql))+b''.join(text(s)+struct.pack('<qI',-1,0) for s in sql)

class Store:
    def __init__(self,library,path,mode):
        self.library=c.CDLL(library)
        self.library.ufsqlite_client_create.argtypes=[c.c_uint32,c.POINTER(c.c_uint64)]
        self.library.ufsqlite_client_release.argtypes=[c.c_uint64]
        self.library.ufsqlite_submit.argtypes=[c.c_uint64,c.c_uint64,c.c_uint32,c.c_void_p,c.c_uint64,c.c_uint32,c.c_uint32,c.c_uint32,c.POINTER(c.c_uint64)]
        self.library.ufsqlite_wait.argtypes=[c.c_uint64,c.POINTER(Completion),c.c_uint32,c.c_uint32,c.POINTER(c.c_uint32)]
        self.library.ufsqlite_release_result.argtypes=[c.c_uint64,c.c_uint64]
        self.client=c.c_uint64()
        assert self.library.ufsqlite_client_create(2,c.byref(self.client))==0
        self.database=0
        _,self.database=self.wait(self.submit(1,struct.pack('<I',mode)+text(str(path))+struct.pack('<QQ',32*1024*1024,128*1024*1024)))
    def submit(self,kind,payload=b''):
        op=c.c_uint64()
        rc=self.library.ufsqlite_submit(self.client,self.database,kind,payload,len(payload),200,4096,5000,c.byref(op))
        assert rc==0,rc
        return op.value
    def wait(self,operation):
        result=Completion();count=c.c_uint32();deadline=time.monotonic()+10
        while time.monotonic()<deadline:
            assert self.library.ufsqlite_wait(self.client,c.byref(result),1,100,c.byref(count))==0
            if count.value:
                assert result.operation==operation
                data=c.string_at(result.data,result.data_size)
                code,message,db=result.error,result.message,result.database
                assert self.library.ufsqlite_release_result(self.client,operation)==0
                assert code==0,(code,message)
                return data,db
        raise TimeoutError('native operation did not complete')
    def sql(self,*sql): return self.wait(self.submit(4,commands(*sql)))[0]
    def scalar(self,sql):
        data,_=self.wait(self.submit(3,commands(sql)))
        tables,cols,rows,_=struct.unpack_from('<IIIq',data)
        assert (tables,cols,rows)==(1,1,1)
        n=struct.unpack_from('<I',data,20)[0];offset=24+n
        assert data[offset]==1
        return struct.unpack_from('<q',data,offset+1)[0]
    def close(self):
        self.wait(self.submit(5))
        assert self.library.ufsqlite_client_release(self.client)==0


def worker(library,path,stage):
    db=Store(library,path,1)
    operation=db.submit(4,commands("INSERT INTO receipts VALUES('operation-1')",
        'WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<100000) INSERT INTO values_table SELECT x FROM r'))
    if stage=='committed': db.wait(operation)
    print('ready',flush=True)
    # A parent owns termination; this is a process-kill test, not a product retry.
    sys.stdin.read(1)
    os._exit(0)


def main(library):
    with tempfile.TemporaryDirectory(prefix='uiframe-sqlite-crash-') as temporary:
        for index,delay in enumerate([0,0.001,0.01,0.05,None]):
            path=Path(temporary)/f'{index}.sqlite'
            db=Store(library,path,0)
            db.sql('CREATE TABLE receipts(id TEXT PRIMARY KEY)','CREATE TABLE values_table(value INTEGER PRIMARY KEY)')
            db.close()
            process=subprocess.Popen([sys.executable,__file__,'worker',library,str(path),'committed' if delay is None else 'accepted'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True)
            assert process.stdout.readline().strip()=='ready'
            if delay: time.sleep(delay)
            process.kill();process.wait(timeout=5)
            db=Store(library,path,1)
            receipt=db.scalar('SELECT count(*) FROM receipts');rows=db.scalar('SELECT count(*) FROM values_table')
            assert (receipt,rows) in [(0,0),(1,100000)],(receipt,rows)
            if delay is None: assert (receipt,rows)==(1,100000),'Acknowledged COMMIT lost'
            db.close()
            print(json.dumps(dict(stage='committed' if delay is None else 'accepted',delay=delay,receipt=receipt,rows=rows)))
    print('Process interruption: 5/5 recovery cases passed.')

if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='worker': worker(*sys.argv[2:])
    else: main(sys.argv[1])
