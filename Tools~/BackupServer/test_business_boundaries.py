"""HTTP admission, transaction rollback and concurrent identity boundaries."""
import copy
import contextlib
import http.client
import io
import json
import random
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

import test_server as fixtures
from protocol import encode, identity


class BusinessBoundaries(unittest.TestCase):
    # Reuse the real HTTP/storage fixture without inheriting its test methods.
    setUp = fixtures.BackupProtocolTests.setUp
    tearDown = fixtures.BackupProtocolTests.tearDown
    call = fixtures.BackupProtocolTests.call
    item = fixtures.BackupProtocolTests.item
    control = fixtures.BackupProtocolTests.control
    plan = fixtures.BackupProtocolTests.plan
    put = fixtures.BackupProtocolTests.put
    query = fixtures.BackupProtocolTests.query
    backup = fixtures.BackupProtocolTests.backup

    def facts(self):
        with self.store.lock:
            return {table: sorted([tuple(row) for row in self.store.db.execute('SELECT * FROM ' + table)], key=repr)
                    for table in ('requests', 'tasks', 'sources', 'attempts', 'incoming', 'contents', 'backups')}

    def invalid_member(self, field, value):
        first, last = self.item(task='healthy'), self.item(task='boundary')
        bad = dict(last, **{field: value})
        before = self.facts()
        code, result = self.control('plans', [first, bad], 'fixed-request')
        self.assertEqual(400, code, result)
        self.assertEqual('InvalidRequest', result['error']['code'])
        self.assertEqual(before, self.facts(), 'Invalid last member must not persist the healthy first member')
        self.assertEqual(200, self.control('plans', [first, last], 'fixed-request')[0])
        self.assertEqual(2, len(self.facts()['attempts']))

    def test_plan_database_failure_rolls_back_entire_batch_and_request_identity(self):
        first, last = self.item(task='healthy'), self.item(task='broken')
        self.store.db.execute("CREATE TEMP TRIGGER fail_plan BEFORE INSERT ON tasks WHEN NEW.id='broken' BEGIN SELECT RAISE(ABORT,'injected disk failure'); END")
        before = self.facts()
        code, result = self.control('plans', [first, last], 'fixed-request')
        self.assertEqual(500, code)
        self.assertEqual('StorageFailure', result['error']['code'])
        self.assertEqual(before, self.facts())
        self.store.db.execute('DROP TRIGGER fail_plan')
        self.assertEqual(200, self.control('plans', [first, last], 'fixed-request')[0])
        self.assertEqual(2, len(self.facts()['attempts']))

    def test_cancel_database_failure_does_not_commit_first_cancellation(self):
        items = [self.item(task='healthy'), self.item(task='broken')]
        self.plan(items)
        self.store.db.execute("CREATE TEMP TRIGGER fail_cancel BEFORE UPDATE ON attempts WHEN NEW.task='broken' BEGIN SELECT RAISE(ABORT,'injected disk failure'); END")
        before = self.facts()
        self.assertEqual(500, self.control('cancellations', list(map(identity, items)), 'cancel-batch')[0])
        self.assertEqual(before, self.facts())
        self.store.db.execute('DROP TRIGGER fail_cancel')
        result = self.control('cancellations', list(map(identity, items)), 'cancel-batch')
        self.assertEqual(['Canceled', 'Canceled'], [item['status'] for item in result[1]['items']])

    def test_concurrent_identical_requests_share_one_attempt_and_upload_identity(self):
        item = self.item(task='shared')
        barrier = threading.Barrier(4)
        def submit(_):
            barrier.wait(timeout=5)
            return self.control('plans', [item], 'same-request')
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(submit, range(4)))
        self.assertEqual([200] * 4, [code for code, _ in results])
        self.assertEqual(1, len({body['items'][0]['upload']['uploadId'] for _, body in results}))
        self.assertEqual(1, len(self.facts()['requests']))
        self.assertEqual(1, len(self.facts()['attempts']))

    def test_concurrent_conflicting_requests_persist_only_the_winner(self):
        items = [self.item(task='first'), self.item(task='second')]
        barrier = threading.Barrier(2)
        def submit(item):
            barrier.wait(timeout=5)
            return self.control('plans', [item], 'same-request')
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(submit, items))
        self.assertEqual([200, 409], sorted(code for code, _ in results))
        winner = items[next(i for i, (code, _) in enumerate(results) if code == 200)]
        self.assertEqual([winner['clientTaskId']], [row[1] for row in self.facts()['tasks']])
        self.assertEqual(1, len(self.facts()['sources']))
        self.assertEqual(1, len(self.facts()['attempts']))
        self.assertEqual(200, self.control('plans', [winner], 'same-request')[0])

    def test_request_identity_is_scoped_to_account(self):
        item = self.item(task='same-task')
        alice = self.control('plans', [item], 'same-request')
        bob = self.control('plans', [item], 'same-request', token='test-bob')
        self.assertEqual((200, 200), (alice[0], bob[0]))
        self.assertNotEqual(alice[1]['items'][0]['upload']['uploadId'], bob[1]['items'][0]['upload']['uploadId'])
        self.assertEqual(2, len(self.facts()['attempts']))

    def test_canceled_generation_does_not_cancel_a_new_generation(self):
        old = self.item(task='photo')
        self.control('cancellations', [identity(old)])
        new = dict(old, attemptGeneration=2)
        planned = self.plan([new])[0]
        self.assertEqual(204, self.put(planned, b'photo')[0])
        self.store.confirm_pending()
        self.assertEqual('Confirmed', self.query(new)['status'])
        self.assertEqual('Canceled', self.query(old)['status'])

    def test_upload_authorization_cannot_be_swapped_between_objects(self):
        first, second = self.plan([self.item(task='first'), self.item(task='second')])
        swapped = copy.deepcopy(second)
        swapped['upload']['headers'] = first['upload']['headers']
        before = self.facts()
        self.assertEqual(403, self.put(swapped, b'photo')[0])
        self.assertEqual(before, self.facts())
        self.assertEqual(204, self.put(second, b'photo')[0])
        self.store.confirm_pending()
        self.assertEqual('Confirmed', self.query(second)['status'])

    def test_stream_failure_preserves_exception_and_only_reclaims_owned_partial_file(self):
        original = OSError('injected stream read failure')
        class BrokenBody(io.BytesIO):
            def read(self, count):
                if self.tell():
                    raise original
                return super().read(2)
        first, second = self.plan([self.item(task='broken'), self.item(task='healthy')])
        descriptor = first['upload']
        with self.assertRaises(OSError) as observed:
            self.store.receive(descriptor['uploadId'], descriptor['headers'][0]['value'], 5, BrokenBody(b'photo'))
        self.assertIs(original, observed.exception)
        row = self.store.db.execute('SELECT * FROM incoming').fetchone()
        self.assertEqual('cleanup', row['state'])
        self.assertEqual(204, self.put(second, b'photo')[0])
        self.store.confirm_pending()
        self.store.cleanup()
        self.assertEqual('Confirmed', self.query(second)['status'])
        self.assertEqual('UploadRequired', self.query(first)['status'])
        self.assertEqual(1, len(self.facts()['backups']))

    def test_duplicate_json_property_in_last_member_rejects_entire_batch(self):
        body = encode(dict(protocolVersion=2, requestId='duplicate', items=[self.item(task='first'), self.item(task='second')]))
        body = body.replace(b'"clientTaskId":"second"', b'"clientTaskId":"second","clientTaskId":"second"')
        before = self.facts()
        self.assertEqual(400, self.call('POST', '/v2/backup/plans', body, headers={'Content-Type': 'application/json'})[0])
        self.assertEqual(before, self.facts())

    def test_maximum_utf8_metadata_and_generation_are_accepted(self):
        item = self.item(task='x' * 128, attemptGeneration=2147483647,
                         sourceNamespace='界' * 341 + 'a', sourceId='s' * 1024,
                         sourceVersion='v' * 1024, name='界' * 341 + 'a', mimeType='x' * 128)
        planned = self.plan([item])[0]
        self.assertEqual(204, self.put(planned, b'photo')[0])
        self.store.confirm_pending()
        receipt = self.query(item)['receipt']
        self.assertEqual(item['sourceNamespace'], receipt['sourceNamespace'])
        self.assertEqual(item['sourceVersion'], receipt['sourceVersion'])

    def invalid_framing(self, headers, status):
        before = self.facts()
        connection = http.client.HTTPConnection('127.0.0.1', self.server.server_port, timeout=5)
        try:
            connection.putrequest('POST', '/v2/backup/plans')
            connection.putheader('Authorization', 'Bearer test-alice')
            connection.putheader('Content-Type', 'application/json')
            for name, value in headers:
                connection.putheader(name, value)
            connection.endheaders()
            result = connection.getresponse()
            self.assertEqual(status, result.status)
            self.assertIn('error', json.loads(result.read()))
        finally:
            connection.close()
        self.assertEqual(before, self.facts())
        self.assertEqual('UploadRequired', self.plan([self.item()])[0]['status'])

    def test_verification_io_failure_isolates_one_object_without_automatic_retry(self):
        items = [self.item(task='broken'), self.item(task='healthy')]
        planned = self.plan(items)
        for item in planned:
            self.assertEqual(204, self.put(item, b'photo')[0])
        missing = self.store.db.execute('SELECT * FROM incoming WHERE upload_id=?', (planned[0]['upload']['uploadId'],)).fetchone()
        Path(missing['path']).unlink()
        result = self.store.confirm_pending()
        self.assertEqual(2, result['processed'])
        self.assertEqual([missing['id']], [failure['id'] for failure in result['failures']])
        self.assertEqual('Rejected', self.query(items[0])['status'])
        self.assertEqual('Confirmed', self.query(items[1])['status'])
        self.assertEqual(0, self.store.confirm_pending()['processed'])
        self.store.cleanup()
        self.assertEqual('verification_failed', self.store.db.execute('SELECT state FROM incoming WHERE id=?', (missing['id'],)).fetchone()[0])
        self.store.cleanup(retry_failed=True)
        self.assertEqual(0, len(self.facts()['incoming']))
        self.assertEqual('Confirmed', self.query(items[1])['status'])

    def test_verification_recording_failure_keeps_primary_error_and_rolls_back(self):
        item = self.item()
        self.assertEqual(204, self.put(self.plan([item])[0], b'photo')[0])
        self.store.db.execute("CREATE TEMP TRIGGER fail_record BEFORE UPDATE ON incoming WHEN NEW.state='verification_failed' BEGIN SELECT RAISE(ABORT,'secondary recording failure'); END")
        before = self.facts()
        original = OSError('primary file read failure')
        errors = io.StringIO()
        with patch('store.file_hash', side_effect=original), contextlib.redirect_stderr(errors):
            with self.assertRaises(OSError) as observed:
                self.store.confirm_pending()
        self.assertIs(original, observed.exception)
        self.assertIn('secondary recording failure', errors.getvalue())
        self.assertEqual(before, self.facts())
        self.store.db.execute('DROP TRIGGER fail_record')
        self.store.confirm_pending()
        self.assertEqual('Confirmed', self.query(item)['status'])

    def test_shutdown_without_worker_failure_still_releases_owner_after_database_close_error(self):
        original = OSError('database close failed after release')
        database = self.store.db
        class ClosingDatabase:
            def close(self):
                database.close()
                raise original
        self.store.db = ClosingDatabase()
        with self.assertRaises(OSError) as observed:
            self.store.close()
        self.assertIs(original, observed.exception)
        self.assertTrue(self.store.owner.file.closed)
        self.store.close()
        reopened = fixtures.Store(self.directory.name, self.credentials)
        reopened.close()

    def test_worker_drains_more_than_one_full_batch_without_another_notification(self):
        items = [self.item(task='batch-' + str(i)) for i in range(33)]
        planned = self.plan(items[:32]) + self.plan(items[32:])
        for item in planned:
            self.assertEqual(204, self.put(item, b'photo')[0])
        completed, errors = threading.Event(), []
        original = self.store.confirm_pending
        calls = []
        def confirm():
            result = original()
            calls.append(result['processed'])
            if sum(calls) >= 33:
                completed.set()
            return result
        with patch.object(self.store, 'confirm_pending', side_effect=confirm):
            self.store.start_worker(errors.append)
            with self.assertRaises(RuntimeError):
                self.store.start_worker(errors.append)
            self.assertTrue(completed.wait(5))
            self.store.stop.set();self.store.wake.set();self.store.worker.join(5)
        self.assertFalse(self.store.worker.is_alive())
        self.assertEqual([32, 1], calls[:2]);self.assertEqual([], errors)
        for item in items:
            self.assertEqual('Confirmed', self.query(item)['status'])

    def test_stopping_service_refuses_new_work_but_keeps_confirmed_reads(self):
        item, _, receipt = self.backup()
        self.store.stop.set()
        before = self.facts()
        code, body = self.control('plans', [self.item()])
        self.assertEqual(503, code);self.assertEqual('ServiceStopping', body['error']['code'])
        self.assertEqual(before, self.facts())
        self.assertEqual('Confirmed', self.query(item)['status'])
        self.assertEqual((200, b'photo'), self.call('GET', '/v2/backups/' + receipt['backupId'] + '/content'))

    def state_sequence(self, seed):
        """A small independent model: no reads of implementation states or SQL."""
        rng = random.Random(seed)
        states, descriptors, payloads = {}, {}, {}
        verified = set()
        trace = []
        for step in range(80):
            account = rng.choice(('alice', 'bob'))
            task = 'model-' + str(rng.randrange(5))
            generation = rng.choice((1, 2))
            key = account, task, generation
            data = ('content-' + str(int(task[-1]) % 2)).encode()
            item = self.item(data, task=task, attemptGeneration=generation)
            token = 'test-' + account
            action = rng.choice(('plan', 'plan', 'query', 'cancel', 'put', 'confirm'))
            trace.append((step, key, action))
            with self.subTest(seed=seed, step=step, trace=trace):
                if action == 'plan':
                    result = self.control('plans', [item], token=token)
                    self.assertEqual(200, result[0])
                    states.setdefault(key, 'Confirmed' if (account, data) in verified else 'UploadRequired')
                    actual = result[1]['items'][0]
                    self.assertEqual(states[key], actual['status'])
                    if actual['status'] == 'UploadRequired':
                        descriptors[key] = actual
                        payloads[key] = data
                elif action == 'cancel':
                    result = self.control('cancellations', [identity(item)], token=token)
                    states[key] = 'Confirmed' if states.get(key) == 'Confirmed' else 'Canceled'
                    self.assertEqual(200, result[0])
                    self.assertEqual(states[key], result[1]['items'][0]['status'])
                elif action == 'put' and key in descriptors:
                    code, _ = self.put(descriptors[key], payloads[key])
                    self.assertEqual(409 if states[key] == 'Canceled' else 204, code)
                    if states[key] == 'UploadRequired':
                        states[key] = 'Verifying'
                elif action == 'confirm':
                    self.store.confirm_pending()
                    for candidate in states:
                        if states[candidate] == 'Verifying':
                            states[candidate] = 'Confirmed'
                            verified.add((candidate[0], payloads[candidate]))
                # Query after every operation, including no-op selections, is the oracle.
                code, body = self.control('status', [identity(item)], token=token)
                self.assertEqual(200, code)
                self.assertEqual(states.get(key, 'Absent'), body['items'][0]['status'])
        for (account, task, generation), expected in states.items():
            code, body = self.control('status', [dict(clientTaskId=task, attemptGeneration=generation)], token='test-' + account)
            self.assertEqual(200, code)
            self.assertEqual(expected, body['items'][0]['status'], (seed, account, task, generation))
            if expected == 'Confirmed':
                receipt = body['items'][0]['receipt']
                expected_bytes = ('content-' + str(int(task[-1]) % 2)).encode()
                self.assertEqual((200, expected_bytes), self.call('GET', '/v2/backups/' + receipt['backupId'] + '/content', token='test-' + account))
                other = 'bob' if account == 'alice' else 'alice'
                self.assertEqual(404, self.call('GET', '/v2/backups/' + receipt['backupId'] + '/content', token='test-' + other)[0])


