"""Validate proposed photo schemas with the SAME native engine; no production wiring."""
from pathlib import Path
import sys
import tempfile
from process_recovery import Store

PACKAGE=Path(__file__).resolve().parents[4]

def main(library):
    with tempfile.TemporaryDirectory(prefix='uiframe-schema-') as temporary:
        for name,relative in [('catalog','Runtime/MediaBackup/Native~/schema/catalog.sql'),('library','Runtime/MediaStorage/Schema~/library.sql')]:
            db=Store(library,Path(temporary)/(name+'.sqlite'),0)
            sql=(PACKAGE/relative).read_text()
            db.sql(*[s.strip() for s in sql.split(';') if s.strip()])
            assert db.scalar('PRAGMA user_version')==1
            if name=='catalog':
                db.sql("INSERT INTO tasks(id,batch_id,source_id,content_version,state,created_utc,updated_utc) VALUES('t1','b','source','v1',3,1,1)",
                       "INSERT INTO operations(id,parameter_fingerprint,action,phase,upper_sequence,created_utc,updated_utc) VALUES('op',zeroblob(32),4,1,1,1,1)",
                       "INSERT INTO operation_items VALUES('op','t1',0,NULL)")
                first=db.scalar('SELECT sequence FROM tasks')
                db.sql("DELETE FROM tasks WHERE id='t1'")
                assert db.scalar('SELECT count(*) FROM operation_items')==1,'history deletion removed operation result'
                db.sql("INSERT INTO tasks(id,batch_id,source_id,content_version,state,created_utc,updated_utc) VALUES('t2','b','source','v2',3,1,1)")
                assert db.scalar('SELECT sequence FROM tasks')>first,'sequence reused after cleanup'
            db.close()
            print(name+': schema creation, identity and constraints passed')

if __name__=='__main__':main(sys.argv[1])
