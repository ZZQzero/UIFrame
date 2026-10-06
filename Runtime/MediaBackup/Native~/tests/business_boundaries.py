"""Business invariants exercised through the installed production repository ABI.

Each malformed response corrupts the last member of an otherwise valid batch.
Both validation and application must reject it without changing earlier members;
the same request must then accept a valid response after an explicit reopen.
No SQLite implementation or production source is replaced by this suite.
"""
import argparse
import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from process_windows import Repository, IDENTITY
from protocol_fixture import NOW, response


class BusinessBoundaries(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.db = Repository(ARGS.core, ARGS.repository, self.root, True)
        self.call(58, 'aa')

    def tearDown(self):
        try:
            self.db.close()
        finally:
            self.temp.cleanup()

    def call(self, command, *args):
        result = self.db.call(command, *args)
        return result[-1] if result else []

    def prepare(self, count=2, executor=0):
        tasks = [format(100 + i, 'x') for i in range(count)]
        for offset in range(0, count, 32):
            batch = tasks[offset:offset + 32]
            values = [format(1000 + offset, 'x'), 'aa', NOW, len(batch)]
            for task in batch:
                values.extend([task, 'file:' + task, 'v1', task + '.jpg', 'image/jpeg'])
            self.call(2, *values, '', 0)
        for task in tasks:
            data = task.encode()
            self.call(77, task, 'aa', len(data), len(data), 1024 * 1024, NOW)
            (self.root / IDENTITY / 'payloads' / (task + '.payload')).write_bytes(data)
            self.call(3, task, 'aa', len(data), hashlib.sha256(data).hexdigest(), 'image/jpeg', 1024 * 1024, NOW)
            self.call(96, task, 'aa', NOW)
            self.call(9, task, executor, 0, NOW)
            self.call(74, task, 1, 'credential')
        return tasks

    def control(self, request='ce', executor=0, start=True):
        row = self.call(80, request, executor, NOW, 0, 1)[0]
        if start:
            self.call(81, request)
            self.assertEqual(1, self.call(82, request, 'system:' + request)[0]['admitted'])
            self.assertEqual(1, self.call(83, request)[0]['admitted'])
        return row

    def snapshot(self, tasks):
        return ([self.call(7, task)[0] for task in tasks], self.call(104, 'ce'), self.call(16, 0, '', 32))

    def apply(self, body):
        encoded = json.dumps(body)
        descriptors = self.call(84, 'ce', encoded)
        args = ['ce', encoded, NOW, len(descriptors)]
        for item in descriptors:
            args.extend([item['task_id'], 'protected:' + item['task_id']])
        self.call(85, *args)

    def rejected_response(self, status, mutation):
        tasks = self.prepare()
        self.control()
        good = json.loads(response(self.db, 'ce', status))
        if status in ('Pending', 'Verifying'):
            for item in good['items']:
                item['nextCheckAt'] = 5000
        before = self.snapshot(tasks)
        bad = copy.deepcopy(good)
        mutation(bad)
        encoded = json.dumps(bad)
        with self.assertRaises(AssertionError):
            self.call(84, 'ce', encoded)
        descriptors = [value for task in tasks for value in (task, 'protected:' + task)] if status == 'UploadRequired' else []
        with self.assertRaises(AssertionError):
            self.call(85, 'ce', encoded, NOW, len(descriptors) // 2, *descriptors)
        self.assertEqual(before, self.snapshot(tasks), 'Malformed last member committed earlier batch members')
        self.db.close()
        self.db = Repository(ARGS.core, ARGS.repository, self.root, False)
        self.assertEqual(before, self.snapshot(tasks), 'Rejected response changed durable facts')
        self.apply(good)
        expected = {'Confirmed': 3, 'UploadRequired': 1, 'Pending': 2, 'Verifying': 2}[status]
        self.assertEqual([expected] * 2, [self.call(7, task)[0]['state'] for task in tasks])
        self.call(87, 'ce')

    def stop_before_start(self, executor, stage, action):
        tasks = self.prepare(executor=executor)
        self.control(executor=executor, start=False)
        if stage >= 1:
            self.call(81, 'ce')
        if stage >= 2:
            self.assertEqual(1, self.call(82, 'ce', 'system')[0]['admitted'])
        if action == 'global_pause':
            self.call(15, 1)
        else:
            self.call(14, tasks[0], 1 if action == 'item_pause' else 2, NOW)
        if stage == 0:
            self.call(81, 'ce')
        opcode = 83 if stage == 2 else 82
        args = ('ce',) if stage == 2 else ('ce', 'system')
        self.assertEqual(0, self.call(opcode, *args)[0]['admitted'])
        self.call(86, 'ce', 'Stopped before network start', NOW, 1)
        self.assertEqual([], self.call(18, '', 32), 'Stop must not invent resource release')
        self.call(87, 'ce')
        for task in tasks:
            self.assertTrue((self.root / IDENTITY / 'payloads' / (task + '.payload')).exists())
        if action == 'global_pause':
            self.call(15, 0)
        # The second batch member is independent and can be explicitly resumed.
        self.call(102, tasks[1], 1)
        self.call(103, tasks[1], 'renewed', NOW)
        row = self.control('cf', executor)
        if action == 'item_cancel':
            self.assertEqual(2, row['kind'], 'Saved cancellation has priority over ordinary work')
            self.assertEqual([tasks[0]], [item['clientTaskId'] for item in json.loads(row['body'])['items']])
            self.call(85, 'cf', response(self.db, 'cf', 'Canceled'), NOW, 0)
            self.call(87, 'cf')
            row = self.control('d0', executor)
        self.assertIn(tasks[1], [item['clientTaskId'] for item in json.loads(row['body'])['items']])

    def late_confirmation(self, executor, action):
        task = self.prepare(1, executor)[0]
        self.control(executor=executor)
        if action == 'global_pause':
            self.call(15, 1)
        else:
            self.call(14, task, 1 if action == 'item_pause' else 2, NOW)
        good = json.loads(response(self.db, 'ce'))
        self.apply(good)
        self.assertEqual(3, self.call(7, task)[0]['state'])
        self.assertEqual([], self.call(18, '', 32), 'Receipt does not release the system owner')
        self.call(87, 'ce')
        self.assertEqual([], self.call(18, '', 32), 'Credentials still have an owner')
        self.call(102, task, 1)
        self.assertEqual([task], [row['id'] for row in self.call(18, '', 32)])
        self.db.close()
        self.db = Repository(ARGS.core, ARGS.repository, self.root, False)
        self.assertEqual(3, self.call(7, task)[0]['state'])
        self.assertEqual(1, len(self.call(16, 0, '', 32)))

    def test_duplicate_confirmation_is_idempotent_before_release(self):
        tasks = self.prepare()
        self.control()
        good = json.loads(response(self.db, 'ce'))
        self.apply(good)
        before = self.snapshot(tasks)
        good['items'].reverse()
        self.apply(good)
        self.assertEqual(before, self.snapshot(tasks))
        self.assertEqual(2, len(self.call(16, 0, '', 32)))

    def test_lost_first_batch_response_does_not_starve_next_batch(self):
        tasks = self.prepare(33)
        first = self.control()
        self.assertEqual(32, len(json.loads(first['body'])['items']))
        self.call(86, 'ce', 'Transport lost response', NOW, 0)
        self.call(87, 'ce')
        second = self.control('cf')
        self.assertEqual([tasks[-1]], [item['clientTaskId'] for item in json.loads(second['body'])['items']])
        self.assertEqual([6] * 32, [self.call(7, task)[0]['state'] for task in tasks[:-1]])


def set_path(path, value):
    def change(body):
        parent = body
        for key in path[:-1]:
            parent = parent[key]
        parent[path[-1]] = value
    return change


# Declarative wire-contract cases: independent named tests, each with fresh storage.
INVALID = [
    ('version_boolean', 'Confirmed', ['protocolVersion'], True),
    ('version_old', 'Confirmed', ['protocolVersion'], 1),
    ('request_identity', 'Confirmed', ['requestId'], 'another'),
    ('account_identity', 'Confirmed', ['account'], 'another'),
    ('server_time_negative', 'Confirmed', ['serverTime'], -1),
    ('server_time_overflow', 'Confirmed', ['serverTime'], 9223372036854775807),
    ('limits_changed', 'Confirmed', ['limits', 'maxItems'], 31),
    ('generation_boolean', 'Confirmed', ['items', 1, 'attemptGeneration'], True),
    ('generation_stale', 'Confirmed', ['items', 1, 'attemptGeneration'], 2),
    ('unknown_status', 'Confirmed', ['items', 1, 'status'], 'Done'),
    ('receipt_task', 'Confirmed', ['items', 1, 'receipt', 'clientTaskId'], 'other'),
    ('receipt_generation', 'Confirmed', ['items', 1, 'receipt', 'attemptGeneration'], 2),
    ('receipt_hash', 'Confirmed', ['items', 1, 'receipt', 'sha256'], '0' * 64),
    ('receipt_size', 'Confirmed', ['items', 1, 'receipt', 'byteCount'], 100),
    ('receipt_namespace', 'Confirmed', ['items', 1, 'receipt', 'sourceNamespace'], 'other'),
    ('receipt_source', 'Confirmed', ['items', 1, 'receipt', 'sourceId'], 'other'),
    ('receipt_version', 'Confirmed', ['items', 1, 'receipt', 'sourceVersion'], 'other'),
    ('receipt_time_future', 'Confirmed', ['items', 1, 'receipt', 'confirmedAt'], 1),
    ('receipt_id_path', 'Confirmed', ['items', 1, 'receipt', 'backupId'], '../escape'),
    ('upload_task', 'UploadRequired', ['items', 1, 'upload', 'clientTaskId'], 'other'),
    ('upload_hash', 'UploadRequired', ['items', 1, 'upload', 'sha256'], '0' * 64),
    ('upload_size_boolean', 'UploadRequired', ['items', 1, 'upload', 'byteCount'], True),
    ('upload_id_path', 'UploadRequired', ['items', 1, 'upload', 'uploadId'], '../escape'),
    ('upload_http', 'UploadRequired', ['items', 1, 'upload', 'url'], 'http://storage.invalid/image'),
    ('upload_credentials', 'UploadRequired', ['items', 1, 'upload', 'url'], 'https://user:secret@storage.invalid/image'),
    ('upload_fragment', 'UploadRequired', ['items', 1, 'upload', 'url'], 'https://storage.invalid/image#fragment'),
    ('upload_crlf', 'UploadRequired', ['items', 1, 'upload', 'url'], 'https://storage.invalid/\r\nheader'),
    ('upload_no_host', 'UploadRequired', ['items', 1, 'upload', 'url'], 'https:///image'),
    ('header_host', 'UploadRequired', ['items', 1, 'upload', 'headers'], [{'name': 'Host', 'value': 'other'}]),
    ('header_length', 'UploadRequired', ['items', 1, 'upload', 'headers'], [{'name': 'Content-Length', 'value': '2'}]),
    ('header_duplicate_case', 'UploadRequired', ['items', 1, 'upload', 'headers'], [{'name': 'X-Key', 'value': 'a'}, {'name': 'x-key', 'value': 'b'}]),
    ('header_crlf', 'UploadRequired', ['items', 1, 'upload', 'headers'], [{'name': 'X-Key', 'value': 'a\r\nb'}]),
    ('header_invalid_name', 'UploadRequired', ['items', 1, 'upload', 'headers'], [{'name': 'X Key', 'value': 'a'}]),
    ('header_count', 'UploadRequired', ['items', 1, 'upload', 'headers'], [{'name': 'X-' + str(i), 'value': 'a'} for i in range(33)]),
    ('success_empty', 'UploadRequired', ['items', 1, 'upload', 'successStatusCodes'], []),
    ('success_duplicate', 'UploadRequired', ['items', 1, 'upload', 'successStatusCodes'], [200, 200]),
    ('success_redirect', 'UploadRequired', ['items', 1, 'upload', 'successStatusCodes'], [302]),
    ('success_boolean', 'UploadRequired', ['items', 1, 'upload', 'successStatusCodes'], [True]),
    ('expires_negative', 'UploadRequired', ['items', 1, 'upload', 'expiresAt'], -1),
    ('pending_not_future', 'Pending', ['items', 1, 'nextCheckAt'], 0),
    ('verifying_overflow', 'Verifying', ['items', 1, 'nextCheckAt'], 9223372036854775807),
]


def invalid_test(status, mutation):
    return lambda self: self.rejected_response(status, mutation)


for name, status, path, value in INVALID:
    setattr(BusinessBoundaries, 'test_atomic_rejection_' + name, invalid_test(status, set_path(path, value)))
for executor in (0, 1, 2):
    for action in ('global_pause', 'item_pause', 'item_cancel'):
        for stage in (0, 1, 2):
            def run(self, executor=executor, stage=stage, action=action):
                self.stop_before_start(executor, stage, action)
            setattr(BusinessBoundaries, f'test_stop_executor_{executor}_stage_{stage}_{action}', run)
        def late(self, executor=executor, action=action):
            self.late_confirmation(executor, action)
        setattr(BusinessBoundaries, f'test_late_receipt_executor_{executor}_{action}', late)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    ARGS, rest = parser.parse_known_args()
    unittest.main(argv=[__file__, *rest])