INVALID = [
    ('task_empty', 'clientTaskId', ''), ('task_path', 'clientTaskId', '../file'),
    ('task_length', 'clientTaskId', 'x' * 129), ('task_type', 'clientTaskId', 1),
    ('generation_zero', 'attemptGeneration', 0), ('generation_negative', 'attemptGeneration', -1),
    ('generation_overflow', 'attemptGeneration', 2147483648), ('generation_float', 'attemptGeneration', 1.5),
    ('generation_string', 'attemptGeneration', '1'),
    ('namespace_empty', 'sourceNamespace', ''), ('namespace_utf8_overflow', 'sourceNamespace', '界' * 342),
    ('source_null', 'sourceId', None), ('source_nul', 'sourceId', 'a\0b'),
    ('version_length', 'sourceVersion', 'v' * 1025), ('version_type', 'sourceVersion', []),
    ('name_utf8_overflow', 'name', '界' * 342), ('name_newline', 'name', 'a\nb'),
    ('mime_length', 'mimeType', 'x' * 129), ('mime_tab', 'mimeType', 'image/\tjpeg'),
    ('hash_uppercase', 'sha256', 'A' * 64), ('hash_short', 'sha256', 'a' * 63),
    ('hash_nonhex', 'sha256', 'g' * 64), ('hash_type', 'sha256', None),
    ('size_negative', 'byteCount', -1), ('size_overflow', 'byteCount', 9223372036854775808),
    ('size_boolean', 'byteCount', True), ('size_float', 'byteCount', 5.0), ('size_string', 'byteCount', '5'),
]
for name, field, value in INVALID:
    def run(self, field=field, value=value):
        self.invalid_member(field, value)
    setattr(BusinessBoundaries, 'test_reject_' + name, run)
