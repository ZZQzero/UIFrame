"""Check production history pages against a model, with optional host timing.

Synthetic fixtures use the shipped SQLite core; reads use the real backup ABI.
No source SQL is copied into the expected model. No real photos are touched.
"""
import argparse
import json
from pathlib import Path
import random
import statistics
import tempfile
import time

from process_windows import Repository, IDENTITY
from flow_query_benchmark import QueryStore


def regression(core, library):
    with tempfile.TemporaryDirectory(prefix='ufbackup-retention-test-') as temporary:
        root = Path(temporary)
        repo = Repository(core, library, root, True)
        assert repo.call(50, 0, 1, 0, 200)[-1] == []
        repo.close()
        db = QueryStore(core, root / IDENTITY / 'catalog.sqlite', 1)
        rng = random.Random(710)
        model = []
        for n in range(1, 401):
            state, updated, owner = rng.choice([0, 3, 4, 6, 7, 8]), rng.randrange(10), n % 7
            key = f'{n:032x}'
            db.sql(f"INSERT INTO file_records VALUES('{key}','payloads/{key}.payload',4,NULL,{1 if owner == 1 else 3},'seed',NULL,1)",
                   f"INSERT INTO tasks(id,batch_id,source_id,content_version,state,file_id,created_utc,updated_utc) VALUES('{key}','seed','file:{n}','v1',{state},'{key}',1,{updated})")
            if owner in (2, 3, 4, 5, 6):
                db.sql(f"INSERT INTO task_attempts(task_id,generation,submission_state,execution_state,server_outcome,payload_released,credential_released) VALUES('{key}',1,0,0,0,{0 if owner == 2 else 1},{0 if owner == 3 else 1})")
            if owner in (4, 5, 6):
                db.sql(f"INSERT INTO control_requests(id,kind,executor,state,relative_path,body,byte_count,not_before_utc,created_utc,released,cleanup_state) VALUES('{key}',0,0,4,'controls/{key}','x',1,0,1,{0 if owner == 4 else 1},{2 if owner == 6 else 3})",
                       f"INSERT INTO control_items VALUES('{key}','{key}',1,1)")
            model.append(dict(sequence=n, id=key, updated_utc=updated, state=state, eligible=owner in (0, 6)))
        db.close()
        repo = Repository(core, library, root, False)
        terminal = [r for r in model if r['state'] in (3, 8)]
        checks = 0
        for keep in (0, 1, 50, len(terminal), 1000000):
            cutoff = terminal[-keep-1]['sequence'] if len(terminal) > keep else 0
            for before in (0, 1, 5, 10):
                for after in (0, 127, 399, 400):
                    expected = [{k: r[k] for k in ('sequence', 'id', 'updated_utc')} for r in terminal
                                if r['eligible'] and r['sequence'] > after and
                                (r['updated_utc'] < before or r['sequence'] <= cutoff)]
                    for limit in (1, 7, 200):
                        actual = repo.call(50, after, before, keep, limit)[-1]
                        assert actual == expected[:limit], (keep, before, after, limit, actual, expected[:limit])
                        checks += 1
                cursor, walked = 0, []
                while True:
                    page = repo.call(50, cursor, before, keep, 7)[-1]
                    if not page:
                        break
                    walked.extend(page)
                    cursor = page[-1]['sequence']
                expected = [{k: r[k] for k in ('sequence', 'id', 'updated_utc')} for r in terminal
                            if r['eligible'] and (r['updated_utc'] < before or r['sequence'] <= cutoff)]
                assert walked == expected, (keep, before)
        repo.close()
        return dict(page_checks=checks, complete_walks=20, empty_catalog=True)


def measure(action):
    samples = []
    for index in range(12):
        start = time.perf_counter()
        rows = action()[-1]
        elapsed = (time.perf_counter() - start) * 1000
        if index >= 2:
            samples.append(elapsed)
    return dict(p50_ms=statistics.median(samples), max_ms=max(samples), rows=len(rows))


def benchmark(core, libraries, sizes, root):
    repo = Repository(core, libraries[-1][1], root, True)
    repo.close()
    previous, results = 0, []
    for count in sizes:
        db = QueryStore(core, root / IDENTITY / 'catalog.sqlite', 1)
        for offset in range(previous, count, 200):
            page = f'WITH RECURSIVE page(n) AS (VALUES({offset+1}) UNION ALL SELECT n+1 FROM page WHERE n<{min(offset+200,count)}) '
            db.sql(page + "INSERT INTO file_records SELECT printf('%032x',n),'payloads/'||printf('%032x',n)||'.payload',1024,zeroblob(32),3,'seed',NULL,1 FROM page",
                   page + "INSERT INTO tasks(id,batch_id,source_id,content_version,state,file_id,created_utc,updated_utc) SELECT printf('%032x',n),'seed','file:'||n,'v1',CASE WHEN n%2=0 THEN 3 ELSE 8 END,printf('%032x',n),1,n%10 FROM page")
        db.close()
        for name, library in libraries:
            repo = Repository(core, library, root, False)
            cases = dict(count_only=(0, 0, 10000, 100), count_and_age=(0, 5, 10000, 100),
                         retained_only=(0, 0, count, 100), age_only=(0, 5, count, 100),
                         tail=(max(0, count-100), 5, 10000, 100))
            row = dict(history_rows=count, implementation=name,
                       cases={key: measure(lambda args=args: repo.call(50, *args)) for key, args in cases.items()})
            repo.close()
            results.append(row)
            print(json.dumps(row), flush=True)
        previous = count
    return results


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--baseline')
    parser.add_argument('--rows', type=int, nargs='+')
    parser.add_argument('--fixture', type=Path, help='New disposable directory, retained for follow-up benchmarks.')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = dict(regression=regression(args.core, args.repository))
    print(json.dumps(result), flush=True)
    if args.rows:
        libraries = ([('before', args.baseline)] if args.baseline else []) + [('after', args.repository)]
        if args.fixture:
            args.fixture.mkdir()
            result['benchmark'] = benchmark(args.core, libraries, args.rows, args.fixture)
        else:
            with tempfile.TemporaryDirectory(prefix='ufbackup-retention-bench-') as directory:
                result['benchmark'] = benchmark(args.core, libraries, args.rows, Path(directory))
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + '\n')
