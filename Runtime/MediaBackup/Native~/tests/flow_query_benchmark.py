"""Check cleanup, Limited-membership and permission-update SQL on the shipped engine.

Extract the production SQL, seed increasing history sizes, and record query plans
and timings. These host measurements are not mobile performance acceptance.
"""
import argparse
import ctypes as C
import json
from pathlib import Path
import re
import sqlite3
import statistics
import tempfile
import time

from benchmark import Store, commands, PACKAGE
from process_windows import Repository, IDENTITY, decode


class QueryStore(Store):
    def query(self, sql):
        payload = commands(sql)
        operation = C.c_uint64()
        assert self.library.ufsqlite_submit(self.client, self.database, 3, payload, len(payload),
                                           200, 128 * 1024, 5000, C.byref(operation)) == 0
        return decode(self.wait(operation.value)[0])[-1]


def statement(path, prefix):
    matches = re.findall(r'"(' + re.escape(prefix) + r'[^"\n]*)"', path.read_text())
    assert len(matches) == 1, (path, prefix, len(matches))
    return matches[0]


def measure(action):
    samples = []
    for iteration in range(30):
        started = time.perf_counter()
        action()
        if iteration >= 5:
            samples.append((time.perf_counter() - started) * 1000)
    return dict(p50_ms=statistics.median(samples), max_ms=max(samples))


