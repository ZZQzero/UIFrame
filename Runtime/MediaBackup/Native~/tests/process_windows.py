"""Kill real repository processes at file/accept/attempt/receipt/release boundaries.

The parent reopens through the production ABI; no test SQLite implementation.
Within-COMMIT failure injection is covered by the shared core's separate suite.
"""
import argparse
import ctypes as C
from pathlib import Path
import struct
import subprocess
import sys
import tempfile

from benchmark import Status, encode

IDENTITY = 'a' * 64
HASH = 'b' * 64
STAGES = ['prepared', 'accepted', 'claimed', 'started', 'confirmed', 'released', 'unlinked']


def decode(data):
    offset = 0
    def number(fmt):
        nonlocal offset
        value = struct.unpack_from(fmt, data, offset)[0]
        offset += struct.calcsize(fmt)
        return value
    def blob():
        nonlocal offset
        size = number('<I')
        value = data[offset:offset + size]
        offset += size
        return value
    result = []
    for _ in range(number('<I')):
        columns, rows = number('<I'), number('<I')
        number('<q')
        names = [blob().decode() for _ in range(columns)]
        table = []
        for _ in range(rows):
            row = {}
            for name in names:
                kind = number('<B')
                row[name] = (None if kind == 0 else number('<q') if kind == 1 else
                             number('<d') if kind == 2 else blob().decode() if kind == 3 else blob())
            table.append(row)
        result.append(table)
    assert offset == len(data)
    return result


class Repository:
    def __init__(self, core, library, root, create):
        self.core = C.CDLL(core, mode=C.RTLD_GLOBAL)
        self.native = C.CDLL(library)
        self.native.ufbackup_open.argtypes = [C.c_char_p] * 4 + [C.c_int, C.POINTER(C.c_uint64), C.POINTER(Status)]
        self.native.ufbackup_call.argtypes = [C.c_uint64, C.c_uint32, C.c_char_p, C.c_uint32, C.c_void_p, C.c_uint32, C.POINTER(Status)]
        self.native.ufbackup_close.argtypes = [C.c_uint64, C.POINTER(Status)]
        self.handle = C.c_uint64()
        status = Status(size=C.sizeof(Status), abi=1)
        assert self.native.ufbackup_open(str(root).encode(), IDENTITY.encode(), b'https://test.invalid', b'test',
                                        int(create), C.byref(self.handle), C.byref(status)) == 0, status.message

    def call(self, command, *args):
        request = encode(args)
        output = C.create_string_buffer(1024 * 1024)
        status = Status(size=C.sizeof(Status), abi=1)
        assert self.native.ufbackup_call(self.handle, command, request, len(request), output, len(output),
                                        C.byref(status)) == 0, status.message
        return decode(output.raw[:status.length]) if status.length else []

    def close(self):
        status = Status(size=C.sizeof(Status), abi=1)
        assert self.native.ufbackup_close(self.handle, C.byref(status)) == 0, status.message


def worker(core, library, root, stage):
    db = Repository(core, library, root, True)
    db.call(58, 'aa')
    db.call(2, 'bb', 'aa', 1, 1, 'cc', 'file:photo', 'v1', 'photo.jpg', 'image/jpeg', 4)
    payload = Path(root) / IDENTITY / 'payloads/cc.payload'
    with payload.open('wb') as stream:
        stream.write(b'test')
        stream.flush()
        import os
        os.fsync(stream.fileno())
    db.call(3, 'cc', 'aa', 4, HASH, HASH, 'image/jpeg', 1024, 2)
    step = STAGES.index(stage)
    if step >= 1:
        db.call(4, 'bb', 'aa', 3)
    if step >= 2:
        db.call(9, 'cc', 0, 0, 4, 'dd')
    if step >= 3:
        db.call(22, 'cc', 1)
    if step >= 4:
        db.call(12, 'cc', 1, 1, HASH, '', 5, 4, HASH)
    if step >= 5:
        db.call(13, 'cc', 1, 1, 1)
    if step >= 6:
        payload.unlink()
    print('ready', flush=True)
    sys.stdin.read(1)