for seed in (7, 19, 83, 211, 20261007):
    def sequence(self, seed=seed):
        self.state_sequence(seed)
    setattr(BusinessBoundaries, 'test_state_sequence_seed_' + str(seed), sequence)

for name, headers, status in [
    ('missing_length', [], 400),
    ('duplicate_length', [('Content-Length', '0'), ('Content-Length', '0')], 400),
    ('negative_length', [('Content-Length', '-1')], 400),
    ('nonnumeric_length', [('Content-Length', 'abc')], 400),
    ('chunked_length', [('Content-Length', '0'), ('Transfer-Encoding', 'chunked')], 400),
    ('oversized_body', [('Content-Length', '262145')], 413),
]:
    def framing(self, headers=headers, status=status):
        self.invalid_framing(headers, status)
    setattr(BusinessBoundaries, 'test_http_' + name, framing)

for name, query in [
    ('duplicate_after', 'after=0&after=1'), ('negative_after', 'after=-1'),
    ('zero_limit', 'limit=0'), ('large_limit', 'limit=33'),
    ('nonnumeric_after', 'after=abc'), ('unknown_parameter', 'unknown=1'),
    ('malformed_query', 'after'),
]:
    def paging(self, query=query):
        before = self.facts()
        self.assertEqual(400, self.call('GET', '/v2/backups?' + query)[0])
        self.assertEqual(before, self.facts())
        self.assertEqual(200, self.call('GET', '/v2/backups?after=0&limit=32')[0])
    setattr(BusinessBoundaries, 'test_http_paging_' + name, paging)


if __name__ == '__main__':
    unittest.main()
