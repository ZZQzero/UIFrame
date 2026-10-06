import hashlib
import io
import json
import os
import secrets
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from http.server import ThreadingHTTPServer
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

from protocol import MAX_FILE_BYTES, decode_request, encode, identity
from server import Store, handler_for
from store import now_ms


class BackupProtocolTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.credentials = [dict(account='alice', token='test-alice'), dict(account='bob', token='test-bob')]
        self.store = Store(self.directory.name, self.credentials)
        self.private = ThreadingHTTPServer(('127.0.0.1', 0), handler_for(self.store, storage=True))
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), handler_for(self.store))
        self.store.storage_origin = 'http://127.0.0.1:' + str(self.private.server_port)
        self.url = 'http://127.0.0.1:' + str(self.server.server_port)
        self.threads = []
        for server in (self.private, self.server):
            thread = threading.Thread(target=lambda s=server: s.serve_forever(poll_interval=0.02))
            thread.start()
            self.threads.append(thread)

    def tearDown(self):
        for server in (self.private, self.server):
            server.shutdown()
            server.server_close()
        for thread in self.threads:
            thread.join()
        self.store.close()
        self.directory.cleanup()

    def call(self, method, path, data=None, token='test-alice', headers=None):
        values = dict(headers or {})
        if token is not None:
            values['Authorization'] = 'Bearer ' + token
        if isinstance(data, dict):
            data = encode(data)
            values['Content-Type'] = 'application/json'
        request = Request(path if path.startswith('http') else self.url + path, data=data, method=method, headers=values)
        try:
            response = urlopen(request, timeout=5)
        except HTTPError as error:
            response = error
        with response:
            raw = response.read()
            return response.status, json.loads(raw) if response.headers.get_content_type() == 'application/json' else raw

    def item(self, data=b'photo', task=None, **changes):
        task = task or secrets.token_hex(16)
        return dict(dict(clientTaskId=task, attemptGeneration=1, sourceNamespace='install:camera', sourceId=task,
                         sourceVersion='1', sha256=hashlib.sha256(data).hexdigest(), byteCount=len(data),
                         mimeType='image/jpeg', name='照片.jpg'), **changes)

    def control(self, operation, items, request_id=None, token='test-alice'):
        return self.call('POST', '/v2/backup/' + operation,
            dict(protocolVersion=2, requestId=request_id or secrets.token_hex(16), items=items), token)

    def plan(self, items):
        code, result = self.control('plans', items)
        self.assertEqual(200, code, result)
        self.assertEqual('alice', result['account'])
        return result['items']

    def put(self, planned, data, headers=None):
        descriptor = planned['upload']
        values = {h['name']: h['value'] for h in descriptor['headers']}
        values.update(headers or {})
        return self.call('PUT', descriptor['url'], data, token=None, headers=values)

    def query(self, item):
        code, response = self.control('status', [identity(item)])
        self.assertEqual(200, code)
        return response['items'][0]

    def backup(self, data=b'photo', **changes):
        item = self.item(data, **changes)
        planned = self.plan([item])[0]
        if planned['status'] == 'UploadRequired':
            self.assertEqual((204, b''), self.put(planned, data))
            self.store.confirm_pending()
        result = self.query(item)
        self.assertEqual('Confirmed', result['status'])
        return item, planned, result['receipt']

    def test_full_batch_and_result_identity(self):
        items = [self.item(task='task-' + str(i)) for i in range(32)]
        results = self.plan(items)
        self.assertEqual(32, len(results))
        self.assertEqual({(x['clientTaskId'], x['attemptGeneration']) for x in items},
                         {(x['clientTaskId'], x['attemptGeneration']) for x in results})
        self.assertTrue(all(x['status'] == 'UploadRequired' for x in results))
        self.assertEqual(1, self.store.db.execute('SELECT count(*) FROM requests').fetchone()[0])

    def test_business_rejection_does_not_reject_healthy_item(self):
        results = self.plan([self.item(byteCount=MAX_FILE_BYTES + 1), self.item()])
        self.assertEqual(['Rejected', 'UploadRequired'], [x['status'] for x in results])
        self.assertEqual((204, b''), self.put(results[1], b'photo'))

    def test_request_id_is_bound_to_fixed_parameters_and_operation(self):
        item = self.item()
        first = self.control('plans', [item], 'request-1')
        self.assertEqual(first[1]['items'], self.control('plans', [item], 'request-1')[1]['items'])
        self.assertEqual(409, self.control('plans', [dict(item, name='changed')], 'request-1')[0])
        self.assertEqual(409, self.control('status', [identity(item)], 'request-1')[0])
        self.assertEqual(1, self.store.db.execute('SELECT count(*) FROM attempts').fetchone()[0])

    def test_structure_rejected_before_any_item_side_effects(self):
        item = self.item()
        for items in ([], [item] * 2, [self.item() for _ in range(33)], [item, dict(item, clientTaskId='second', extra=1)],
                      [dict(item, attemptGeneration=True)], [dict(item, name='bad\r\nheader')]):
            self.assertEqual(400, self.control('plans', items)[0])
        self.assertEqual(0, self.store.db.execute('SELECT count(*) FROM attempts').fetchone()[0])
        raw = b'{"protocolVersion":2,"protocolVersion":2,"requestId":"x","items":[]}'
        self.assertEqual(400, self.call('POST', '/v2/backup/plans', raw, headers={'Content-Type': 'application/json'})[0])

    def test_control_body_and_version_limits(self):
        self.assertEqual(413, self.call('POST', '/v2/backup/plans', b'x' * (256 * 1024 + 1))[0])
        self.assertEqual(400, self.call('POST', '/v2/backup/plans', dict(protocolVersion=1, requestId='x', items=[self.item()]))[0])
        self.assertEqual(404, self.call('POST', '/v1/uploads', {})[0])

    def test_put_success_is_empty_and_not_a_receipt(self):
        item = self.item()
        planned = self.plan([item])[0]
        self.assertNotEqual(self.url, self.store.storage_origin)
        self.assertEqual((204, b''), self.put(planned, b'photo'))
        pending = self.query(item)
        self.assertEqual('Verifying', pending['status'])
        self.assertGreater(pending['nextCheckAt'], now_ms())
        self.assertNotIn('receipt', pending)
        self.store.confirm_pending()
        self.assertEqual('Confirmed', self.query(item)['status'])

    def test_confirmation_runs_without_further_client_requests(self):
        errors = []
        self.store.start_worker(errors.append)
        item = self.item()
        planned = self.plan([item])[0]
        self.assertEqual(204, self.put(planned, b'photo')[0])
        deadline = time.monotonic() + 5
        while True:
            with self.store.lock:
                count = self.store.db.execute('SELECT count(*) FROM backups').fetchone()[0]
            if count:
                break
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.01)
        self.assertEqual([], errors)

    def test_verified_dedup_keeps_separate_sources_and_account_isolation(self):
        first, _, receipt = self.backup()
        second = self.item()
        result = self.plan([second])[0]
        self.assertEqual('Confirmed', result['status'])
        self.assertNotEqual(receipt['backupId'], result['receipt']['backupId'])
        self.assertEqual(second['clientTaskId'], result['receipt']['clientTaskId'])
        self.assertEqual(1, self.store.db.execute('SELECT count(*) FROM contents').fetchone()[0])
        status, bob = self.control('plans', [second], token='test-bob')
        self.assertEqual(200, status)
        self.assertEqual('UploadRequired', bob['items'][0]['status'])
        self.assertEqual(404, self.call('GET', '/v2/backups/' + receipt['backupId'] + '/content', token='test-bob')[0])
        self.assertEqual([], self.call('GET', '/v2/backups', token='test-bob')[1]['items'])

    def test_concurrent_first_content_keeps_independent_attempts(self):
        items = [self.item(), self.item()]
        planned = self.plan(items)
        self.assertNotEqual(planned[0]['upload']['uploadId'], planned[1]['upload']['uploadId'])
        for result in planned:
            self.put(result, b'photo')
        self.store.confirm_pending()
        self.assertEqual(1, self.store.db.execute('SELECT count(*) FROM contents').fetchone()[0])
        self.assertEqual(2, self.store.db.execute('SELECT count(*) FROM backups').fetchone()[0])

    def test_source_version_and_task_metadata_conflicts(self):
        item, _, receipt = self.backup()
        result = self.plan([dict(item, sha256='0' * 64), dict(item, clientTaskId='new', sha256='0' * 64)])
        self.assertEqual(['TaskConflict', 'SourceConflict'], [x['error']['code'] for x in result])
        alias = self.plan([dict(item, clientTaskId='same-source')])[0]
        self.assertEqual(receipt['backupId'], alias['receipt']['backupId'])
        self.assertEqual('same-source', alias['receipt']['clientTaskId'])

    def test_cancel_before_plan_leaves_tombstone(self):
        item = self.item()
        result = self.control('cancellations', [identity(item)])[1]['items'][0]
        self.assertEqual('Canceled', result['status'])
        self.assertEqual('Canceled', self.plan([item])[0]['status'])
        next_attempt = dict(item, attemptGeneration=2)
        self.assertEqual('UploadRequired', self.plan([next_attempt])[0]['status'])
        self.assertEqual('Canceled', self.query(item)['status'])

    def test_cancel_wins_after_upload_before_verification(self):
        item = self.item()
        planned = self.plan([item])[0]
        self.put(planned, b'photo')
        self.assertEqual('Canceled', self.control('cancellations', [identity(item)])[1]['items'][0]['status'])
        self.store.confirm_pending()
        self.assertEqual('Canceled', self.query(item)['status'])
        self.assertEqual(0, self.store.db.execute('SELECT count(*) FROM backups').fetchone()[0])
        self.assertEqual(409, self.put(planned, b'photo')[0])
        self.store.cleanup()
        self.assertEqual([], list(Path(self.directory.name).rglob('*.incoming')))

    def test_confirm_wins_cancel_returns_receipt_without_deletion(self):
        item, planned, receipt = self.backup()
        result = self.control('cancellations', [identity(item)])[1]['items'][0]
        self.assertEqual('Confirmed', result['status'])
        self.assertEqual(receipt, result['receipt'])
        self.assertEqual(b'photo', self.call('GET', '/v2/backups/' + receipt['backupId'] + '/content')[1])

    def test_repeated_signed_put_cannot_overwrite_confirmed_content(self):
        item, planned, receipt = self.backup()
        self.assertEqual(204, self.put(planned, b'other')[0])
        self.store.confirm_pending()
        self.assertEqual(receipt, self.query(item)['receipt'])
        self.assertEqual(b'photo', self.call('GET', '/v2/backups/' + receipt['backupId'] + '/content')[1])
        self.assertEqual(204, self.put(planned, b'photo')[0])
        self.store.confirm_pending()
        self.assertEqual(1, self.store.db.execute('SELECT count(*) FROM backups').fetchone()[0])

    def test_corrupt_content_and_incorrect_size_never_confirm(self):
        item = self.item()
        planned = self.plan([item])[0]
        self.assertEqual(400, self.put(planned, b'bad')[0])
        self.assertEqual(204, self.put(planned, b'other')[0])
        self.store.confirm_pending()
        self.assertEqual('ChecksumMismatch', self.query(item)['error']['code'])
        self.assertEqual(409, self.put(planned, b'photo')[0])

    def test_empty_file(self):
        item, _, receipt = self.backup(b'')
        self.assertEqual(0, receipt['byteCount'])

    def test_auth_expiry_refresh_and_credential_separation(self):
        item = self.item()
        planned = self.plan([item])[0]
        self.assertEqual(403, self.put(planned, b'photo', {'Authorization': 'Bearer test-alice'})[0])
        self.assertEqual(403, self.put(planned, b'photo', {'X-Upload-Authorization': '1.invalid'})[0])
        with self.store.lock, self.store.db:
            self.store.db.execute('UPDATE attempts SET expires_at=1')
        expired = self.query(item)
        self.assertEqual(403, self.put(expired, b'photo')[0])
        refreshed = self.plan([item])[0]
        self.assertEqual(planned['upload']['uploadId'], refreshed['upload']['uploadId'])
        self.assertGreater(refreshed['upload']['expiresAt'], now_ms())
        self.assertEqual(204, self.put(refreshed, b'photo')[0])
        self.assertEqual(401, self.control('status', [identity(item)], token='wrong')[0])

    def test_absent_is_not_cancellation(self):
        item = self.item()
        self.assertEqual('Absent', self.query(item)['status'])
        self.assertEqual('UploadRequired', self.plan([item])[0]['status'])

    def test_lost_plan_response_can_be_reconciled_by_task_and_generation(self):
        item = self.item()
        first = self.plan([item])[0]
        self.assertEqual(first, self.query(item))

    def test_slow_body_does_not_block_plan_or_cancellation(self):
        item = self.item(b'abcdef')
        planned = self.plan([item])[0]
        descriptor = planned['upload']
        endpoint = urlsplit(descriptor['url'])
        auth = descriptor['headers'][0]['value']
        with socket.create_connection((endpoint.hostname, endpoint.port)) as connection:
            connection.settimeout(5)
            connection.sendall((f'PUT {endpoint.path} HTTP/1.1\r\nHost: localhost\r\nX-Upload-Authorization: {auth}\r\nContent-Length: 6\r\n\r\na').encode())
            deadline = time.monotonic() + 3
            while True:
                with self.store.lock:
                    count = self.store.db.execute("SELECT count(*) FROM incoming WHERE state='writing'").fetchone()[0]
                if count:
                    break
                self.assertLess(time.monotonic(), deadline)
                time.sleep(0.01)
            self.plan([self.item()])
            self.store.maintenance(at=descriptor['expiresAt'] + 24 * 60 * 60 * 1000 + 1)
            with self.store.lock:
                self.assertEqual('upload', self.store.db.execute('SELECT state FROM attempts WHERE upload_id=?', (descriptor['uploadId'],)).fetchone()[0])
                self.assertEqual('writing', self.store.db.execute('SELECT state FROM incoming WHERE upload_id=?', (descriptor['uploadId'],)).fetchone()[0])
            self.assertEqual('Canceled', self.control('cancellations', [identity(item)])[1]['items'][0]['status'])
            connection.sendall(b'bcdef')
            response = bytearray()
            while block := connection.recv(4096): response.extend(block)
            self.assertTrue(response.startswith(b'HTTP/1.1 409'), response)

    def test_duplicate_slow_put_does_not_block_confirmation_or_cleanup(self):
        data = b'photo'
        items = [self.item(data), self.item(data, sourceVersion='other')]
        planned = self.plan(items)
        for result in planned:
            self.assertEqual(204, self.put(result, data)[0])
        entered, release = threading.Event(), threading.Event()
        class SlowBody:
            def read(self, count):
                entered.set()
                if not release.wait(5):
                    raise TimeoutError('Fixture not released')
                return data
        descriptor = planned[0]['upload']
        with ThreadPoolExecutor(max_workers=2) as pool:
            duplicate = pool.submit(self.store.receive, descriptor['uploadId'], descriptor['headers'][0]['value'], len(data), SlowBody())
            try:
                self.assertTrue(entered.wait(2))
                self.assertEqual([], pool.submit(self.store.confirm_pending).result(timeout=2)['failures'])
                self.assertEqual(['Confirmed', 'Confirmed'], [self.query(item)['status'] for item in items])
                cleaned = pool.submit(self.store.cleanup).result(timeout=2)
                self.assertEqual(2 * len(data), cleaned['freedBytes'])
                self.assertEqual([], cleaned['failures'])
                with self.store.lock:
                    self.assertEqual(['writing'], [row[0] for row in self.store.db.execute('SELECT state FROM incoming')])
            finally:
                release.set()
            duplicate.result(timeout=2)
        self.store.confirm_pending(); self.store.cleanup()
        self.assertEqual(['Confirmed', 'Confirmed'], [self.query(item)['status'] for item in items])
        self.assertEqual(0, self.store.db.execute('SELECT count(*) FROM incoming').fetchone()[0])

    def test_slow_hash_does_not_hold_database_and_cancel_still_wins(self):
        item = self.item()
        self.put(self.plan([item])[0], b'photo')
        entered, release = threading.Event(), threading.Event()
        import store as implementation
        original = implementation.file_hash
        def slow(stream):
            entered.set()
            if not release.wait(5): raise TimeoutError('Fixture not released')
            return original(stream)
        with patch.object(implementation, 'file_hash', slow):
            worker = threading.Thread(target=self.store.confirm_pending)
            worker.start()
            try:
                self.assertTrue(entered.wait(2))
                self.plan([self.item()])
                self.assertEqual('Canceled', self.control('cancellations', [identity(item)])[1]['items'][0]['status'])
            finally:
                release.set()
                worker.join(5)
        self.assertEqual('Canceled', self.query(item)['status'])

    def test_cleanup_failure_isolated_and_requires_explicit_retry(self):
        items = [self.item(b'first'), self.item(b'other')]
        planned = self.plan(items)
        for result, data in zip(planned, (b'first', b'other')): self.put(result, data)
        self.store.confirm_pending()
        rows = self.store.db.execute('SELECT * FROM incoming ORDER BY created_at,id').fetchall()
        failed_path = Path(rows[0]['path'])
        original = Path.unlink
        failure = PermissionError('fixture locked file')
        def remove(path, *args, **kwargs):
            if path == failed_path: raise failure
            return original(path, *args, **kwargs)
        with patch.object(Path, 'unlink', remove):
            result = self.store.cleanup()
        self.assertEqual(1, len(result['failures']))
        self.assertEqual(5, result['freedBytes'])
        self.assertEqual(0, self.store.cleanup()['freedBytes'])
        self.assertTrue(failed_path.exists())
        self.backup(b'independent')
        self.assertEqual('Confirmed', self.query(items[0])['status'])
        self.store.cleanup(retry_failed=True)
        self.assertFalse(failed_path.exists())

    def test_cleanup_recording_failure_preserves_original_exception(self):
        self.backup()
        self.store.db.execute("CREATE TEMP TRIGGER reject_cleanup BEFORE UPDATE ON incoming WHEN new.state='cleanup_failed' BEGIN SELECT RAISE(ABORT,'fixture DB failure'); END")
        primary = PermissionError('fixture primary')
        recorded = io.StringIO()
        with patch.object(Path, 'unlink', side_effect=primary), patch.object(sys, 'stderr', recorded):
            with self.assertRaises(PermissionError) as caught:
                self.store.cleanup()
        self.assertIs(primary, caught.exception)
        self.assertIn('fixture DB failure', recorded.getvalue())
        self.store.db.execute('DROP TRIGGER reject_cleanup')

    def test_publication_before_database_commit_recovers(self):
        item = self.item()
        self.put(self.plan([item])[0], b'photo')
        incoming = self.store.db.execute('SELECT * FROM incoming').fetchone()
        blob = self.store.directory('alice') / (item['sha256'] + '-5.blob')
        with self.store.db:
            self.store.db.execute('INSERT INTO contents(account,sha256,size,path,verified,created_at) VALUES(?,?,?,?,0,?)', ('alice', item['sha256'], 5, str(blob), now_ms()))
        os.link(incoming['path'], blob)
        self.store.confirm_pending()
        self.assertEqual('Confirmed', self.query(item)['status'])
        self.assertEqual(1, self.store.db.execute('SELECT count(*) FROM contents').fetchone()[0])

    def test_pagination_is_bounded_and_download_keeps_bytes(self):
        for i in range(3): self.backup(bytes([i]))
        first = self.call('GET', '/v2/backups?limit=2')[1]
        second = self.call('GET', '/v2/backups?limit=2&after=' + str(first['nextAfter']))[1]
        self.assertEqual(2, len(first['items']))
        self.assertEqual(1, len(second['items']))
        self.assertEqual(400, self.call('GET', '/v2/backups?limit=33')[0])
        for row in first['items'] + second['items']:
            data = self.call('GET', '/v2/backups/' + row['backupId'] + '/content')[1]
            self.assertEqual(row['sha256'], hashlib.sha256(data).hexdigest())

    def test_expired_attempts_reject_stale_signed_uploads(self):
        item = self.item()
        planned = self.plan([item])[0]
        self.store.maintenance(at=planned['upload']['expiresAt'] + 86400001)
        self.assertEqual('Rejected', self.query(item)['status'])
        self.assertEqual(409, self.put(planned, b'photo')[0])
        renewed = dict(item, attemptGeneration=2)
        self.assertEqual('UploadRequired', self.plan([renewed])[0]['status'])

    def test_ttl_removes_unreferenced_publication_but_retains_confirmed_content(self):
        _, _, confirmed = self.backup(b'confirmed')
        orphan = self.store.directory('alice') / 'orphan.blob'
        orphan.write_bytes(b'orphan')
        with self.store.db:
            self.store.db.execute('INSERT INTO contents(account,sha256,size,path,verified,created_at) VALUES(?,?,?,?,1,?)',
                ('alice', hashlib.sha256(b'orphan').hexdigest(), 6, str(orphan), now_ms() - 86400001))
        self.store.maintenance()
        self.assertFalse(orphan.exists())
        self.assertEqual(b'confirmed', Path(self.store.content('alice', confirmed['backupId'])['path']).read_bytes())

    def test_content_cleanup_failure_isolated_and_explicitly_retried(self):
        files = []
        for index in range(2):
            path = self.store.directory('alice') / ('orphan-%d.blob' % index)
            path.write_bytes(bytes([index])); files.append(path)
            with self.store.db:
                self.store.db.execute('INSERT INTO contents(account,sha256,size,path,created_at) VALUES(?,?,?,?,?)',
                    ('alice', hashlib.sha256(bytes([index])).hexdigest(), 1, str(path), now_ms() - 86400001))
        unlink = Path.unlink
        def remove(path, *args, **kwargs):
            if path == files[0]: raise PermissionError('isolated object failure')
            return unlink(path, *args, **kwargs)
        with patch.object(Path, 'unlink', remove):
            self.assertEqual(1, len(self.store.maintenance()['failures']))
            self.assertEqual([], self.store.maintenance()['failures'])
        self.assertTrue(files[0].exists()); self.assertFalse(files[1].exists())
        self.store.maintenance(retry_failed=True); self.assertFalse(files[0].exists())

    def test_truncated_body_is_never_confirmed_and_reclaims_only_its_file(self):
        item = self.item()
        planned = self.plan([item])[0]['upload']
        with self.assertRaises(Exception):
            self.store.receive(planned['uploadId'], planned['headers'][0]['value'], 5, io.BytesIO(b'ph'))
        self.store.confirm_pending(); self.store.cleanup()
        self.assertEqual('UploadRequired', self.query(item)['status'])
        self.assertEqual(0, self.store.db.execute('SELECT count(*) FROM incoming').fetchone()[0])

    def test_slow_download_does_not_hold_database(self):
        _, _, receipt = self.backup()
        entered, release = threading.Event(), threading.Event()
        class SlowWriter(io.BytesIO):
            def write(self, value):
                entered.set()
                if not release.wait(5): raise TimeoutError('Fixture not released')
                return super().write(value)
        handler_type = handler_for(self.store)
        handler = handler_type.__new__(handler_type)
        handler.path = '/v2/backups/' + receipt['backupId'] + '/content'
        handler.headers = {'Authorization': 'Bearer test-alice'}
        handler.wfile = SlowWriter()
        handler.send_response = handler.send_header = lambda *args: None
        handler.end_headers = lambda: None
        thread = threading.Thread(target=lambda: handler.route('GET'))
        thread.start()
        try:
            self.assertTrue(entered.wait(2))
            self.plan([self.item()])
        finally:
            release.set()
            thread.join(5)
        self.assertEqual(b'photo', handler.wfile.getvalue())

    def test_process_kill_after_body_arrival_keeps_pending_confirmation(self):
        worker = '''
import sys
from store import Store
from protocol import decode_request
import io
s=Store(sys.argv[1],[dict(account='alice',token='test-alice')])
s.storage_origin='http://127.0.0.1:1'
request=decode_request(sys.argv[2].encode(),'plans')
upload=s.control('alice','plans',request)['items'][0]['upload']
s.receive(upload['uploadId'],upload['headers'][0]['value'],5,io.BytesIO(b'photo'))
print('ready',flush=True)
__import__('time').sleep(60)
'''
        with tempfile.TemporaryDirectory() as directory:
            item = self.item()
            request = dict(protocolVersion=2, requestId='crash', items=[item])
            process = subprocess.Popen([sys.executable, '-u', '-c', worker, directory, encode(request).decode()],
                cwd=Path(__file__).parent, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                self.assertEqual('ready', process.stdout.readline().strip())
                process.kill()
                process.wait(timeout=5)
                reopened = Store(directory, self.credentials)
                try:
                    self.assertEqual(1, reopened.confirm_pending()['processed'])
                    self.assertEqual(1, reopened.db.execute('SELECT count(*) FROM backups').fetchone()[0])
                    self.assertEqual(5, reopened.cleanup()['freedBytes'])
                finally:
                    reopened.close()
            finally:
                if process.poll() is None: process.kill()
                process.communicate(timeout=5)

    def test_old_catalog_is_rejected_without_modification(self):
        with tempfile.TemporaryDirectory() as directory:
            with sqlite3.connect(Path(directory) / 'backup.sqlite3') as db:
                db.execute('CREATE TABLE uploads(id TEXT)')
            with self.assertRaisesRegex(ValueError, 'Unsupported server catalog'):
                Store(directory, self.credentials)
            with sqlite3.connect(Path(directory) / 'backup.sqlite3') as db:
                self.assertEqual([('uploads',)], db.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall())


if __name__ == '__main__':
    unittest.main()