def native_worker(core, library, root, executor, stage):
    db = Repository(core, library, root, True)
    db.call(58, 'aa')
    db.call(2, 'bb', 'aa', 1, 1, 'cc', 'file:photo', 'v1', 'photo.jpg', 'image/jpeg', 4)
    (Path(root) / IDENTITY / 'payloads/cc.payload').write_bytes(b'test')
    db.call(3, 'cc', 'aa', 4, HASH, HASH, 'image/jpeg', 1024, 2)
    db.call(4, 'bb', 'aa', 3)
    db.call(9, 'cc', executor, 0, 4, 'aa')
    if stage >= 1:
        db.call(74, 'cc', 1, 'protected-reference')
    if stage >= 2:
        db.call(10, 'cc', 1, 'system-task', 'protected-reference')
    print('ready', flush=True)
    sys.stdin.read(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--worker', choices=STAGES)
    parser.add_argument('--root', type=Path)
    parser.add_argument('--native-stage', type=int, choices=[0, 1, 2])
    parser.add_argument('--executor', type=int, choices=[1, 2])
    args = parser.parse_args()
    if args.native_stage is not None:
        return native_worker(args.core, args.repository, args.root, args.executor, args.native_stage)
    if args.worker:
        return worker(args.core, args.repository, args.root, args.worker)
    with tempfile.TemporaryDirectory(prefix='ufbackup-process-') as directory:
        for index, stage in enumerate(STAGES):
            root = Path(directory) / stage
            process = subprocess.Popen([sys.executable, __file__, '--core', args.core, '--repository', args.repository,
                                        '--worker', stage, '--root', str(root)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
            try:
                assert process.stdout.readline().strip() == 'ready'
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
            db = Repository(args.core, args.repository, root, False)
            preparation = db.call(64, 'bb')
            assert preparation[0][0]['phase'] == (0 if index == 0 else 1)
            assert preparation[1][0]['id'] == 'cc'
            if index == 0:
                db.call(58, 'ee')
                assert db.call(55, 'ee', 6, 32)[-1][0]['recovered'] == 1
                assert not db.call(7, 'cc')[-1]
            elif index in (2, 3, 4):
                assert not db.call(18, '', 32)[-1], 'Unreleased payload selected for cleanup'
                db.call(57, 'cc', 1, 6)
                assert db.call(7, 'cc')[-1][0]['state'] == (3 if index == 4 else 6)
            if index >= 4:
                assert len(db.call(17, HASH)[-1]) == 1, 'Durable receipt was lost'
            for item in db.call(18, '', 32)[-1]:
                assert db.call(19, item['id'], item['updated_utc'], 7)[-1][0]['deleted'] == 1
            db.close()
            print(stage + ': passed', flush=True)
        for executor in (1, 2):
            for stage in (0, 1, 2):
                root = Path(directory) / ('handoff-%d-%d' % (executor, stage))
                process = subprocess.Popen([sys.executable, __file__, '--core', args.core, '--repository', args.repository,
                    '--native-stage', str(stage), '--executor', str(executor), '--root', str(root)],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
                try:
                    assert process.stdout.readline().strip() == 'ready'
                finally:
                    if process.poll() is None: process.kill()
                    process.wait(timeout=5)
                db = Repository(args.core, args.repository, root, False)
                row = db.call(7, 'cc')[-1][0]
                assert row['submission_state'] == stage
                scheduled = db.call(75, 0, executor, 100)[-1]
                assert len(scheduled) == (0 if stage == 0 else 1)
                if stage == 0:
                    db.call(58, 'ee')
                    db.call(57, 'cc', 1, 6)
                    recovered = db.call(75, 0, executor, 100)[-1][0]
                    assert recovered['execution_state'] == 4 and recovered['credential_released'] == 0
                elif stage == 1:
                    db.call(10, 'cc', 1, 'recovered-system-task', 'protected-reference')
                    assert db.call(22, 'cc', 1)[-1]
                else:
                    assert db.call(22, 'cc', 1)[-1]
                db.close()
                print('native handoff executor=%d phase=%d: passed' % (executor, stage))
    print('Backup process windows: 13/13 passed (7 lifecycle + 6 native handoff)')


if __name__ == '__main__':
    main()
