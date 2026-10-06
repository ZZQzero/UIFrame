"""Kill a process during a remote check; reopen without losing retry/unknown facts."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile
from process_windows import Repository, IDENTITY, HASH
from protocol_fixture import NOW, control


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
        db.call(2, 'bb', 'aa', 1, 1, 'cc', 'file:photo', 'v1', 'photo.jpg', 'image/jpeg', '', 0)
        (args.worker / IDENTITY / 'payloads/cc.payload').write_bytes(b'test')
        db.call(77, 'cc', 'aa', 4, 4, 1024, 2)
        db.call(3, 'cc', 'aa', 4, HASH, 'image/jpeg', 1024, 2)
        db.call(96, 'cc', 'aa', 3); db.call(9, 'cc', 0, 0, 4)
        db.call(74, 'cc', 1, 'protected')
        control(db, 'ce')
        db.call(86, 'ce', 'response lost', NOW, 0); db.call(87, 'ce'); db.call(102, 'cc', 1)
        db.call(101, 'cc', NOW, 0, 'renewed')
        control(db, 'cf')
        if args.retry:
            try: db.call(14, 'cc', 3, NOW)
            except AssertionError: pass
            else: raise AssertionError('Retry bypassed an active reconciliation')
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
                assert row['state'] == 2 and row['current_generation'] == 1
                attempt = db.call(60, 'cc', 1)[-1][0]
                assert attempt['control_id'] == 'cf' and attempt['credential_released'] == 0
                db.call(88, 'cf', 'Query interrupted', NOW, 0); db.call(87, 'cf'); db.call(102, 'cc', 1)
                assert db.call(7, 'cc')[-1][0]['state'] == 6
                try: db.call(14, 'cc', 3, NOW)
                except AssertionError: pass
                else: raise AssertionError('Unknown result allowed retry')
                db.call(101, 'cc', NOW, 0, 'renewed-again')
                assert db.call(7, 'cc')[-1][0]['current_generation'] == 1
            finally:
                db.close()
            print('reconciliation with persisted retry=%s: passed' % retry)
    print('Reconciliation process windows: 2/2 passed')


if __name__ == '__main__':
    main()
