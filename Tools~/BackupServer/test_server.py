import hashlib
import io
import json
import tempfile
import threading
import unittest
import socket
import subprocess
import sys
import time
from pathlib import Path
from http.server import ThreadingHTTPServer
from urllib.request import Request, urlopen
from urllib.error import HTTPError
from server import Store, handler_for


class BackupProtocolTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(self.directory.name, [{'account': 'alice', 'token': 'test-alice'}, {'account': 'bob', 'token': 'test-bob'}])
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), handler_for(self.store))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = 'http://127.0.0.1:' + str(self.server.server_port)

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.store.close()
        self.directory.cleanup()

    def call(self, method, path, data=None, token='test-alice', headers=None):
        if isinstance(data, dict):
            data = json.dumps(data).encode()
        request = Request(self.url + path, data=data, method=method, headers=dict({'Authorization': 'Bearer ' + token}, **(headers or {})))
        try:
            with urlopen(request, timeout=5) as response:
                raw = response.read()
                return response.status, json.loads(raw) if response.headers.get_content_type() == 'application/json' else raw
        except HTTPError as error:
            return error.code, json.loads(error.read())

    def create(self, data, identity='image', digest=None):
        key = hashlib.sha256(identity.encode()).hexdigest()
        body = dict(key=key, sha256=digest or hashlib.sha256(data).hexdigest(), size=len(data), name='图片.png', mime='image/png', source=identity)
        status, result = self.call('POST', '/v1/uploads', body)
        self.assertEqual(200, status)
        return key, body, result

    def test_expiration_reclaims_only_incomplete_sessions_and_requires_explicit_registration(self):
        key, body, _ = self.create(b'abc')
        self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'ab')
        with self.store.lock, self.store.db:
            self.store.db.execute('UPDATE uploads SET last_activity=0')
        result = self.store.cleanup_expired(1)
        self.assertEqual(1, result['expired'])
        self.assertEqual(2, result['freedBytes'])
        self.assertEqual(410, self.call('GET', '/v1/uploads/' + key)[0])
        self.assertEqual(410, self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'abc')[0])
        status, response = self.call('POST', '/v1/uploads', body)
        self.assertEqual(200, status)
        self.assertEqual(0, response['offset'])
        self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'abc')
        self.call('POST', '/v1/uploads/' + key + '/commit')
        with self.store.lock, self.store.db:
            self.store.db.execute('UPDATE uploads SET last_activity=0')
        self.assertEqual(0, self.store.cleanup_expired(1)['expired'])
        self.assertEqual(b'abc', self.call('GET', '/v1/backups/' + key + '/content')[1])

    def test_cleanup_skips_active_transfer_and_recovers_durable_claim(self):
        key, _, _ = self.create(b'abc')
        self.assertTrue(self.store.begin_activity('alice', key))
        with self.store.lock, self.store.db:
            self.store.db.execute('UPDATE uploads SET last_activity=0')
        self.assertEqual(1, self.store.cleanup_expired(1)['skipped'])
        self.store.end_activity('alice', key)
        incoming = self.store.directory('alice') / 'interrupted.incoming'
        incoming.write_bytes(b'1234')
        with self.store.lock, self.store.db:
            self.store.db.execute('UPDATE uploads SET cleanup_state=1')
            self.store.db.execute('INSERT INTO incoming(account,upload_id,filename,created_at) VALUES(?,?,?,0)', ('alice', key, incoming.name))
        self.assertFalse(self.store.begin_activity('alice', key))
        self.assertEqual(4, self.store.cleanup_expired(1)['freedBytes'])
        self.assertFalse(incoming.exists())

    def test_upload_creation_includes_current_server_capabilities(self):
        _, _, response = self.create(b'abc')
        self.assertEqual('alice', response['capabilities']['account'])
        self.assertEqual(1, response['capabilities']['protocolVersion'])
        self.assertGreater(response['capabilities']['chunkBytes'], 0)

    def test_foreground_request_cannot_cross_cancellation_and_recreation(self):
        key, body, _ = self.create(b'abcdef')
        path = '/v1/uploads/' + key
        with socket.create_connection(('127.0.0.1', self.server.server_port)) as connection:
            connection.settimeout(5)
            connection.sendall((f'PUT {path}?offset=0 HTTP/1.1\r\nHost: localhost\r\n'
                'Authorization: Bearer test-alice\r\nContent-Length: 6\r\n\r\na').encode())
            deadline = time.monotonic() + 3
            while True:
                with self.store.lock:
                    if self.store.active_uploads.get(('alice', key)):
                        break
                self.assertLess(time.monotonic(), deadline)
                time.sleep(0.01)
            self.assertEqual(200, self.call('DELETE', path)[0])
            self.assertEqual(0, self.call('POST', '/v1/uploads', body)[1]['offset'])
            connection.sendall(b'bcdef')
            response = bytearray()
            while data := connection.recv(4096):
                response.extend(data)
            self.assertTrue(response.startswith(b'HTTP/1.1 409'), response)
        self.assertEqual(0, self.call('GET', path)[1]['offset'])
        self.assertEqual(200, self.call('PUT', path + '?offset=0', b'abcdef')[0])
        self.assertTrue(self.call('POST', path + '/commit')[1]['completed'])

    def test_canceled_background_file_is_reclaimed_after_process_termination(self):
        worker = '''
import sys
from http.server import ThreadingHTTPServer
from server import Store, handler_for
store = Store(sys.argv[1], [{'account':'alice','token':'test-alice'}])
server = ThreadingHTTPServer(('127.0.0.1', 0), handler_for(store))
print(server.server_port, flush=True)
server.serve_forever()
'''
        with tempfile.TemporaryDirectory() as directory:
            process = subprocess.Popen([sys.executable, '-u', '-c', worker, directory],
                cwd=Path(__file__).parent, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
            original_url = self.url
            try:
                port = int(process.stdout.readline())
                self.url = 'http://127.0.0.1:' + str(port)
                data = b'x' * (1024 * 1024)
                key, _, _ = self.create(data)
                with socket.create_connection(('127.0.0.1', port)) as connection:
                    connection.settimeout(5)
                    connection.sendall((f'PUT /v1/uploads/{key}/background HTTP/1.1\r\nHost: localhost\r\n'
                        'Authorization: Bearer test-alice\r\n'
                        f'X-Backup-Account-SHA256: {hashlib.sha256(b"alice").hexdigest()}\r\n'
                        f'Content-Length: {len(data)}\r\n\r\n').encode() + data[:262144])
                    deadline = time.monotonic() + 3
                    while not any(p.stat().st_size > 0 for p in Path(directory).rglob('*.incoming')):
                        self.assertLess(time.monotonic(), deadline)
                        time.sleep(0.01)
                    self.assertEqual(200, self.call('DELETE', '/v1/uploads/' + key)[0])
                    process.kill()
                    process.wait(timeout=5)
                reopened = Store(directory, [{'account': 'alice', 'token': 'test-alice'}])
                try:
                    self.assertEqual(0, reopened.db.execute('SELECT count(*) FROM uploads').fetchone()[0])
                    self.assertEqual(1, reopened.db.execute('SELECT count(*) FROM incoming').fetchone()[0])
                    self.assertGreater(reopened.cleanup_expired(86400)['freedBytes'], 0)
                    self.assertEqual(0, reopened.db.execute('SELECT count(*) FROM incoming').fetchone()[0])
                    self.assertEqual([], list(Path(directory).rglob('*.incoming')))
                finally:
                    reopened.close()
            finally:
                self.url = original_url
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
                process.stdout.close()

    def test_incoming_cleanup_failure_requires_explicit_retry(self):
        from unittest.mock import patch
        file = self.store.directory('alice') / 'orphan.incoming'
        file.write_bytes(b'data')
        with self.store.lock, self.store.db:
            self.store.db.execute('INSERT INTO incoming(account,upload_id,filename,created_at) VALUES(?,?,?,0)',
                ('alice', 'removed-upload', file.name))
        with patch.object(Path, 'unlink', side_effect=OSError('injected deletion failure')):
            result = self.store.cleanup_expired(1)
            self.assertEqual(1, len(result['failures']))
            self.assertEqual('injected deletion failure', result['failures'][0]['error'])
        self.assertEqual(2, self.store.db.execute('SELECT cleanup_state FROM incoming').fetchone()[0])
        self.assertEqual(0, self.store.cleanup_expired(1)['freedBytes'])
        self.assertTrue(file.exists())
        self.assertEqual(4, self.store.cleanup_expired(1, retry_failed=True)['freedBytes'])
        self.assertFalse(file.exists())

    def test_isolated_cleanup_failures_do_not_stop_other_files_or_uploads(self):
        from unittest.mock import patch
        incoming = self.store.directory('alice') / 'failed.incoming'
        healthy = self.store.directory('bob') / 'healthy.incoming'
        incoming.write_bytes(b'bad'); healthy.write_bytes(b'good')
        bad_key, _, _ = self.create(b'failed part', identity='failed-part')
        good_key, _, _ = self.create(b'healthy part', identity='healthy-part')
        self.call('PUT', '/v1/uploads/' + bad_key + '?offset=0', b'failed part')
        self.call('PUT', '/v1/uploads/' + good_key + '?offset=0', b'healthy part')
        with self.store.lock, self.store.db:
            self.store.db.execute('UPDATE uploads SET last_activity=0')
            self.store.db.executemany('INSERT INTO incoming(account,upload_id,filename,created_at) VALUES(?,?,?,?)',
                [('alice', 'old-a', incoming.name, 0), ('bob', 'old-b', healthy.name, 1)])
        original = Path.unlink
        def remove(path, *args, **kwargs):
            if path in (incoming, self.store.part('alice', bad_key)):
                raise PermissionError('fixture file is locked')
            return original(path, *args, **kwargs)
        with patch.object(Path, 'unlink', remove):
            result = self.store.cleanup_expired(1)
        self.assertEqual(2, len(result['failures']))
        self.assertEqual(1, result['expired'])
        self.assertFalse(healthy.exists())
        self.assertFalse(self.store.part('alice', good_key).exists())
        self.assertEqual(2, self.store.db.execute('SELECT cleanup_state FROM incoming').fetchone()[0])
        self.assertEqual(3, self.store.get('alice', bad_key)['cleanup_state'])
        self.assertEqual([], self.store.cleanup_expired(1)['failures'])
        self.assertTrue(incoming.exists()); self.assertTrue(self.store.part('alice', bad_key).exists())
        result = self.store.cleanup_expired(1, retry_failed=True)
        self.assertEqual([], result['failures']); self.assertEqual(1, result['expired'])
        self.assertFalse(incoming.exists()); self.assertFalse(self.store.part('alice', bad_key).exists())
        key, body, _ = self.create(b'normal', identity='new-independent')
        self.assertEqual(200, self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'normal')[0])
        self.assertEqual(200, self.call('POST', '/v1/uploads/' + key + '/commit')[0])

    def test_cleanup_recording_failure_preserves_primary_and_stops_batch(self):
        from unittest.mock import patch
        for table in ('incoming', 'uploads'):
            with self.subTest(table=table):
                if table == 'incoming':
                    file = self.store.directory('alice') / 'record-failure.incoming'
                    file.write_bytes(b'data')
                    with self.store.db:
                        self.store.db.execute('INSERT INTO incoming(account,upload_id,filename,created_at) VALUES(?,?,?,0)', ('alice', 'old', file.name))
                    failed_state = 2
                else:
                    key, _, _ = self.create(b'data', identity='record-failure')
                    self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'data')
                    with self.store.db: self.store.db.execute('UPDATE uploads SET last_activity=0')
                    failed_state = 3
                with self.store.db:
                    self.store.db.execute(f"CREATE TEMP TRIGGER reject_cleanup BEFORE UPDATE ON {table} WHEN new.cleanup_state={failed_state} BEGIN SELECT RAISE(ABORT,'fixture persistence failure'); END")
                failure = PermissionError('fixture primary failure')
                try:
                    with patch.object(Path, 'unlink', side_effect=failure):
                        with self.assertRaises(PermissionError) as caught:
                            self.store.cleanup_expired(1)
                    self.assertIs(failure, caught.exception)
                finally:
                    with self.store.db: self.store.db.execute('DROP TRIGGER reject_cleanup')
                    self.store.cleanup_expired(1)

    @unittest.skipIf(sys.platform == 'win32', 'Requires POSIX directory permissions')
    def test_server_starts_with_an_isolated_stale_file_failure(self):
        import os
        import select
        if os.geteuid() == 0:
            self.skipTest('Root bypasses directory permission checks')
        with tempfile.TemporaryDirectory() as directory:
            credentials = [{'account': 'a', 'token': 'fixture-token'}]
            root = Path(directory); (root / 'credentials.json').write_text(json.dumps(credentials))
            store = Store(root, credentials); folder = store.directory('a')
            (folder / 'old.incoming').write_bytes(b'data')
            with store.db:
                store.db.execute('INSERT INTO incoming(account,upload_id,filename,created_at) VALUES(?,?,?,0)', ('a', 'old', 'old.incoming'))
            store.close(); os.chmod(folder, 0o500)
            process = subprocess.Popen([sys.executable, '-u', str(Path(__file__).with_name('server.py')), 'serve', '--root', directory, '--port', '0', '--upload-ttl-days', '1'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                readable, _, _ = select.select([process.stdout], [], [], 10)
                self.assertTrue(readable, 'Server did not finish startup')
                self.assertIn('Backup service listening', process.stdout.readline())
                self.assertIsNone(process.poll())
                with __import__('sqlite3').connect(root / 'backup.sqlite3') as db:
                    self.assertEqual(2, db.execute('SELECT cleanup_state FROM incoming').fetchone()[0])
            finally:
                process.terminate(); process.communicate(timeout=5); os.chmod(folder, 0o700)

    def test_cleanup_rechecks_failure_recorded_after_candidate_selection(self):
        from contextlib import contextmanager
        from unittest.mock import patch
        key, _, _ = self.create(b'upload')
        file = self.store.directory('alice') / 'late-failure.incoming'
        file.write_bytes(b'data')
        with self.store.lock, self.store.db:
            self.store.db.execute('UPDATE uploads SET last_activity=0')
            self.store.db.execute('INSERT INTO incoming(account,upload_id,filename,created_at) VALUES(?,?,?,0)',
                ('alice', key, file.name))
        original = self.store.upload_lock
        @contextmanager
        def concurrent_failure(account, identity):
            with original(account, identity):
                # Another owner finishes with a cleanup failure after selection,
                # before this maintenance pass acquires the upload lock.
                with self.store.lock, self.store.db:
                    self.store.db.execute('UPDATE incoming SET cleanup_state=2')
                    self.store.db.execute('UPDATE uploads SET cleanup_state=3')
                yield
        with patch.object(self.store, 'upload_lock', concurrent_failure):
            self.assertEqual(0, self.store.cleanup_expired(1)['freedBytes'])
        self.assertTrue(file.exists())
        self.assertEqual(0, self.store.cleanup_expired(1)['expired'])
        result = self.store.cleanup_expired(1, retry_failed=True)
        self.assertEqual(4, result['freedBytes'])
        self.assertEqual(1, result['expired'])

    def test_slow_commit_hash_does_not_block_other_uploads_or_allow_same_upload_deletion(self):
        from unittest.mock import patch
        import server as implementation
        key, _, _ = self.create(b'abc')
        self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'abc')
        entered, release, delete_started, deleted = (threading.Event() for _ in range(4))
        original = implementation.file_hash
        outcomes = []
        def blocked_hash(stream):
            entered.set()
            if not release.wait(5):
                raise TimeoutError('test did not release checksum')
            return original(stream)
        def delete():
            delete_started.set()
            outcomes.append(self.call('DELETE', '/v1/uploads/' + key)[0]); deleted.set()
        with patch.object(implementation, 'file_hash', blocked_hash):
            commit = threading.Thread(target=lambda: outcomes.append(self.call('POST', '/v1/uploads/' + key + '/commit')[0]))
            removal = threading.Thread(target=delete)
            commit.start()
            try:
                self.assertTrue(entered.wait(2))
                self.assertEqual(200, self.call('GET', '/v1/capabilities')[0])
                # Choose a different stripe so this also exercises independent metadata mutation.
                identity = 'other'
                while self.store.upload_lock('alice', hashlib.sha256(identity.encode()).hexdigest()) is self.store.upload_lock('alice', key):
                    identity += 'x'
                self.create(b'next', identity=identity)
                removal.start(); self.assertTrue(delete_started.wait(2))
                self.assertFalse(deleted.wait(0.05))
            finally:
                release.set(); commit.join(5)
                if removal.ident is not None: removal.join(5)
        self.assertCountEqual([200, 409], outcomes)
        self.assertTrue(self.call('GET', '/v1/uploads/' + key)[1]['completed'])

    def test_native_file_upload_commits_and_is_idempotent(self):
        data = b'background-image' * 400000  # larger than the foreground chunk limit
        key, _, _ = self.create(data)
        headers = {'X-Backup-Account-SHA256': hashlib.sha256(b'alice').hexdigest()}
        self.assertTrue(self.call('GET', '/v1/capabilities')[1]['backgroundUpload'])
        for _ in range(2):
            status, receipt = self.call('PUT', '/v1/uploads/' + key + '/background', data, headers=headers)
            self.assertEqual(200, status)
            self.assertTrue(receipt['completed'])
            self.assertEqual(len(data), receipt['offset'])
            self.assertEqual(key, receipt['backupId'])
        self.assertEqual(data, self.call('GET', '/v1/backups/' + key + '/content')[1])
        self.assertEqual(1, len(self.call('GET', '/v1/backups')[1]['items']))
        self.assertEqual([], list(self.store.root.rglob('*.incoming')))

    def test_native_upload_rejects_account_size_and_checksum_mismatch(self):
        key, _, _ = self.create(b'abc')
        path = '/v1/uploads/' + key + '/background'
        headers = {'X-Backup-Account-SHA256': hashlib.sha256(b'alice').hexdigest()}
        self.assertEqual(403, self.call('PUT', path, b'abc')[0])
        self.assertEqual(400, self.call('PUT', path, b'ab', headers=headers)[0])
        self.assertEqual(422, self.call('PUT', path, b'xyz', headers=headers)[0])
        self.assertFalse(self.call('GET', '/v1/uploads/' + key)[1]['completed'])
        self.assertEqual([], list(self.store.root.rglob('*.incoming')))
        self.assertEqual(200, self.call('PUT', path, b'abc', headers=headers)[0])
        self.assertEqual(422, self.call('PUT', path, b'xyz', headers=headers)[0])
        self.assertEqual(b'abc', self.call('GET', '/v1/backups/' + key + '/content')[1])

    def test_interrupted_native_upload_preserves_foreground_offset(self):
        import socket
        key, _, _ = self.create(b'abcdef')
        self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'ab')
        account_hash = hashlib.sha256(b'alice').hexdigest()
        with socket.create_connection(('127.0.0.1', self.server.server_port)) as connection:
            request = (f'PUT /v1/uploads/{key}/background HTTP/1.1\r\nHost: localhost\r\n'
                       f'Authorization: Bearer test-alice\r\nX-Backup-Account-SHA256: {account_hash}\r\n'
                       'Content-Length: 6\r\n\r\nxy').encode()
            connection.sendall(request)
            connection.shutdown(socket.SHUT_WR)
            while connection.recv(4096):
                pass
        self.assertEqual(2, self.call('GET', '/v1/uploads/' + key)[1]['offset'])
        self.assertFalse(self.call('GET', '/v1/uploads/' + key)[1]['completed'])
        self.assertEqual([], list(self.store.root.rglob('*.incoming')))

    def test_slow_native_upload_does_not_block_registration_or_revive_abandoned_session(self):
        import socket
        import time
        key, _, _ = self.create(b'abcdef')
        account_hash = hashlib.sha256(b'alice').hexdigest()
        with socket.create_connection(('127.0.0.1', self.server.server_port)) as connection:
            connection.settimeout(5)
            request = (f'PUT /v1/uploads/{key}/background HTTP/1.1\r\nHost: localhost\r\n'
                       f'Authorization: Bearer test-alice\r\nX-Backup-Account-SHA256: {account_hash}\r\n'
                       'Content-Length: 6\r\n\r\nab').encode()
            connection.sendall(request)
            deadline = time.monotonic() + 3
            while not list(self.store.root.rglob('*.incoming')):
                self.assertLess(time.monotonic(), deadline)
                time.sleep(0.01)
            self.assertEqual(200, self.call('GET', '/v1/capabilities')[0])
            self.create(b'second image', identity='next')
            self.assertEqual(200, self.call('DELETE', '/v1/uploads/' + key)[0])
            connection.sendall(b'cdef')
            result = bytearray()
            while chunk := connection.recv(4096):
                result.extend(chunk)
            self.assertTrue(result.startswith(b'HTTP/1.1 409'), result)
        self.assertEqual(404, self.call('GET', '/v1/uploads/' + key)[0])
        self.assertEqual([], list(self.store.root.rglob('*.incoming')))

    def test_resumable_upload_commit_download(self):
        data = b'image-content-123' * 1000
        key, body, _ = self.create(data)
        path = '/v1/uploads/' + key
        self.assertEqual(200, self.call('PUT', path + '?offset=0', data[:500])[0])
        self.assertEqual(500, self.call('GET', path)[1]['offset'])
        self.assertEqual(409, self.call('PUT', path + '?offset=0', data[:500])[0])
        self.assertEqual(200, self.call('PUT', path + '?offset=500', data[500:])[0])
        self.assertTrue(self.call('POST', path + '/commit')[1]['completed'])
        self.assertTrue(self.call('POST', '/v1/uploads', body)[1]['completed'])
        self.assertTrue(self.call('POST', path + '/commit')[1]['completed'])
        self.assertEqual(data, self.call('GET', '/v1/backups/' + key + '/content')[1])
        self.assertEqual(1, len(self.call('GET', '/v1/backups')[1]['items']))

    def test_slow_download_does_not_block_other_requests(self):
        data = b'photo-content'
        key, _, _ = self.create(data)
        self.call('PUT', '/v1/uploads/' + key + '?offset=0', data)
        self.call('POST', '/v1/uploads/' + key + '/commit')
        entered, release = threading.Event(), threading.Event()

        class SlowWriter(io.BytesIO):
            def write(self, value):
                entered.set()
                if not release.wait(4):
                    raise TimeoutError('Download was not released')
                return super().write(value)

        handler_type = handler_for(self.store)
        handler = handler_type.__new__(handler_type)
        handler.path = '/v1/backups/' + key + '/content'
        handler.headers = {'Authorization': 'Bearer test-alice'}
        handler.wfile = SlowWriter()
        handler.send_response = lambda *args: None
        handler.send_header = lambda *args: None
        handler.end_headers = lambda: None
        download = threading.Thread(target=lambda: handler.route('GET'))
        download.start()
        try:
            self.assertTrue(entered.wait(2))
            self.assertTrue(self.store.lock.acquire(timeout=0.5), 'Slow download holds the store lock')
            self.store.lock.release()
            self.assertEqual(200, self.call('GET', '/v1/capabilities')[0])
            self.create(b'next photo', identity='independent')
        finally:
            release.set()
            download.join(5)
        self.assertFalse(download.is_alive())
        self.assertEqual(data, handler.wfile.getvalue())

    def test_slow_foreground_upload_body_does_not_hold_store_lock(self):
        key, _, _ = self.create(b'abc')
        entered, release = threading.Event(), threading.Event()

        class SlowReader(io.BytesIO):
            def read(self, size=-1):
                entered.set()
                if not release.wait(4):
                    raise TimeoutError('Upload was not released')
                return super().read(size)

        handler_type = handler_for(self.store)
        handler = handler_type.__new__(handler_type)
        handler.path = '/v1/uploads/' + key + '?offset=0'
        handler.headers = {'Authorization': 'Bearer test-alice', 'Content-Length': '3'}
        handler.rfile = SlowReader(b'abc')
        responses = []
        handler.reply = lambda code, body: responses.append((code, body))
        upload = threading.Thread(target=lambda: handler.route('PUT'))
        upload.start()
        try:
            self.assertTrue(entered.wait(2))
            self.assertTrue(self.store.lock.acquire(timeout=0.5), 'Slow upload holds the store lock')
            self.store.lock.release()
            self.assertEqual(200, self.call('GET', '/v1/capabilities')[0])
        finally:
            release.set()
            upload.join(5)
        self.assertFalse(upload.is_alive())
        self.assertEqual(200, responses[0][0])

    def test_authentication_and_account_isolation(self):
        key, _, _ = self.create(b'abc')
        self.assertEqual(401, self.call('GET', '/v1/capabilities', token='wrong')[0])
        self.assertEqual(404, self.call('GET', '/v1/uploads/' + key, token='test-bob')[0])
        self.assertEqual([], self.call('GET', '/v1/backups', token='test-bob')[1]['items'])

    def test_incomplete_and_wrong_checksum_never_complete(self):
        key, _, _ = self.create(b'abc', digest='0'*64)
        path = '/v1/uploads/' + key
        self.assertEqual(409, self.call('POST', path + '/commit')[0])
        self.call('PUT', path + '?offset=0', b'abc')
        self.assertEqual(422, self.call('POST', path + '/commit')[0])
        self.assertFalse(self.call('GET', path)[1]['completed'])

    def test_same_content_preserves_sources_without_duplicate_blobs(self):
        for identity in ['camera/one', 'album/two']:
            key, _, _ = self.create(b'abc', identity)
            self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'abc')
            self.call('POST', '/v1/uploads/' + key + '/commit')
        self.assertEqual(2, len(self.call('GET', '/v1/backups')[1]['items']))
        self.assertEqual(1, len(list(self.store.root.rglob('*.blob'))))

    def test_idempotency_mismatch_and_invalid_paths(self):
        key, body, _ = self.create(b'abc')
        body['sha256'] = 'f'*64
        self.assertEqual(409, self.call('POST', '/v1/uploads', body)[0])
        body['key'] = '../../escape'
        self.assertEqual(400, self.call('POST', '/v1/uploads', body)[0])
        self.assertEqual(404, self.call('GET', '/v1/uploads/not-a-key')[0])

    def test_partial_cancel_does_not_delete_committed_backup(self):
        key, _, _ = self.create(b'abc')
        path = '/v1/uploads/' + key
        self.call('PUT', path + '?offset=0', b'a')
        self.assertEqual(200, self.call('DELETE', path)[0])
        self.assertEqual(404, self.call('GET', path)[0])
        self.create(b'abc')
        self.call('PUT', path + '?offset=0', b'abc')
        self.call('POST', path + '/commit')
        self.assertEqual(409, self.call('DELETE', path)[0])

    def test_server_reopen_retains_offset_and_committed_identity(self):
        key, _, _ = self.create(b'abc')
        self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'a')
        with self.store.lock:
            second = Store(self.directory.name, self.store.credentials)
            self.assertEqual(1, second.describe(second.get('alice', key))['offset'])
            second.close()

    def test_commit_recovers_object_rename_before_database_update(self):
        key, _, _ = self.create(b'abc')
        self.call('PUT', '/v1/uploads/' + key + '?offset=0', b'abc')
        with self.store.lock:
            row = self.store.get('alice', key)
            self.store.part('alice', key).replace(self.store.blob(row))
        self.assertTrue(self.call('POST', '/v1/uploads/' + key + '/commit')[1]['completed'])


if __name__ == '__main__':
    unittest.main()
