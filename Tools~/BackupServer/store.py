"""Durable backup facts. Network and file IO never run inside a DB transaction."""
import hashlib
import hmac
import os
import secrets
import sqlite3
import sys
import threading
import time
from pathlib import Path

from protocol import (CHECK_INTERVAL_MS, LIMITS, MAX_DESCRIPTOR_BYTES, MAX_FILE_BYTES,
                      UPLOAD_TTL_MS, ProtocolError, encode, fingerprint, identity)
import json


def now_ms():
    return time.time_ns() // 1_000_000


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


class Store:
    def __init__(self, root, credentials):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.credentials = credentials
        self.storage_origin = None
        self.lock = threading.RLock()
        self.upload_locks = [threading.RLock() for _ in range(64)]
        self.content_locks = [threading.RLock() for _ in range(64)]
        self.wake = threading.Event()
        self.stop = threading.Event()
        self.worker = None
        self.worker_error = None
        self.db = sqlite3.connect(self.root / 'backup.sqlite3', check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        try:
            version = self.db.execute('PRAGMA user_version').fetchone()[0]
            tables = self.db.execute("SELECT count(*) FROM sqlite_master WHERE type='table'").fetchone()[0]
            if version != 2 and (version != 0 or tables != 0):
                raise ValueError('Unsupported server catalog. Use a new explicit data directory for protocol v2.')
            self.db.execute('PRAGMA journal_mode=WAL')
            self.db.execute('PRAGMA synchronous=FULL')
            if not tables:
                self.db.executescript('BEGIN IMMEDIATE;\n' + Path(__file__).with_name('schema.sql').read_text() + '\nCOMMIT;')
            with self.db:
                self.db.execute("INSERT OR IGNORE INTO settings VALUES('signing_key',?)", (secrets.token_bytes(32),))
                # A dead process owns no open body streams. Ready work remains ready;
                # lost notifications therefore do not lose verification work.
                self.db.execute("UPDATE incoming SET state='cleanup',error='Process ended before body publication' WHERE state='writing'")
            self.signing_key = bytes(self.db.execute("SELECT value FROM settings WHERE key='signing_key'").fetchone()[0])
            os.chmod(self.root / 'backup.sqlite3', 0o600)
        except BaseException:
            self.db.close()
            raise

    def close(self):
        self.stop.set()
        self.wake.set()
        if self.worker:
            self.worker.join()
        self.db.close()

    def authenticate(self, authorization):
        if not authorization.startswith('Bearer '):
            return None
        token = authorization[7:]
        for credential in self.credentials:
            if hmac.compare_digest(token.encode(), credential['token'].encode()):
                return credential['account']
        return None

    def upload_lock(self, upload_id):
        return self.upload_locks[int(hashlib.sha256(upload_id.encode()).hexdigest()[:8], 16) % len(self.upload_locks)]

    def directory(self, account):
        path = self.root / hashlib.sha256(account.encode()).hexdigest()
        path.mkdir(exist_ok=True, mode=0o700)
        return path

    def _attempt(self, account, item):
        return self.db.execute('SELECT * FROM attempts WHERE account=? AND task=? AND generation=?',
                               (account, item['clientTaskId'], item['attemptGeneration'])).fetchone()

    def _metadata(self, row):
        metadata = json.loads(self.db.execute('SELECT metadata FROM tasks WHERE account=? AND id=?',
                                             (row['account'], row['task'])).fetchone()[0])
        return dict(metadata, attemptGeneration=row['generation'])

    def _receipt(self, row, metadata):
        backup = self.db.execute('SELECT * FROM backups WHERE account=? AND namespace=? AND source=? AND version=?',
            (row['account'], metadata['sourceNamespace'], metadata['sourceId'], metadata['sourceVersion'])).fetchone()
        if backup is None or backup['sha256'] != metadata['sha256'] or backup['size'] != metadata['byteCount']:
            raise RuntimeError('Confirmed attempt lacks a matching backup')
        return dict(identity(metadata), backupId=backup['id'], sourceNamespace=backup['namespace'],
                    sourceId=backup['source'], sourceVersion=backup['version'], sha256=backup['sha256'],
                    byteCount=backup['size'], confirmedAt=backup['confirmed_at'])

    def _confirm(self, row, metadata):
        self.db.execute('''INSERT OR IGNORE INTO backups(id,account,namespace,source,version,sha256,size,name,mime,confirmed_at)
            VALUES(?,?,?,?,?,?,?,?,?,?)''', (secrets.token_hex(16), row['account'], metadata['sourceNamespace'],
            metadata['sourceId'], metadata['sourceVersion'], metadata['sha256'], metadata['byteCount'],
            metadata['name'], metadata['mimeType'], now_ms()))
        receipt = self._receipt(row, metadata)
        self.db.execute('UPDATE sources SET backup_id=? WHERE account=? AND namespace=? AND id=? AND version=?',
            (receipt['backupId'], row['account'], metadata['sourceNamespace'], metadata['sourceId'], metadata['sourceVersion']))
        self.db.execute("UPDATE attempts SET state='confirmed' WHERE upload_id=?", (row['upload_id'],))

    def _signature(self, row, expires):
        payload = encode([row['account'], row['task'], row['generation'], row['upload_id'], expires, 'PUT'])
        return hmac.new(self.signing_key, payload, hashlib.sha256).hexdigest()

    def _describe(self, account, item, refresh=False):
        result = identity(item)
        row = self._attempt(account, item)
        if row is None:
            return dict(result, status='Absent')
        if row['state'] == 'canceled':
            return dict(result, status='Canceled')
        if row['state'] == 'rejected':
            return dict(result, status='Rejected', error=dict(code=row['error_code'], message=row['error_message']))
        metadata = self._metadata(row)
        if row['state'] == 'confirmed':
            return dict(result, status='Confirmed', receipt=self._receipt(row, metadata))
        if row['state'] == 'verifying':
            return dict(result, status='Verifying', nextCheckAt=now_ms() + CHECK_INTERVAL_MS)
        expires = row['expires_at']
        if refresh and expires <= now_ms():
            expires = now_ms() + UPLOAD_TTL_MS
            self.db.execute('UPDATE attempts SET expires_at=? WHERE upload_id=?', (expires, row['upload_id']))
        descriptor = dict(result, uploadId=row['upload_id'], sha256=metadata['sha256'], byteCount=metadata['byteCount'],
            url=self.storage_origin + '/objects/' + row['upload_id'],
            headers=[dict(name='X-Upload-Authorization', value=str(expires) + '.' + self._signature(row, expires))],
            expiresAt=expires, successStatusCodes=[200, 201, 204])
        if len(encode(descriptor)) > MAX_DESCRIPTOR_BYTES:
            raise RuntimeError('Upload descriptor exceeds wire budget')
        return dict(result, status='UploadRequired', upload=descriptor)

    def control(self, account, operation, request):
        if self.storage_origin is None:
            raise RuntimeError('Private storage origin has not been configured')
        parameter_hash = fingerprint(dict(operation=operation, request=request))
        with self.lock, self.db:
            existing = self.db.execute('SELECT fingerprint FROM requests WHERE account=? AND id=?', (account, request['requestId'])).fetchone()
            if existing and existing[0] != parameter_hash:
                raise ProtocolError(409, 'RequestConflict', 'requestId already belongs to different parameters')
            self.db.execute('INSERT OR IGNORE INTO requests VALUES(?,?,?,?)', (account, request['requestId'], operation, parameter_hash))
            results = []
            for item in request['items']:
                if operation == 'plans':
                    result = self._plan(account, item)
                elif operation == 'cancellations':
                    row = self._attempt(account, item)
                    if row is None:
                        # This tombstone also wins against a Plan that has not arrived.
                        self.db.execute("INSERT INTO attempts(account,task,generation,upload_id,state,created_at,expires_at) VALUES(?,?,?,?,'canceled',?,0)",
                            (account, item['clientTaskId'], item['attemptGeneration'], secrets.token_hex(16), now_ms()))
                    elif row['state'] != 'confirmed':
                        self.db.execute("UPDATE attempts SET state='canceled' WHERE upload_id=?", (row['upload_id'],))
                    result = self._describe(account, item)
                else:
                    result = self._describe(account, item)
                results.append(result)
            return dict(protocolVersion=2, requestId=request['requestId'], account=account,
                        serverTime=now_ms(), limits=LIMITS, items=results)

    def _plan(self, account, item):
        def rejected(code, message):
            return dict(identity(item), status='Rejected', error=dict(code=code, message=message))
        if item['byteCount'] > MAX_FILE_BYTES:
            return rejected('FileTooLarge', 'File exceeds 512 MiB')
        row = self._attempt(account, item)
        if row and row['state'] == 'canceled':
            return self._describe(account, item)
        metadata = dict(item)
        # Attempts share immutable task metadata, but generation is a separate fact.
        metadata.pop('attemptGeneration')
        task = self.db.execute('SELECT fingerprint FROM tasks WHERE account=? AND id=?', (account, item['clientTaskId'])).fetchone()
        if task and task[0] != fingerprint(metadata):
            return rejected('TaskConflict', 'Task identity already belongs to different metadata')
        source_key = (account, item['sourceNamespace'], item['sourceId'], item['sourceVersion'])
        source = self.db.execute('SELECT sha256,size FROM sources WHERE account=? AND namespace=? AND id=? AND version=?', source_key).fetchone()
        if source and (source['sha256'] != item['sha256'] or source['size'] != item['byteCount']):
            return rejected('SourceConflict', 'Source version already belongs to different content')
        self.db.execute('INSERT OR IGNORE INTO tasks VALUES(?,?,?,?)', (account, item['clientTaskId'], encode(metadata).decode(), fingerprint(metadata)))
        self.db.execute('INSERT OR IGNORE INTO sources(account,namespace,id,version,sha256,size) VALUES(?,?,?,?,?,?)',
                        (*source_key, item['sha256'], item['byteCount']))
        if row is None:
            self.db.execute("INSERT INTO attempts(account,task,generation,upload_id,state,created_at,expires_at) VALUES(?,?,?,?,'upload',?,?)",
                            (account, item['clientTaskId'], item['attemptGeneration'], secrets.token_hex(16), now_ms(), now_ms() + UPLOAD_TTL_MS))
            row = self._attempt(account, item)
            content = self.db.execute('SELECT verified FROM contents WHERE account=? AND sha256=? AND size=? AND cleanup_state=0',
                                      (account, item['sha256'], item['byteCount'])).fetchone()
            if content and content[0]:
                self._confirm(row, item)
        return self._describe(account, item, refresh=True)

    def receive(self, upload_id, authorization, length, stream):
        try:
            expires_text, signature = authorization.split('.', 1)
            expires = int(expires_text)
        except (ValueError, AttributeError):
            raise ProtocolError(403, 'UploadAuthorization', 'Invalid upload authorization')
        with self.upload_lock(upload_id):
            with self.lock:
                row = self.db.execute('SELECT * FROM attempts WHERE upload_id=?', (upload_id,)).fetchone()
                if row is None or not hmac.compare_digest(signature, self._signature(row, expires)) or expires <= now_ms():
                    raise ProtocolError(403, 'UploadAuthorization', 'Upload authorization is invalid or expired')
                if row['state'] in ('canceled', 'rejected'):
                    raise ProtocolError(409, 'AttemptClosed', 'This attempt can no longer accept content')
                metadata = self._metadata(row)
                if length != metadata['byteCount']:
                    raise ProtocolError(400, 'SizeMismatch', 'Content-Length does not match upload authorization')
            incoming_id = secrets.token_hex(16)
            path = self.directory(row['account']) / (incoming_id + '.incoming')
            with self.lock, self.db:
                self.db.execute("INSERT INTO incoming(id,upload_id,path,state,created_at) VALUES(?,?,?,'writing',?)",
                                (incoming_id, upload_id, str(path), now_ms()))
        # Each incoming file has one stream owner. Its durable 'writing' row
        # excludes it from verification/GC; network IO needs no attempt-wide lock.
        # A duplicate slow PUT cannot hold up already closed files or other items.
        try:
            with path.open('xb') as output:
                remaining = length
                while remaining:
                    block = stream.read(min(131072, remaining))
                    if not block:
                        raise ProtocolError(400, 'TruncatedBody', 'File body ended before Content-Length')
                    output.write(block)
                    remaining -= len(block)
                output.flush()
                os.fsync(output.fileno())
            sync_directory(path.parent)
            with self.upload_lock(upload_id), self.lock, self.db:
                current = self.db.execute('SELECT state FROM attempts WHERE upload_id=?', (upload_id,)).fetchone()[0]
                if current in ('canceled', 'rejected'):
                    self.db.execute("UPDATE incoming SET state='cleanup' WHERE id=?", (incoming_id,))
                else:
                    self.db.execute("UPDATE incoming SET state='ready' WHERE id=?", (incoming_id,))
                    self.db.execute("UPDATE attempts SET state='verifying' WHERE upload_id=? AND state='upload'", (upload_id,))
            self.wake.set()
            if current in ('canceled', 'rejected'):
                raise ProtocolError(409, 'AttemptClosed', 'Attempt closed during upload')
        except BaseException as primary:
            try:
                with self.lock, self.db:
                    self.db.execute("UPDATE incoming SET state='cleanup',error=? WHERE id=? AND state='writing'", (str(primary), incoming_id))
            except Exception as secondary:
                print('Recording incomplete body failed: ' + repr(secondary), file=sys.stderr)
            raise

    def confirm_one(self, incoming):
        with self.upload_lock(incoming['upload_id']):
            with self.lock:
                live = self.db.execute('SELECT * FROM incoming WHERE id=?', (incoming['id'],)).fetchone()
                if live is None or live['state'] != 'ready':
                    return
                row = self.db.execute('SELECT * FROM attempts WHERE upload_id=?', (incoming['upload_id'],)).fetchone()
                metadata = self._metadata(row)
            path = Path(live['path'])
            with path.open('rb') as source:
                actual_size = os.fstat(source.fileno()).st_size
                digest = file_hash(source)
            if actual_size != metadata['byteCount'] or digest != metadata['sha256']:
                with self.lock, self.db:
                    self.db.execute("UPDATE attempts SET state='rejected',error_code='ChecksumMismatch',error_message='Stored bytes do not match registered content' WHERE upload_id=? AND state IN ('upload','verifying')", (row['upload_id'],))
                    self.db.execute("UPDATE incoming SET state='cleanup' WHERE id=?", (incoming['id'],))
                return
            stripe = self.content_locks[int(digest[:8], 16) % len(self.content_locks)]
            with stripe:
                blob = self.directory(row['account']) / (digest + '-' + str(actual_size) + '.blob')
                with self.lock, self.db:
                    state = self.db.execute('SELECT state FROM attempts WHERE upload_id=?', (row['upload_id'],)).fetchone()[0]
                    if state in ('canceled', 'rejected'):
                        self.db.execute("UPDATE incoming SET state='cleanup' WHERE id=?", (incoming['id'],))
                        return
                    # Publish ownership before file IO. The closed incoming inode is
                    # never written again; no signed upload URL can write this blob.
                    self.db.execute('INSERT OR IGNORE INTO contents(account,sha256,size,path,created_at) VALUES(?,?,?,?,?)',
                                    (row['account'], digest, actual_size, str(blob), now_ms()))
                    content = self.db.execute('SELECT * FROM contents WHERE account=? AND sha256=? AND size=?',
                                              (row['account'], digest, actual_size)).fetchone()
                if content['cleanup_state'] != 0:
                    raise OSError('Content cleanup must finish before this object can be published')
                if not content['verified']:
                    try:
                        os.link(path, blob)
                    except FileExistsError:
                        # Recovery of publication before the DB commit; only the
                        # reserved immutable path may already exist here.
                        with blob.open('rb') as existing:
                            if os.fstat(existing.fileno()).st_size != actual_size or file_hash(existing) != digest:
                                raise OSError('Published content failed integrity validation')
                    sync_directory(blob.parent)
                with self.lock, self.db:
                    self.db.execute('UPDATE contents SET verified=1 WHERE account=? AND sha256=? AND size=?', (row['account'], digest, actual_size))
                    current = self.db.execute('SELECT * FROM attempts WHERE upload_id=?', (row['upload_id'],)).fetchone()
                    if current['state'] not in ('canceled', 'rejected'):
                        self._confirm(current, dict(metadata, attemptGeneration=row['generation']))
                    self.db.execute("UPDATE incoming SET state='cleanup' WHERE id=?", (incoming['id'],))

    def confirm_pending(self, limit=32):
        with self.lock:
            rows = self.db.execute("SELECT * FROM incoming WHERE state='ready' ORDER BY created_at,id LIMIT ?", (limit,)).fetchall()
        failures = []
        for row in rows:
            try:
                self.confirm_one(row)
            except OSError as primary:
                try:
                    with self.lock, self.db:
                        self.db.execute("UPDATE incoming SET state='verification_failed',error=? WHERE id=?", (str(primary), row['id']))
                        self.db.execute("UPDATE attempts SET state='rejected',error_code='StorageFailure',error_message='Server could not verify the stored file' WHERE upload_id=? AND state IN ('upload','verifying')", (row['upload_id'],))
                except Exception as secondary:
                    print('Recording verification failure failed: ' + repr(secondary), file=sys.stderr)
                    raise primary
                failures.append(dict(id=row['id'], error=str(primary)))
        return dict(processed=len(rows), failures=failures)

    def cleanup(self, retry_failed=False, limit=64):
        if retry_failed:
            with self.lock, self.db:
                self.db.execute("UPDATE incoming SET state='cleanup',error=NULL WHERE state IN ('cleanup_failed','verification_failed')")
        with self.lock:
            rows = self.db.execute("SELECT * FROM incoming WHERE state='cleanup' ORDER BY created_at,id LIMIT ?", (limit,)).fetchall()
        freed, failures = 0, []
        for row in rows:
            with self.upload_lock(row['upload_id']):
                with self.lock:
                    current = self.db.execute('SELECT state FROM incoming WHERE id=?', (row['id'],)).fetchone()
                    if current is None or current[0] != 'cleanup':
                        continue
                path = Path(row['path'])
                try:
                    try:
                        size = path.stat().st_size
                    except FileNotFoundError:
                        size = 0
                    path.unlink(missing_ok=True)
                    sync_directory(path.parent)
                except OSError as primary:
                    try:
                        with self.lock, self.db:
                            self.db.execute("UPDATE incoming SET state='cleanup_failed',error=? WHERE id=?", (str(primary), row['id']))
                    except Exception as secondary:
                        print('Recording cleanup failure failed: ' + repr(secondary), file=sys.stderr)
                        raise primary
                    failures.append(dict(id=row['id'], error=str(primary)))
                    continue
                with self.lock, self.db:
                    self.db.execute('DELETE FROM incoming WHERE id=?', (row['id'],))
                freed += size
        return dict(freedBytes=freed, failures=failures)

    def start_worker(self, report):
        if self.worker is not None:
            raise RuntimeError('Confirmation worker already started')
        def run():
            try:
                next_maintenance = 0
                while not self.stop.is_set():
                    # Durable ready rows, rather than notifications, are authoritative.
                    result = self.confirm_pending()
                    for failure in result['failures'] + self.cleanup()['failures']:
                        report(failure)
                    if now_ms() >= next_maintenance:
                        for failure in self.maintenance()['failures']:
                            report(failure)
                        next_maintenance = now_ms() + 60000
                    if result['processed'] == 32:
                        continue
                    self.wake.wait(0.5)
                    self.wake.clear()
            except Exception as error:
                self.worker_error = error
                report(dict(error='Confirmation worker stopped: ' + repr(error)))
        self.worker = threading.Thread(target=run, name='BackupConfirmation')
        self.worker.start()

    def maintenance(self, at=None, limit=64, retry_failed=False):
        """Bounded TTL cleanup. Confirmed references and active body streams win.

        An unused attempt expires 24h after its last descriptor expired. Unreferenced
        publication reservations live for 24h, allowing crash recovery before GC.
        Tombstones and request identities remain durable for idempotency.
        """
        at = now_ms() if at is None else at
        ttl = 24 * 60 * 60 * 1000
        with self.lock:
            expired = self.db.execute("SELECT upload_id FROM attempts WHERE state IN ('upload','verifying') AND expires_at<? ORDER BY expires_at LIMIT ?", (at - ttl, limit)).fetchall()
        for attempt in expired:
            gate = self.upload_lock(attempt['upload_id'])
            if not gate.acquire(blocking=False):
                continue
            try:
                with self.lock, self.db:
                    # A live writer owns its private incoming file until close.
                    # Admission and this check share the attempt gate, but the
                    # potentially slow network transfer never holds that gate.
                    if self.db.execute("SELECT 1 FROM incoming WHERE upload_id=? AND state='writing' LIMIT 1", (attempt['upload_id'],)).fetchone():
                        continue
                    self.db.execute("UPDATE attempts SET state='rejected',error_code='AttemptExpired',error_message='Upload attempt exceeded its retention window' WHERE upload_id=? AND state IN ('upload','verifying') AND expires_at<?", (attempt['upload_id'], at - ttl))
                    self.db.execute("UPDATE incoming SET state='cleanup' WHERE upload_id=? AND state IN ('writing','ready')", (attempt['upload_id'],))
            finally:
                gate.release()
        with self.lock, self.db:
            if retry_failed:
                self.db.execute("UPDATE contents SET cleanup_state=1,cleanup_error=NULL WHERE cleanup_state=3 AND reference_count=0")
            rows = self.db.execute('''SELECT * FROM contents WHERE reference_count=0 AND cleanup_state=1
                UNION ALL SELECT * FROM contents WHERE reference_count=0 AND cleanup_state=0 AND created_at<?
                LIMIT ?''', (at - ttl, limit)).fetchall()
        failures = []
        for row in rows:
            gate = self.content_locks[int(row['sha256'][:8], 16) % len(self.content_locks)]
            if not gate.acquire(blocking=False):
                continue
            try:
                with self.lock, self.db:
                    current = self.db.execute('SELECT reference_count,cleanup_state FROM contents WHERE path=?', (row['path'],)).fetchone()
                    if current is None or current['reference_count'] != 0 or current['cleanup_state'] not in (0, 1):
                        continue
                    # Disable reuse under the same stripe as immutable publication,
                    # before releasing the DB lock for the file operation.
                    self.db.execute('UPDATE contents SET verified=0,cleanup_state=1 WHERE path=?', (row['path'],))
                try:
                    path = Path(row['path'])
                    path.unlink(missing_ok=True)
                    sync_directory(path.parent)
                except OSError as primary:
                    try:
                        with self.lock, self.db:
                            self.db.execute('UPDATE contents SET cleanup_state=3,cleanup_error=? WHERE path=?', (str(primary), row['path']))
                    except Exception as secondary:
                        print('Recording content cleanup failure failed: ' + repr(secondary), file=sys.stderr)
                        raise primary
                    failures.append(dict(id=row['sha256'], error=str(primary)))
                    continue
                with self.lock, self.db:
                    self.db.execute('DELETE FROM contents WHERE path=? AND reference_count=0 AND cleanup_state=1', (row['path'],))
            finally:
                gate.release()
        return dict(failures=failures)

    def list_backups(self, account, after, limit):
        with self.lock:
            rows = self.db.execute('SELECT * FROM backups WHERE account=? AND sequence>? ORDER BY sequence LIMIT ?', (account, after, limit)).fetchall()
        return dict(items=[dict(backupId=r['id'], sequence=r['sequence'], sourceNamespace=r['namespace'],
                    sourceId=r['source'], sourceVersion=r['version'], sha256=r['sha256'], byteCount=r['size'],
                    name=r['name'], mimeType=r['mime'], confirmedAt=r['confirmed_at']) for r in rows],
                    nextAfter=rows[-1]['sequence'] if rows else after)

    def content(self, account, backup_id):
        with self.lock:
            row = self.db.execute('''SELECT c.path,b.mime,b.size FROM backups b JOIN contents c
                ON c.account=b.account AND c.sha256=b.sha256 AND c.size=b.size AND c.verified=1
                WHERE b.account=? AND b.id=?''', (account, backup_id)).fetchone()
        if row is None:
            raise ProtocolError(404, 'NotFound', 'Backup not found')
        return row
