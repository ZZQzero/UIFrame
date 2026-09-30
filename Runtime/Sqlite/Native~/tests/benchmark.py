"""Host-only catalog/keyset baseline. Measures pinned core + C ABI, not Unity/IL2CPP.
Every generated write affects <= 200 rows; no legacy-format input is involved.
"""
import argparse
import json
from pathlib import Path
import resource
import statistics
import subprocess
import sys
import tempfile
import sqlite3
import time
from process_recovery import Store, commands

PACKAGE = Path(__file__).resolve().parents[4]

def worker(library, count):
    with tempfile.TemporaryDirectory(prefix='uiframe-catalog-benchmark-') as directory:
        path = Path(directory)/'catalog.sqlite'
        store = Store(library, path, 0)
        schema = (PACKAGE/'Runtime/MediaBackup/Native~/schema/catalog.sql').read_text()
        statements=[];pending=''
        for line in schema.splitlines(True):
            pending+=line
            if sqlite3.complete_statement(pending):statements.append(pending);pending=''
        store.sql(*statements)
        started = time.perf_counter()
        for offset in range(0, count, 200):
            store.sql(f'''WITH RECURSIVE page(n) AS (VALUES({offset+1}) UNION ALL
                SELECT n+1 FROM page WHERE n<{min(offset+200,count)})
                INSERT INTO tasks(id,batch_id,source_id,content_version,state,created_utc,updated_utc)
                SELECT 'task-'||n,'batch','source-'||n,'v1',n%9,1,1 FROM page''')
        populate_seconds = time.perf_counter()-started
        store.close()
        started = time.perf_counter()
        store = Store(library, path, 1)
        open_ms = (time.perf_counter()-started)*1000
        sql = f'SELECT sequence,state FROM tasks WHERE state=3 AND sequence>{count//2} AND sequence<={count} ORDER BY sequence LIMIT 100'
        plan, _ = store.wait(store.submit(3,commands('EXPLAIN QUERY PLAN '+sql)))
        assert b'tasks_state_page' in plan, 'State pagination did not use its composite index'
        samples = []
        for iteration in range(220):
            started = time.perf_counter()
            page, _ = store.wait(store.submit(3,commands(sql)))
            assert len(page) < 4096
            if iteration>=20: samples.append((time.perf_counter()-started)*1000)
        store.close()
        samples.sort()
        print(json.dumps(dict(rows=count,populate_seconds=populate_seconds,open_ms=open_ms,
            query_p50_ms=statistics.median(samples),query_p95_ms=samples[189],query_p99_ms=samples[197],
            max_rss_bytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss,
            database_bytes=path.stat().st_size,query_plan='tasks_state_page',
            conditions='macOS process reopen; warm filesystem; 100 rows; includes Python C ABI bridge; no Unity')))

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('library')
    parser.add_argument('--rows',type=int,nargs='+',default=[10000,100000,1000000])
    parser.add_argument('--worker',type=int)
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    if args.worker is not None: return worker(args.library,args.worker)
    results=[]
    for count in args.rows:
        run=subprocess.run([sys.executable,__file__,args.library,'--worker',str(count)],
                           check=True,text=True,capture_output=True)
        value=json.loads(run.stdout)
        results.append(value)
        print(json.dumps(value),flush=True)
    if args.output: args.output.write_text(json.dumps(results,indent=2)+'\n')

if __name__=='__main__':main()
