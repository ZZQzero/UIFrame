"""Measure retained-file/baseline maintenance and source updates using the shipped engine.

Source updates measure the actual UPDATE text plus transaction delivery, not
the entire Accept/Finish command. No hardware-dependent pass threshold is used.
"""
import argparse
import json
from pathlib import Path
import re
import statistics
import tempfile
import time

from process_windows import Repository, IDENTITY, decode
from benchmark import Store

NATIVE = Path(__file__).resolve().parents[1]


def source_statement(prefix):
    matches = re.findall(r'commands\.emplace_back\("(' + re.escape(prefix) + r'[^"\n]*)"',
                         (NATIVE / 'src/repository.cpp').read_text())
    if len(matches) != 1:
        raise ValueError('Expected exactly one production statement: ' + prefix)
    return matches[0]


def measure(action):
    samples = []
    for i in range(12):
        start = time.perf_counter()
        action()
        elapsed = (time.perf_counter() - start) * 1000
        if i >= 2:
            samples.append(elapsed)
    return dict(p50_ms=statistics.median(samples), max_ms=max(samples))


def run(core, library, count, workload):
    with tempfile.TemporaryDirectory(prefix='ufbackup-maintenance-') as folder:
        root = Path(folder)
        repository = Repository(core, library, root, True)
        repository.close()
        db = Store(core, root / IDENTITY / 'catalog.sqlite', 1)
        started = time.perf_counter()
        if workload in ('baseline', 'sources'):
            db.sql("INSERT INTO scopes(id,source_id,library_id,index_generation,scope_revision,permission_generation,include_existing) VALUES('aa','album','bb',1,1,1,1)")
        if workload == 'baseline':
            db.sql("INSERT INTO scan_runs(id,scope_id,permission_generation,scope_revision,log_start,baseline,completed) VALUES('cc','aa',1,1,0,1,1)",
                   "UPDATE scopes SET active_baseline_id='cc',initialized=1,enabled=1 WHERE id='aa'")
        for offset in range(0, count, 200):
            page = f'WITH RECURSIVE page(n) AS (VALUES({offset+1}) UNION ALL SELECT n+1 FROM page WHERE n<{min(offset+200,count)}) '
            if workload == 'baseline':
                db.sql(page + "INSERT INTO scan_items(run_id,source_id,content_version,baseline_member) SELECT 'cc','asset:'||n,'v1',1 FROM page")
            elif workload == 'retained-files':
                db.sql(page + "INSERT INTO file_records SELECT printf('%032x',n),'payloads/'||printf('%032x',n)||'.payload',1024,zeroblob(32),3,'seed',NULL,1 FROM page",
                       page + "INSERT INTO tasks(id,batch_id,source_id,content_version,state,file_id,created_utc,updated_utc) SELECT printf('%032x',n),'seed','asset:'||n,'v1',3,printf('%032x',n),1,1 FROM page",
                       page + "INSERT INTO task_metadata(task_id,name,mime,idempotency_key) SELECT printf('%032x',n),'photo.jpg','image/jpeg',printf('%064x',n) FROM page")
            else:
                db.sql(page + "INSERT INTO discoveries(scope_id,source_id,content_version,disposition) SELECT 'aa',printf('asset:%09d',n),'v1',3 FROM page")
        result = dict(workload=workload, rows=count, seed_seconds=time.perf_counter()-started)
        if workload == 'sources':
            db.sql("INSERT INTO tasks(id,batch_id,source_id,content_version,state,created_utc,updated_utc) VALUES('target','batch','asset:target','v1',0,1,1)",
                   "INSERT INTO discoveries(scope_id,source_id,content_version,disposition) VALUES('aa','asset:target','v1',0)")
            statements = {
                'accept': source_statement('UPDATE discoveries SET disposition=2,task_id=').replace('?', "'batch'"),
                'finish': source_statement('UPDATE discoveries SET disposition=3,error=NULL').replace('?', "'target'")
            }
            for name, sql in statements.items():
                result[name + '_plan'] = decode(db.sql('EXPLAIN QUERY PLAN ' + sql))[-1]
                samples = []
                for i in range(12):
                    db.sql("UPDATE discoveries SET disposition=0 WHERE scope_id='aa' AND source_id='asset:target' AND content_version='v1'")
                    start = time.perf_counter()
                    db.sql(sql)
                    if i >= 2:
                        samples.append((time.perf_counter() - start) * 1000)
                result[name] = dict(p50_ms=statistics.median(samples), max_ms=max(samples))
            db.close()
        else:
            if workload == 'retained-files':
                result['reference_plan'] = decode(db.sql("EXPLAIN QUERY PLAN SELECT 1 FROM tasks WHERE file_id='00000000000000000000000000000001'"))[-1]
            db.close()
            repository = Repository(core, library, root, False)
            try:
                def prune():
                    assert repository.call(68, 200)[-1][0]['removed'] == 0, 'Protected metadata was deleted'
                result['prune_scans'] = measure(prune)
            finally:
                repository.close()
        return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--rows', nargs='+', type=int, default=[10000, 100000, 1000000])
    parser.add_argument('--workload', choices=['all', 'retained-files', 'baseline', 'sources'], default='all')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if any(count < 1 for count in args.rows):
        parser.error('Row counts must be positive')
    results = []
    workloads = ('retained-files', 'baseline', 'sources') if args.workload == 'all' else (args.workload,)
    for workload in workloads:
        for count in args.rows:
            result = run(args.core, args.repository, count, workload)
            results.append(result)
            print(json.dumps(result), flush=True)
    if args.output:
        args.output.write_text(json.dumps(results, indent=2) + '\n')
