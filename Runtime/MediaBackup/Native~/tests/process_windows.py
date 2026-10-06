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
STAGES = ['prepared', 'accepted', 'claimed', 'handed', 'control_created', 'control_sealed',
          'control_submitted', 'control_started', 'response_applied', 'control_released',
          'credentials_released', 'unlinked', 'upload_planned', 'upload_started', 'upload_ended', 'upload_released']
from protocol_fixture import NOW, control, apply


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
    def __init__(self, core, library, root, create, identity=IDENTITY):
        self.core = C.CDLL(core, mode=C.RTLD_GLOBAL)
        self.native = C.CDLL(library)
        self.native.ufbackup_open.argtypes = [C.c_char_p] * 4 + [C.c_int, C.POINTER(C.c_uint64), C.POINTER(Status)]
        self.native.ufbackup_call.argtypes = [C.c_uint64, C.c_uint32, C.c_char_p, C.c_uint32, C.c_void_p, C.c_uint32, C.POINTER(Status)]
        self.native.ufbackup_close.argtypes = [C.c_uint64, C.POINTER(Status)]
        self.handle = C.c_uint64()
        status = Status(size=C.sizeof(Status), abi=2)
        assert self.native.ufbackup_open(str(root).encode(), identity.encode(), b'https://test.invalid', b'test',
                                        int(create), C.byref(self.handle), C.byref(status)) == 0, status.message

    def call(self, command, *args):
        request = encode(args)
        output = C.create_string_buffer(1024 * 1024)
        status = Status(size=C.sizeof(Status), abi=2)
        assert self.native.ufbackup_call(self.handle, command, request, len(request), output, len(output),
                                        C.byref(status)) == 0, status.message
        return decode(output.raw[:status.length]) if status.length else []

    def close(self):
        status = Status(size=C.sizeof(Status), abi=2)
        assert self.native.ufbackup_close(self.handle, C.byref(status)) == 0, status.message


def worker(core, library, root, stage, executor):
    db = Repository(core, library, root, True)
    db.call(58, 'aa')
    db.call(2, 'bb', 'aa', NOW, 1, 'cc', 'file:photo', 'v1', 'photo.jpg', 'image/jpeg', 4)
    payload = Path(root) / IDENTITY / 'payloads/cc.payload'
    payload.write_bytes(b'test')
    db.call(3, 'cc', 'aa', 4, HASH, 'image/jpeg', 1024, NOW)
    step = STAGES.index(stage)
    if step >= 1: db.call(96, 'cc', 'aa', NOW)
    if step >= 2: db.call(9, 'cc', executor, 0, NOW)
    if step >= 3: db.call(74, 'cc', 1, 'protected-api')
    if step >= 4: control(db, 'ce', executor, submit=False)
    if step >= 5: db.call(81, 'ce')
    if step >= 6: db.call(82, 'ce', 'system:ce')
    if step >= 7: db.call(83, 'ce')
    if step >= 12:
        apply(db, 'ce', 'UploadRequired'); db.call(87, 'ce')
        if step >= 13: assert db.call(91, 'cc', 1, 'system:upload', NOW)[-1][0]['started']
        if step >= 14: db.call(92, 'cc', 1, 1, '', NOW)
        if step >= 15: db.call(93, 'cc', 1, NOW)
    else:
        if step >= 8: apply(db, 'ce')
        if step >= 9: db.call(87, 'ce')
        if step >= 10: db.call(102, 'cc', 1)
        if step >= 11: payload.unlink()
    print('ready', flush=True)
    sys.stdin.read(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True); parser.add_argument('--repository', required=True)
    parser.add_argument('--worker', choices=STAGES); parser.add_argument('--root', type=Path)
    parser.add_argument('--executor', type=int, choices=[0, 1, 2], default=0)
    args = parser.parse_args()
    if args.worker: return worker(args.core, args.repository, args.root, args.worker, args.executor)
    passed = 0
    with tempfile.TemporaryDirectory(prefix='ufbackup-process-v2-') as directory:
        for executor in (0, 1, 2):
            for index, stage in enumerate(STAGES):
                root = Path(directory) / (str(executor) + '-' + stage)
                process = subprocess.Popen([sys.executable, __file__, '--core', args.core, '--repository', args.repository,
                    '--worker', stage, '--root', str(root), '--executor', str(executor)],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
                try: assert process.stdout.readline().strip() == 'ready'
                finally:
                    if process.poll() is None: process.kill()
                    process.wait(timeout=5); process.stdin.close(); process.stdout.close()
                db = Repository(args.core, args.repository, root, False)
                try:
                    item = db.call(98, 'bb')[-1][0]
                    assert item['id'] == 'cc' and not item['details_expired']
                    if index == 0:
                        db.call(58, 'ee')
                        assert db.call(55, 'ee', NOW, 32)[-1][0]['recovered'] == 1
                        assert db.call(7, 'cc')[-1][0]['state'] == 7
                    elif 2 <= index < 10 or index >= 12:
                        assert not db.call(18, '', 32)[-1], 'Unreleased or unconfirmed payload became reclaimable'
                    if 4 <= index <= 7:
                        row = db.call(104, 'ce')[-1][0]
                        assert row['id'] == 'ce' and row['state'] == index - 4
                        if index >= 6:
                            db.call(88, 'ce', 'Process ended; result unknown', NOW, 0); db.call(87, 'ce')
                            assert db.call(7, 'cc')[-1][0]['state'] == 6
                    if 8 <= index <= 11:
                        assert len(db.call(17, HASH)[-1]) == 1, 'Receipt lost across process death'
                        if index == 8: db.call(87, 'ce')
                        if index < 10: db.call(102, 'cc', 1)
                    if index in (2, 3):
                        db.call(57, 'cc', 1, NOW); db.call(102, 'cc', 1)
                        assert db.call(7, 'cc')[-1][0]['state'] == 6
                    if index == 13:
                        db.call(92, 'cc', 1, 0, 'Process ended during PUT', NOW); db.call(93, 'cc', 1, NOW)
                        assert db.call(7, 'cc')[-1][0]['state'] == 6
                    if index == 14:
                        db.call(93, 'cc', 1, NOW)
                        row = db.call(7, 'cc')[-1][0]
                        assert row['state'] == 2 and row['upload_released'] and row['upload_reference'] is None
                    for item in db.call(18, '', 32)[-1]:
                        assert db.call(19, item['id'], item['updated_utc'], NOW)[-1][0]['deleted'] == 1
                finally: db.close()
                passed += 1
                print('executor=%d %s: passed' % (executor, stage), flush=True)
    print('Backup v2 process windows: %d/48 passed' % passed)


if __name__ == '__main__': main()
