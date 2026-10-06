"""Exercise source membership, interrupted reconciliation and cross-scope linkage through the production ABI."""
import argparse
from pathlib import Path
import tempfile

from process_windows import Repository, IDENTITY, HASH


def scope(db, identity='aa'):
    db.call(30, identity, 'album:chosen', 'bb', 1, 1, 1, 1)


def scan_page(db, run, sources):
    for start in range(0, len(sources), 32):
        page = sources[start:start + 32]
        args = ['aa', run, len(page)]
        for source in page:
            args += [source, 'v1', 'photo.jpg', 'image/jpeg', source, 4]
        db.call(32, *args)


def activate(db, run, start=0, through=0):
    return db.call(33, 'aa', run, 'bb', 1, 1, 1, start, through, start)[-1][0]['pending']


def discover(db, before, after, kind, source):
    db.call(34, 'aa', 'bb', 1, 1, 1, before, after, before, 1, after, kind,
            source, 'v1', 'photo.jpg', 'image/jpeg', source, 4)


def prepare(db, root, task, source):
    db.call(2, task + 'a', 'ee', 1, 1, task, source, 'v1', 'photo.jpg', 'image/jpeg', 4)
    (root / IDENTITY / ('payloads/' + task + '.payload')).write_bytes(b'test')
    db.call(3, task, 'ee', 4, HASH, 'image/jpeg', 1024, 2)
    db.call(96, task, 'ee', 3)


def run(core, library):
    with tempfile.TemporaryDirectory(prefix='ufbackup-scope-') as temporary:
        root = Path(temporary)
        db = Repository(core, library, root, True)
        try:
            db.call(58, 'ee')
            scope(db)
            db.call(31, 'aa', 'cc', 'bb', 1, 1, 1, 0)
            scan_page(db, 'cc', ['asset:%03d' % i for i in range(70)])
            while activate(db, 'cc'):
                pass
            prepare(db, root, 'dd', 'asset:000')
            prepare(db, root, 'de', 'asset:unrelated')
            # All earlier removal events have expired. Keep some members, remove
            # another during enumeration, and add one after its enumeration page.
            db.call(31, 'aa', 'ff', 'bb', 1, 1, 1, 100)
            scan_page(db, 'ff', ['asset:%03d' % i for i in range(35, 70)])
            discover(db, 100, 101, 0, 'asset:late')
            discover(db, 101, 102, 3, 'asset:040')
            assert activate(db, 'ff', 100, 102) == 1
            assert db.call(73, 'aa')[-1][0]['enabled'] == 0
            assert db.call(7, 'dd')[-1][0]['state'] == 4
            db.close()
            db = Repository(core, library, root, False)
            pages = 1
            while activate(db, 'ff', 100, 102):
                pages += 1
                assert pages < 5
                assert db.call(73, 'aa')[-1][0]['enabled'] == 0
            assert db.call(73, 'aa')[-1][0]['enabled'] == 1
            pending = {r['source_id'] for r in db.call(35, 'aa', '', '', 0, 200)[-1]}
            assert pending == {'asset:%03d' % i for i in range(35, 70) if i != 40} | {'asset:late'}
            assert db.call(7, 'de')[-1][0]['state'] == 0, 'Independent task was paused'
            # A removed candidate is accepted elsewhere, then rejoins this scope.
            db.call(58, 'ee')
            prepare(db, root, 'df', 'asset:001')
            discover(db, 102, 103, 0, 'asset:001')
            associated = {r['source_id']: r for r in db.call(35, 'aa', '', '', 2, 200)[-1]}
            assert associated['asset:001']['task_id'] == 'df'
            assert db.call(69, 'aa', 0, 4)[-1][0]['count'] == 1
            assert db.call(7, 'df')[-1][0]['state'] == 4
            assert db.call(7, 'de')[-1][0]['state'] == 0
            # Normal first association uses the same task relation.
            scope(db, 'ab')
            db.call(31, 'ab', 'cd', 'bb', 1, 1, 1, 0)
            db.call(32, 'ab', 'cd', 1, 'asset:unrelated', 'v1', 'photo.jpg', 'image/jpeg', 'unrelated', 4)
            db.call(33, 'ab', 'cd', 'bb', 1, 1, 1, 0, 0, 0)
            assert db.call(69, 'ab', 0, 5)[-1][0]['count'] == 1
            assert db.call(7, 'de')[-1][0]['state'] == 4
            # Batch acceptance updates every matching pending scope, while an
            # explicit preparation cancellation remains a separate disposition.
            for identity, run in (('ac', 'ce'), ('ad', 'cf')):
                scope(db, identity)
                db.call(31, identity, run, 'bb', 1, 1, 1, 0)
                db.call(32, identity, run, 1, 'asset:shared', 'v1', 'photo.jpg', 'image/jpeg', 'shared', 4)
                db.call(33, identity, run, 'bb', 1, 1, 1, 0, 0, 0)
            db.call(36, 'ad', 'asset:shared', 'v1', 5, 'explicitly canceled')
            prepare(db, root, 'd0', 'asset:shared')
            assert db.call(35, 'ac', '', '', 2, 32)[-1][0]['task_id'] == 'd0'
            assert db.call(35, 'ad', '', '', 5, 32)[-1][0]['task_id'] is None
            db.call(9, 'd0', 0, 0, 6)
            from protocol_fixture import control, apply
            db.call(74, 'd0', 1, 'protected-api')
            control(db); apply(db, 'ce'); db.call(87, 'ce'); db.call(102, 'd0', 1)
            for identity in ('ac', 'ad'):
                assert db.call(35, identity, '', '', 3, 32)[-1][0]['source_id'] == 'asset:shared'
        finally:
            db.close()
    print('Scope reconciliation, durable paging and task association passed')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    args = parser.parse_args()
    run(args.core, args.repository)
