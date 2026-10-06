"""Bounded 1,000-object protocol/byte accounting, real ABI and reference store.

Synthetic 64 KiB byte fixtures, in-process service calls, sequential storage I/O.
This measures request/byte invariants, not mobile throughput or radio energy.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import resource
import sys
import tempfile
import time

from process_windows import Repository, IDENTITY

PACKAGE = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(PACKAGE / 'Tools~/BackupServer'))
from store import Store, now_ms
from protocol import encode


def payload(index):
    return hashlib.sha256(str(index).encode()).digest() * 2048


def run(args, ratio):
    with tempfile.TemporaryDirectory(prefix='ufbackup-v2-bench-') as directory:
        root = Path(directory)
        server = Store(root / 'server', [dict(account='test', token='test')])
        server.storage_origin = 'https://storage.invalid'
        db = Repository(args.core, args.repository, root, True)
        db.call(58, 'aa')
        reused = args.photos * ratio // 100
        counts = dict(plans=0, queries=0, photo_bytes=0, uploads=0, max_prepared=0)
        try:
            # Establish already-verified contents before the measured workload.
            for offset in range(0, reused, 32):
                items = []
                for index in range(offset, min(reused, offset + 32)):
                    data = payload(index)
                    items.append(dict(clientTaskId='seed-' + str(index), attemptGeneration=1,
                        sourceNamespace='seed', sourceId=str(index), sourceVersion='v1',
                        sha256=hashlib.sha256(data).hexdigest(), byteCount=len(data), name='fixture.jpg', mimeType='image/jpeg'))
                response = server.control('test', 'plans', dict(protocolVersion=2, requestId='seed-' + str(offset), items=items))
                for item in response['items']:
                    upload = item['upload']; index = int(item['clientTaskId'].split('-')[1]); data = payload(index)
                    server.receive(upload['uploadId'], upload['headers'][0]['value'], len(data), io.BytesIO(data))
                server.confirm_pending()
            started = time.perf_counter()
            sequence = 0

            def control(now):
                nonlocal sequence
                sequence += 1; request = format(sequence, '032x')
                rows = db.call(80, request, 0, now, 0, 1)[-1]
                assert len(rows) == 1
                row = rows[0]; body = json.loads(row['body'])
                counts['plans' if row['kind'] == 0 else 'queries'] += 1
                db.call(81, request); db.call(82, request, 'system:' + request); db.call(83, request)
                response = server.control('test', ['plans', 'status', 'cancellations'][row['kind']], body)
                encoded = encode(response).decode()
                descriptors = db.call(84, request, encoded)[-1]
                values = [request, encoded, now, len(descriptors)]
                for descriptor in descriptors: values += [descriptor['task_id'], 'protected-' + descriptor['task_id']]
                db.call(85, *values); db.call(87, request)
                return response

            for offset in range(0, args.photos, 32):
                now = 621355968000000000 + now_ms() * 10000
                indexes = range(offset, min(args.photos, offset + 32))
                batch = format(offset + 1, '032x'); values = [batch, 'aa', now, len(indexes)]
                for index in indexes:
                    values += [format(index + 1, '032x'), 'asset:' + str(index), 'v1', 'fixture.jpg', 'image/jpeg', 0]
                db.call(2, *values)
                for index in indexes:
                    item = format(index + 1, '032x'); data = payload(index)
                    (root / IDENTITY / 'payloads' / (item + '.payload')).write_bytes(data)
                    db.call(3, item, 'aa', len(data), hashlib.sha256(data).hexdigest(), 'image/jpeg', 536870912, now)
                    db.call(96, item, 'aa', now); db.call(9, item, 0, 0, now); db.call(74, item, 1, 'protected-api')
                staged = sum(row['count'] for row in db.call(56)[-1] if row['state'] == 1)
                counts['max_prepared'] = max(counts['max_prepared'], staged)
                response = control(now); sent = 0
                for result in response['items']:
                    if result['status'] == 'Confirmed': continue
                    item = result['clientTaskId']; index = int(item, 16) - 1; data = payload(index); upload = result['upload']
                    assert index >= reused, 'Verified duplicate sent photo bytes'
                    assert db.call(91, item, 1, 'system:upload', now)[-1][0]['started']
                    server.receive(upload['uploadId'], upload['headers'][0]['value'], len(data), io.BytesIO(data))
                    counts['photo_bytes'] += len(data); counts['uploads'] += 1; sent += 1
                    db.call(92, item, 1, 1, '', now); db.call(93, item, 1, now)
                if sent:
                    server.confirm_pending(); confirmed = control(now + 10000000)
                    assert all(item['status'] == 'Confirmed' for item in confirmed['items'])
                for index in indexes: db.call(102, format(index + 1, '032x'), 1)
                for item in db.call(18, '', 64)[-1]: db.call(19, item['id'], item['updated_utc'], now)
                db.call(100, now, 64)
            counts.update(photos=args.photos, verified_percent=ratio, seconds=time.perf_counter() - started,
                          fixture_bytes=65536, process_peak_rss_bytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss)
            assert counts['plans'] == (args.photos + 31) // 32
            assert counts['photo_bytes'] == (args.photos - reused) * 65536
            assert counts['max_prepared'] <= 64
            assert not list((root / IDENTITY / 'controls').iterdir())
            return counts
        finally:
            db.close(); server.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--core', required=True); parser.add_argument('--repository', required=True)
    parser.add_argument('--photos', type=int, default=1000); parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    results = []
    for ratio in (0, 50, 100):
        result = run(args, ratio); results.append(result); print(json.dumps(result), flush=True)
    args.output.write_text(json.dumps(results, indent=2) + '\n')
