"""Kill a process during a remote check; reopen without losing retry/unknown facts."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile
from process_windows import Repository, IDENTITY, HASH


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--worker', type=Path)
    parser.add_argument('--retry', action='store_true')
    args = parser.parse_args()
    if args.worker:
        db = Repository(args.core, args.repository, args.worker, True)
        db.call(58, 'aa')
        db.call(2, 'bb', 'aa', 1, 1, 'cc', 'file:photo', 'v1', 'photo.jpg', 'image/jpeg', 4)
        (args.worker / IDENTITY / 'payloads/cc.payload').write_bytes(b'test')
        db.call(3, 'cc', 'aa', 4, HASH, HASH, 'image/jpeg', 1024, 2)
        db.call(4, 'bb', 'aa', 3); db.call(9, 'cc', 0, 0, 4, 'aa')
        db.call(12, 'cc', 1, 3, '', 'unknown', 5, 4, HASH); db.call(13, 'cc', 1, 1, 1)
        db.call(77, 'cc')
        if args.retry:
            db.call(14, 'cc', 3, 6)
            assert not db.call(9, 'cc', 0, 0, 7, 'aa')[-1]
        print('ready', flush=True)
        sys.stdin.read(1)
        return
    for retry in (False, True):
        with tempfile.TemporaryDirectory(prefix='ufbackup-reconcile-process-') as directory:
            root = Path(directory)
            command = [sys.executable, __file__, '--core', args.core, '--repository', args.repository, '--worker', str(root)]
            if retry:
                command.append('--retry')
            process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
            try:
                assert process.stdout.readline().strip() == 'ready'
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
                process.stdin.close(); process.stdout.close()
            db = Repository(args.core, args.repository, root, False)
            try:
                row = db.call(7, 'cc')[-1][0]
                assert row['state'] == (0 if retry else 6) and row['current_generation'] == 1
                attempt = db.call(60, 'cc', 1)[-1][0]
                assert attempt['execution_state'] == 4 and attempt['payload_released'] == 1
                if retry:
                    assert db.call(9, 'cc', 0, 0, 8, 'aa')[-1][0]['current_generation'] == 2
                    db.call(12, 'cc', 2, 2, '', 'local failure before new transfer', 9, 4, HASH)
                    db.call(13, 'cc', 2, 1, 1)
                    assert db.call(7, 'cc')[-1][0]['state'] == 6
                assert db.call(77, 'cc')[-1]
                db.call(78, HASH)
            finally:
                db.close()
            print('reconciliation with persisted retry=%s: passed' % retry)
    print('Reconciliation process windows: 2/2 passed')


if __name__ == '__main__':
    main()
