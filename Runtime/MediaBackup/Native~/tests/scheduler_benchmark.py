"""Measure live-attempt queries against released history using the published ABI."""
import argparse
import json
from pathlib import Path
import re
import statistics
import tempfile
import time
from flow_query_benchmark import QueryStore
from process_windows import Repository, IDENTITY


def measure(action):
    elapsed = []
    for sample in range(8):
        start = time.perf_counter()
        rows = action()
        duration = (time.perf_counter() - start) * 1000
        if sample >= 2:
            elapsed.append(duration)
    return dict(rows=len(rows), p50_ms=statistics.median(elapsed), max_ms=max(elapsed))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--rows', nargs='+', type=int, default=[10000, 100000, 1000000])
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if any(count < 1 for count in args.rows):
        parser.error('Positive history sizes required')
    source = (Path(__file__).resolve().parents[1] / 'src/repository.cpp').read_text()
    def literals(name):
        return ''.join(re.findall(r'"([^"\n]*)"', source.split('const std::string ' + name + '=')[1].split(';')[0]))
    base = literals('TaskFields') + literals('AttemptColumns')
    # Same predicate as the command; execute the command itself as the timed path.
    query = base + 'WHERE t.sequence>0 AND a.executor=1 AND (a.payload_released=0 OR a.credential_released=0) AND (a.submission_state>0 OR a.execution_state>=2) ORDER BY t.sequence LIMIT 100'
    results = []
    for count in args.rows:
        with tempfile.TemporaryDirectory(prefix='ufbackup-scheduler-') as temporary:
            root = Path(temporary)
            repository = Repository(args.core, args.repository, root, True); repository.close()
            database = QueryStore(args.core, root / IDENTITY / 'catalog.sqlite', 1)
            started = time.perf_counter()
            for offset in range(0, count, 200):
                page = f'WITH RECURSIVE page(n) AS (VALUES({offset+1}) UNION ALL SELECT n+1 FROM page WHERE n<{min(offset+200,count)}) '
                database.sql(
                    page + "INSERT INTO file_records SELECT printf('%032x',n),'payloads/'||printf('%032x',n)||'.payload',4,zeroblob(32),3,'seed',NULL,1 FROM page",
                    page + "INSERT INTO tasks(id,batch_id,source_id,content_version,state,file_id,current_generation,created_utc,updated_utc) SELECT printf('%032x',n),'seed','file:'||n,'v1',3,printf('%032x',n),1,1,1 FROM page",
                    page + "INSERT INTO task_metadata(task_id,name,mime,idempotency_key) SELECT printf('%032x',n),'photo.jpg','image/jpeg',printf('%064x',n) FROM page",
                    page + "INSERT INTO task_attempts(task_id,generation,idempotency_key,submission_state,execution_state,server_outcome,payload_released,credential_released,executor) SELECT printf('%032x',n),1,printf('%032x',n)||':1',2,2,1,1,1,1 FROM page")
            result = dict(history_rows=count, seed_seconds=time.perf_counter()-started)
            result['plan'] = database.query('EXPLAIN QUERY PLAN ' + query)
            assert any('attempts_unreleased' in row['detail'] for row in result['plan'])
            assert not any(re.search(r'\bSCAN t\b', row['detail']) for row in result['plan'])
            database.close()
            repository = Repository(args.core, args.repository, root, False)
            for command, name in ((75, 'schedulable'), (23, 'attempts')):
                result[name + '_empty'] = measure(lambda: repository.call(command, 0, 1, 100)[-1])
                assert result[name + '_empty']['rows'] == 0
            repository.close()
            database = QueryStore(args.core, root / IDENTITY / 'catalog.sqlite', 1)
            database.sql(f'UPDATE tasks SET state=1 WHERE sequence={count}',
                f"UPDATE task_attempts SET payload_released=0,credential_released=0,execution_state=0,server_outcome=0 WHERE task_id=printf('%032x',{count})")
            database.close()
            repository = Repository(args.core, args.repository, root, False)
            for command, name in ((75, 'schedulable'), (23, 'attempts')):
                result[name + '_one_at_tail'] = measure(lambda: repository.call(command, 0, 1, 100)[-1])
                assert result[name + '_one_at_tail']['rows'] == 1
                assert repository.call(command, count, 1, 100)[-1] == []
            repository.close()
            results.append(result)
            print(json.dumps(result), flush=True)
    args.output.write_text(json.dumps(results, indent=2) + '\n')


if __name__ == '__main__':
    main()
