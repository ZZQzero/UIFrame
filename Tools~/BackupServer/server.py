#!/usr/bin/env python3
"""UIFrame local backup reference server. Standard library only; localhost by default."""
import argparse
import hashlib
import hmac
import json
import os
import re
import secrets
import sqlite3
import threading
import time
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit, parse_qs

CHUNK_BYTES = 4 * 1024 * 1024
MAX_FILE_BYTES = 512 * 1024 * 1024
HEX = re.compile(r'^[a-f0-9]{64}$')


def file_hash(stream):
    digest = hashlib.sha256()
    for block in iter(lambda: stream.read(131072), b''):
        digest.update(block)
    return digest.hexdigest()


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


class CleanupFileError(Exception):
    """A filesystem failure whose item has been durably isolated."""
    def __init__(self, account, filename, error):
        super().__init__(str(error))
        self.account, self.filename, self.error = account, filename, error

    def describe(self):
        return dict(account=self.account, filename=self.filename, error=str(self.error))


class Store:
    def __init__(self, root, credentials):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.credentials = credentials
        self.active_uploads = {}
        self.lock = threading.RLock()
        # Fixed stripes bound lock memory even when old task history grows.
        self.upload_locks = [threading.RLock() for _ in range(64)]
        self.db = sqlite3.connect(self.root / 'backup.sqlite3', check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.execute('PRAGMA synchronous=FULL')
        self.db.execute('''CREATE TABLE IF NOT EXISTS uploads (
            account TEXT NOT NULL, id TEXT NOT NULL, sha256 TEXT NOT NULL,
            size INTEGER NOT NULL, name TEXT NOT NULL, mime TEXT NOT NULL,
            source TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0,
            epoch TEXT NOT NULL, last_activity REAL NOT NULL,
            cleanup_state INTEGER NOT NULL DEFAULT 0 CHECK(cleanup_state BETWEEN 0 AND 3),
            cleanup_error TEXT,
            PRIMARY KEY(account,id))''')
        self.db.execute('CREATE INDEX IF NOT EXISTS uploads_expiry ON uploads(completed,cleanup_state,last_activity)')
        self.db.execute('''CREATE TABLE IF NOT EXISTS incoming(
            account TEXT NOT NULL, upload_id TEXT NOT NULL, filename TEXT PRIMARY KEY,
            created_at REAL NOT NULL, cleanup_state INTEGER NOT NULL DEFAULT 0 CHECK(cleanup_state BETWEEN 0 AND 2),
            cleanup_error TEXT)''')
        self.db.execute('CREATE INDEX IF NOT EXISTS incoming_upload ON incoming(account,upload_id)')
        self.db.execute('CREATE INDEX IF NOT EXISTS incoming_cleanup ON incoming(cleanup_state,created_at,filename)')
        self.db.commit()

    def upload_lock(self, account, key):
        return self.upload_locks[hash((account, key)) % len(self.upload_locks)]

    def capabilities(self, account):
        return dict(protocolVersion=1, account=account, chunkBytes=CHUNK_BYTES, maxFileBytes=MAX_FILE_BYTES, backgroundUpload=True)

    def authenticate(self, authorization):
        candidate = authorization.removeprefix('Bearer ') if authorization.startswith('Bearer ') else ''
        for item in self.credentials:
            if hmac.compare_digest(candidate, item['token']):
                return item['account']
        return None

    def directory(self, account):
        path = self.root / hashlib.sha256(account.encode()).hexdigest()
        path.mkdir(exist_ok=True)
        return path

    def part(self, account, key):
        return self.directory(account) / (key + '.part')

    def blob(self, row):
        return self.directory(row['account']) / (row['sha256'] + '.blob')

    def get(self, account, key):
        return self.db.execute('SELECT * FROM uploads WHERE account=? AND id=?', (account, key)).fetchone()

    def describe(self, row):
        part = self.part(row['account'], row['id'])
        offset = row['size'] if row['completed'] else (part.stat().st_size if part.exists() else 0)
        return dict(uploadId=row['id'], offset=offset, sha256=row['sha256'], size=row['size'],
                    backupId=row['id'] if row['completed'] else '', completed=bool(row['completed']))

    def begin_activity(self, account, key):
        with self.lock, self.db:
            row = self.get(account, key)
            if row and row['cleanup_state'] != 0:
                return False
            self.active_uploads[(account, key)] = self.active_uploads.get((account, key), 0) + 1
            self.db.execute('UPDATE uploads SET last_activity=? WHERE account=? AND id=?', (time.time(), account, key))
            return True

    def end_activity(self, account, key):
        with self.lock:
            count = self.active_uploads[(account, key)] - 1
            if count:
                self.active_uploads[(account, key)] = count
            else:
                del self.active_uploads[(account, key)]

    def remove_incoming(self, account, filename):
        # This record owns its file even after cancellation removes the upload.
        # A failed deletion remains observable and needs an explicit retry.
        with self.lock, self.db:
            self.db.execute('UPDATE incoming SET cleanup_state=1,cleanup_error=NULL WHERE filename=?', (filename,))
        try:
            file = self.directory(account) / filename
            size = file.stat().st_size if file.exists() else 0
            file.unlink(missing_ok=True)
            sync_directory(self.directory(account))
        except OSError as primary:
            try:
                with self.lock, self.db:
                    changed = self.db.execute('UPDATE incoming SET cleanup_state=2,cleanup_error=? WHERE filename=?', (str(primary), filename)).rowcount
                    if changed != 1:
                        raise RuntimeError('Incoming cleanup owner disappeared')
            except Exception as secondary:
                print('Recording incoming cleanup failure also failed: ' + repr(secondary), file=sys.stderr)
                raise primary
            raise CleanupFileError(account, filename, primary) from primary
        with self.lock, self.db:
            self.db.execute('DELETE FROM incoming WHERE filename=?', (filename,))
        return size

    def cleanup_expired(self, ttl_seconds, maximum=200, retry_failed=False):
        if ttl_seconds <= 0 or not 1 <= maximum <= 200:
            raise ValueError('Positive TTL and 1-200 cleanup page size required')
        cutoff = time.time() - ttl_seconds
        deleted = freed = skipped = 0
        failures = []
        with self.lock:
            incoming = self.db.execute('SELECT account,upload_id,filename FROM incoming WHERE '
                'cleanup_state=1 OR (cleanup_state=0 AND created_at<?) OR (cleanup_state=2 AND ?) '
                'ORDER BY created_at,filename LIMIT ?', (cutoff, retry_failed, maximum)).fetchall()
        for item in incoming:
            account, key = item['account'], item['upload_id']
            with self.upload_lock(account, key):
                with self.lock:
                    current = self.db.execute('SELECT created_at,cleanup_state FROM incoming WHERE filename=?', (item['filename'],)).fetchone()
                    if (not current or self.active_uploads.get((account, key)) or
                        (current['cleanup_state'] == 2 and not retry_failed) or
                        (current['cleanup_state'] == 0 and current['created_at'] >= cutoff)):
                        skipped += 1
                        continue
                try:
                    freed += self.remove_incoming(account, item['filename'])
                except CleanupFileError as error:
                    failures.append(error.describe())
        # Count both kinds of persisted cleanup work against the same page budget.
        remaining = maximum - len(incoming)
        with self.lock:
            candidates = self.db.execute('SELECT account,id FROM uploads WHERE completed=0 AND '
                '(cleanup_state=1 OR (cleanup_state=0 AND last_activity<?) OR (cleanup_state=3 AND ?)) '
                'ORDER BY last_activity,account,id LIMIT ?', (cutoff, retry_failed, remaining)).fetchall()
        for candidate in candidates:
            account, key = candidate['account'], candidate['id']
            with self.upload_lock(account, key):
                with self.lock, self.db:
                    row = self.get(account, key)
                    if (not row or row['completed'] or self.active_uploads.get((account, key)) or
                        row['cleanup_state'] == 2 or (row['cleanup_state'] == 3 and not retry_failed) or
                        (row['cleanup_state'] == 0 and row['last_activity'] >= cutoff)):
                        skipped += 1
                        continue
                    self.db.execute('UPDATE uploads SET cleanup_state=1,cleanup_error=NULL WHERE account=? AND id=?', (account, key))
                try:
                    file = self.part(account, key)
                    if file.exists():
                        size = file.stat().st_size
                        file.unlink()
                        freed += size
                    sync_directory(self.directory(account))
                except OSError as primary:
                    try:
                        with self.lock, self.db:
                            changed = self.db.execute('UPDATE uploads SET cleanup_state=3,cleanup_error=? WHERE account=? AND id=?', (str(primary), account, key)).rowcount
                            if changed != 1:
                                raise RuntimeError('Upload cleanup owner disappeared')
                    except Exception as secondary:
                        print('Recording cleanup failure also failed: ' + repr(secondary), file=sys.stderr)
                        raise primary
                    failures.append(CleanupFileError(account, key + '.part', primary).describe())
                    continue
                with self.lock, self.db:
                    self.db.execute('UPDATE uploads SET cleanup_state=2 WHERE account=? AND id=? AND cleanup_state=1', (account, key))
                deleted += 1
        return dict(expired=deleted, freedBytes=freed, skipped=skipped, failures=failures)

    def close(self):
        self.db.close()


def handler_for(store):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, *_):
            pass  # Do not log bearer tokens or personal filenames.

        def setup(self):
            super().setup()
            self.connection.settimeout(30)

        def reply(self, code, body):
            data = json.dumps(body, ensure_ascii=False).encode()
            self.send_response(code)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Content-Length', str(len(data)))
            self.send_header('Connection', 'close')
            self.end_headers()
            self.close_connection = True
            self.wfile.write(data)

        def json_body(self):
            length = int(self.headers.get('Content-Length', '-1'))
            if not 0 < length <= 65536:
                raise ValueError('Invalid JSON body size')
            raw = self.rfile.read(length)
            if len(raw) != length:
                raise ValueError('Truncated body')
            return json.loads(raw)

        def do_GET(self):
            self.route('GET')

        def do_POST(self):
            self.route('POST')

        def do_PUT(self):
            self.route('PUT')

        def do_DELETE(self):
            self.route('DELETE')

        def background_upload(self, account, key, epoch):
            # Stream without the store lock so Unity can register the next queued file
            # while this OS request is still sending. Recheck identity at commit.
            with store.lock:
                row = store.get(account, key)
                if not row:
                    self.reply(404, {'error': 'Upload not found'})
                    return
                row = dict(row)
                if row['epoch'] != epoch or row['cleanup_state'] != 0:
                    self.reply(409, {'error': 'Upload abandoned or identity changed during transfer'})
                    return
                if self.headers.get('X-Backup-Account-SHA256') != hashlib.sha256(account.encode()).hexdigest():
                    self.reply(403, {'error': 'Configured account mismatch'})
                    return
                length = int(self.headers.get('Content-Length', '-1'))
                if length != row['size']:
                    raise ValueError('File size mismatch')
                directory = store.directory(account)
                filename = secrets.token_hex(16) + '.incoming'
                with store.db:
                    store.db.execute('INSERT INTO incoming(account,upload_id,filename,created_at) VALUES(?,?,?,?)', (account, key, filename, time.time()))
            digest = hashlib.sha256()
            temporary = directory / filename
            try:
                with temporary.open('xb') as output:
                    remaining = length
                    while remaining:
                        data = self.rfile.read(min(131072, remaining))
                        if not data:
                            raise ValueError('Truncated file')
                        output.write(data)
                        digest.update(data)
                        remaining -= len(data)
                    output.flush()
                    os.fsync(output.fileno())
                if digest.hexdigest() != row['sha256']:
                    self.reply(422, {'error': 'Content checksum mismatch'})
                    return
                with store.upload_lock(account, key), store.lock:
                    current = store.get(account, key)
                    if not current or current['cleanup_state'] != 0 or any(current[field] != row[field] for field in ('sha256', 'size', 'source', 'epoch')):
                        self.reply(409, {'error': 'Upload abandoned or identity changed during transfer'})
                        return
                    os.replace(temporary, store.blob(row))
                    sync_directory(directory)
                    with store.db:
                        store.db.execute('UPDATE uploads SET completed=1 WHERE account=? AND id=?', (account, key))
                    store.part(account, key).unlink(missing_ok=True)
                    receipt = store.describe(store.get(account, key))
                self.reply(200, receipt)
            finally:
                if temporary is not None:
                    primary = sys.exc_info()[1]
                    try:
                        store.remove_incoming(account, temporary.name)
                    except Exception as cleanup:
                        if primary is None:
                            raise
                        print('Incoming file cleanup also failed: ' + repr(cleanup), file=sys.stderr)

        def download(self, account, key):
            # Pin an open immutable object while checking its identity, then let slow readers
            # stream independently of queue registration, commits and other accounts.
            with store.lock:
                row = store.get(account, key)
                input_file = store.blob(row).open('rb') if row and row['completed'] else None
            if input_file is None:
                self.reply(404, {'error': 'Backup not found'})
                return
            with input_file:
                self.send_response(200)
                self.send_header('Content-Type', 'application/octet-stream')
                self.send_header('Content-Length', str(os.fstat(input_file.fileno()).st_size))
                self.send_header('Connection', 'close')
                self.end_headers()
                self.close_connection = True
                while data := input_file.read(131072):
                    self.wfile.write(data)

        def route(self, method):
            account = store.authenticate(self.headers.get('Authorization', ''))
            if not account:
                self.reply(401, {'error': 'Authentication required'})
                return
            parsed = urlsplit(self.path)
            parts = parsed.path.strip('/').split('/')
            activity_key = None
            epoch = None
            try:
                if method in ('PUT', 'POST', 'DELETE') and len(parts) >= 3 and parts[:2] == ['v1', 'uploads'] and HEX.fullmatch(parts[2]):
                    with store.lock:
                        if not store.begin_activity(account, parts[2]):
                            self.reply(410, {'error': 'Upload session expired; explicitly register this identity again to resume'})
                            return
                        activity_key = parts[2]
                        admitted = store.get(account, activity_key)
                        epoch = admitted['epoch'] if admitted else None
                if method == 'PUT' and len(parts) == 4 and parts[:2] == ['v1', 'uploads'] and parts[3] == 'background' and HEX.fullmatch(parts[2]):
                    self.background_upload(account, parts[2], epoch)
                    return
                if method == 'GET' and len(parts) == 4 and parts[:2] == ['v1', 'backups'] and parts[3] == 'content' and HEX.fullmatch(parts[2]):
                    self.download(account, parts[2])
                    return
                # Read client bodies before acquiring the shared store lock. Offsets and
                # identities are still checked under that lock immediately before writing.
                if method == 'POST' and parts == ['v1', 'uploads']:
                    json_data = self.json_body()
                if method == 'PUT' and len(parts) == 3 and parts[:2] == ['v1', 'uploads'] and HEX.fullmatch(parts[2]):
                    length = int(self.headers.get('Content-Length', '-1'))
                    if not 0 < length <= CHUNK_BYTES:
                        raise ValueError('Invalid chunk size')
                    chunk_data = self.rfile.read(length)
                    if len(chunk_data) != length:
                        raise ValueError('Truncated chunk')
                if method == 'GET' and parts == ['v1', 'capabilities']:
                    self.reply(200, store.capabilities(account))
                    return
                if method == 'POST' and parts == ['v1', 'uploads']:
                    data = json_data
                    key, digest, size = data['key'], data['sha256'], data['size']
                    if not isinstance(key, str) or not HEX.fullmatch(key) or not isinstance(digest, str) or not HEX.fullmatch(digest):
                        raise ValueError('Invalid content identity')
                    if type(size) is not int or not 0 <= size <= MAX_FILE_BYTES:
                        raise ValueError('Invalid file size')
                    name, mime, source = data['name'], data['mime'], data['source']
                    if not all(isinstance(v, str) and len(v) <= 4096 for v in (name, mime, source)):
                        raise ValueError('Invalid metadata')
                    with store.upload_lock(account, key), store.lock:
                        row = store.get(account, key)
                        if row and (row['sha256'] != digest or row['size'] != size or row['source'] != source):
                            self.reply(409, {'error': 'Idempotency identity mismatch'})
                            return
                        if row and row['cleanup_state'] in (1, 3):
                            self.reply(409, {'error': 'Upload cleanup has not completed'})
                            return
                        if row and row['cleanup_state'] == 2:
                            with store.db:
                                store.db.execute('UPDATE uploads SET cleanup_state=0,cleanup_error=NULL,epoch=?,last_activity=? WHERE account=? AND id=?', (secrets.token_hex(16), time.time(), account, key))
                            row = store.get(account, key)
                        if not row:
                            with store.db:
                                store.db.execute('INSERT INTO uploads(account,id,sha256,size,name,mime,source,epoch,last_activity) VALUES(?,?,?,?,?,?,?,?,?)',
                                                 (account, key, digest, size, name, mime, source, secrets.token_hex(16), time.time()))
                            row = store.get(account, key)
                        receipt = store.describe(row)
                    receipt['capabilities'] = store.capabilities(account)
                    self.reply(200, receipt)
                    return
                if len(parts) >= 3 and parts[:2] == ['v1', 'uploads'] and HEX.fullmatch(parts[2]):
                    key = parts[2]
                    # Serialize this upload's mutations, without blocking other uploads or the SQLite connection during file IO.
                    with store.upload_lock(account, key):
                        with store.lock:
                            row = store.get(account, key)
                        if not row:
                            self.reply(404, {'error': 'Upload not found'})
                            return
                        if row['cleanup_state'] != 0:
                            self.reply(410, {'error': 'Upload session expired'})
                            return
                        if activity_key is not None and row['epoch'] != epoch:
                            self.reply(409, {'error': 'Upload abandoned or identity changed during transfer'})
                            return
                        part = store.part(account, key)
                        if method == 'GET' and len(parts) == 3:
                            self.reply(200, store.describe(row))
                        elif method == 'PUT' and len(parts) == 3:
                            offset = int(parse_qs(parsed.query).get('offset', ['-1'])[0])
                            length = int(self.headers.get('Content-Length', '-1'))
                            actual = part.stat().st_size if part.exists() else 0
                            if row['completed'] or offset != actual:
                                self.reply(409, dict(error='Offset mismatch or upload already committed', **store.describe(row)))
                                return
                            if not 0 < length <= CHUNK_BYTES or actual + length > row['size']:
                                raise ValueError('Invalid chunk size')
                            with part.open('ab') as output:
                                output.write(chunk_data)
                                output.flush()
                                os.fsync(output.fileno())
                            self.reply(200, store.describe(row))
                        elif method == 'POST' and parts[3:] == ['commit']:
                            blob = store.blob(row)
                            candidate = part if part.exists() else blob
                            if not candidate.exists() and row['size'] == 0:
                                part.touch()
                                candidate = part
                            if not candidate.exists() or candidate.stat().st_size != row['size']:
                                self.reply(409, {'error': 'Incomplete upload'})
                                return
                            with candidate.open('rb') as input_file:
                                digest = file_hash(input_file)
                            if digest != row['sha256']:
                                self.reply(422, {'error': 'Content checksum mismatch'})
                                return
                            if candidate == part:
                                if blob.exists():
                                    with blob.open('rb') as existing:
                                        if file_hash(existing) != digest:
                                            self.reply(500, {'error': 'Stored object checksum mismatch'})
                                            return
                                    part.unlink()
                                else:
                                    os.replace(part, blob)
                                sync_directory(blob.parent)
                            with store.lock, store.db:
                                store.db.execute('UPDATE uploads SET completed=1 WHERE account=? AND id=?', (account, key))
                                receipt = store.describe(store.get(account, key))
                            self.reply(200, receipt)
                        elif method == 'DELETE' and len(parts) == 3:
                            if row['completed']:
                                self.reply(409, {'error': 'Committed backups cannot be canceled'})
                                return
                            part.unlink(missing_ok=True)
                            sync_directory(store.directory(account))
                            with store.lock, store.db:
                                store.db.execute('UPDATE incoming SET cleanup_state=1 WHERE account=? AND upload_id=? AND cleanup_state=0', (account, key))
                                store.db.execute('DELETE FROM uploads WHERE account=? AND id=?', (account, key))
                            self.reply(200, {'canceled': True})
                        else:
                            self.reply(404, {'error': 'Route not found'})
                    return
                if method == 'GET' and parts == ['v1', 'backups']:
                    with store.lock:
                        rows = store.db.execute('SELECT * FROM uploads WHERE account=? AND completed=1', (account,)).fetchall()
                        items = [store.describe(row) for row in rows]
                    self.reply(200, {'items': items})
                else:
                    self.reply(404, {'error': 'Route not found'})

            except (ValueError, KeyError, TypeError, json.JSONDecodeError):
                self.reply(400, {'error': 'Invalid request'})
            except (OSError, sqlite3.Error):
                self.reply(500, {'error': 'Storage operation failed'})
            finally:
                if activity_key is not None:
                    store.end_activity(account, activity_key)
    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['init', 'serve'])
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--host', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=8787)
    parser.add_argument('--upload-ttl-days', type=float, help='Explicitly enable incomplete-session expiration (recommended: 7 days)')
    parser.add_argument('--retry-failed-cleanup', action='store_true', help='Explicitly retry recorded cleanup failures on startup')
    args = parser.parse_args()
    config = args.root / 'credentials.json'
    if args.command == 'init':
        args.root.mkdir(parents=True, exist_ok=True)
        fd = os.open(config, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as output:
            json.dump([{'account': 'local-user', 'token': secrets.token_urlsafe(32)}], output, indent=2)
        print('Created credentials.json. Configure the client with this local account and token; do not commit it.')
        return
    if args.upload_ttl_days is not None and args.upload_ttl_days <= 0:
        parser.error('--upload-ttl-days must be positive')
    credentials = json.loads(config.read_text())
    store = Store(args.root, credentials)
    server = ThreadingHTTPServer((args.host, args.port), handler_for(store))
    stop_cleanup = threading.Event()
    cleaner = None
    if args.upload_ttl_days is not None:
        def cleanup_sessions(retry_failed=False):
            result = store.cleanup_expired(args.upload_ttl_days * 86400, retry_failed=retry_failed)
            if result['failures']:
                print('Session cleanup isolated file failures: ' + json.dumps(result['failures']), file=sys.stderr)
        cleanup_sessions(args.retry_failed_cleanup)
        def maintenance():
            while not stop_cleanup.wait(60):
                try:
                    cleanup_sessions()
                except Exception as error:
                    print('Session cleanup stopped: ' + repr(error), file=sys.stderr)
                    return
        cleaner = threading.Thread(target=maintenance, name='BackupCleanup')
        cleaner.start()
    print(f'Backup service listening at http://{args.host}:{args.port}; authenticated protocol v1')
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        stop_cleanup.set()
        if cleaner is not None:
            cleaner.join()
        server.server_close()
        store.close()


if __name__ == '__main__':
    main()