def run(core, library, count):
    with tempfile.TemporaryDirectory(prefix='ufbackup-flow-query-') as temporary:
        root = Path(temporary)
        repository = Repository(core, library, root, True)
        repository.close()
        db = QueryStore(core, root / IDENTITY / 'catalog.sqlite', 1)
        for offset in range(0, count, 200):
            page = f'WITH RECURSIVE page(n) AS (VALUES({offset + 1}) UNION ALL SELECT n+1 FROM page WHERE n<{min(offset + 200, count)}) '
            db.sql(page + "INSERT INTO file_records SELECT printf('%032x',n),'payloads/'||printf('%032x',n)||'.payload',4,NULL,CASE WHEN n%2=0 THEN 4 ELSE 1 END,'fixture','cleanup failed',1 FROM page")
        cleanup = statement(PACKAGE / 'Runtime/MediaBackup/Native~/src/repository.cpp', 'SELECT f.id,f.byte_count,f.cleanup_error,')
        cleanup = cleanup.replace('?', "''", 1).replace('?', '100', 1)
        cleanup_plan = db.query('EXPLAIN QUERY PLAN ' + cleanup)
        assert any('files_cleanup (state=? AND id>?)' in row['detail'] for row in cleanup_plan), cleanup_plan
        assert any('tasks_file' in row['detail'] for row in cleanup_plan), cleanup_plan
        assert not any('TEMP B-TREE' in row['detail'] for row in cleanup_plan), cleanup_plan
        db.close()
        repository = Repository(core, library, root, False)
        first = repository.call(76, '', 100)[-1]
        second = repository.call(76, first[-1]['id'], 100)[-1]
        assert len(first) == len(second) == 100
        assert first[0]['id'] == f'{2:032x}' and second[-1]['id'] == f'{400:032x}'
        tail = repository.call(76, f'{count - 200:032x}', 100)[-1]
        assert len(tail) == 100 and tail[-1]['id'] == f'{count - count % 2:032x}'
        assert not repository.call(76, tail[-1]['id'], 100)[-1]
        result = dict(rows=count, failed_files=count // 2, cleanup_plan=cleanup_plan,
                      cleanup_page=measure(lambda: repository.call(76, '', 100)))
        repository.close()

        db = QueryStore(core, root / 'library.sqlite', 0)
        pending, schema = '', []
        for line in (PACKAGE / 'Runtime/MediaStorage/Schema~/library.sql').read_text().splitlines(True):
            pending += line
            if sqlite3.complete_statement(pending):
                schema.append(pending)
                pending = ''
        db.sql(*schema)
        db.sql("INSERT INTO library_scopes(id,provider,source_identity,revision,permission_generation,access_state,requires_reconcile) VALUES('aa','PhotoLibrary','',1,0,1,0)")
        for offset in range(0, count, 200):
            page = f'WITH RECURSIVE page(n) AS (VALUES({offset + 1}) UNION ALL SELECT n+1 FROM page WHERE n<{min(offset + 200, count)}) '
            db.sql(page + "INSERT INTO assets(source_id,content_version,name,mime,width,height) SELECT printf('asset:%09d',n),'v1','photo.jpg','image/jpeg',1,1 FROM page",
                   page + "INSERT INTO scope_assets(scope_id,source_id,updated_seq,present) SELECT 'aa',printf('asset:%09d',n),0,1 FROM page")
        revoke = statement(PACKAGE / 'Runtime/MediaStorage/LibraryRepository.cs', 'UPDATE library_scopes SET revision=revision+1,permission_generation=permission_generation+1,requires_reconcile=1,access_state=')
        for value in ('1', "'aa'", '3', '2'):
            revoke = revoke.replace('?', value, 1)
        revoke_plan = decode(db.sql('EXPLAIN QUERY PLAN ' + revoke))[-1]
        assert len(revoke_plan) == 1 and 'SEARCH library_scopes USING INDEX sqlite_autoindex_library_scopes_1 (id=?)' in revoke_plan[0]['detail'], revoke_plan
        db.sql("INSERT INTO library_scopes(id,provider,source_identity,revision,permission_generation,access_state,requires_reconcile) VALUES('bb','PhotoLibrary','other',1,0,3,0)")
        for access in (3, 2):
            db.sql(f"UPDATE library_scopes SET revision=1,permission_generation=0,access_state={access},requires_reconcile=0,access_fingerprint='fixture' WHERE id='aa'")
            db.sql(revoke, revoke)
            state = db.query("SELECT revision,permission_generation,access_state,requires_reconcile,access_fingerprint FROM library_scopes WHERE id='aa'")[0]
            assert state == dict(revision=2, permission_generation=1, access_state=1, requires_reconcile=1, access_fingerprint=None), state
        db.sql("UPDATE library_scopes SET access_state=4 WHERE id='aa'", revoke)
        assert db.query("SELECT access_state,permission_generation FROM library_scopes WHERE id='aa'")[0] == dict(access_state=4, permission_generation=1)
        assert db.query("SELECT permission_generation FROM library_scopes WHERE id='bb'")[0]['permission_generation'] == 0
        result['permission_revoke_plan'] = revoke_plan
        membership = statement(PACKAGE / 'Runtime/MediaStorage/LibraryRepository.cs', 'SELECT source_id FROM scope_assets WHERE scope_id=? AND present=1 AND source_id>? ORDER BY source_id LIMIT 200')
        def membership_sql(after):
            return membership.replace('?', "'aa'", 1).replace('?', "'" + after + "'", 1)
        member_plan = db.query('EXPLAIN QUERY PLAN ' + membership_sql(''))
        assert any('COVERING INDEX assets_page' in row['detail'] for row in member_plan), member_plan
        assert not any('TEMP B-TREE' in row['detail'] for row in member_plan), member_plan
        result['membership_plan'] = member_plan
        result['membership_page'] = measure(lambda: db.query(membership_sql(f'asset:{count // 2:09d}')))
        after, visited, max_page = '', 0, 0
        started = time.perf_counter()
        while True:
            rows = db.query(membership_sql(after))
            assert len(rows) <= 200
            for row in rows:
                assert row['source_id'] > after
                after = row['source_id']
            visited += len(rows)
            max_page = max(max_page, len(rows))
            if len(rows) < 200:
                break
        assert visited == count
        result['membership_walk'] = dict(rows=visited, max_page=max_page, seconds=time.perf_counter() - started)
        db.close()
        return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--rows', nargs='+', type=int, default=[10000, 100000])
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if any(count < 400 for count in args.rows):
        parser.error('Row counts must be at least 400')
    results = []
    for count in args.rows:
        value = run(args.core, args.repository, count)
        results.append(value)
        print(json.dumps(value), flush=True)
    if args.output:
        args.output.write_text(json.dumps(results, indent=2) + '\n')
