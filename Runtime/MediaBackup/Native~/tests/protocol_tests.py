"""Exercise the production shared ABI against the real v2 reference service facts."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

from process_windows import Repository, IDENTITY

PACKAGE = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(PACKAGE / 'Tools~/BackupServer'))
from store import Store, now_ms
from protocol import encode

NOW = 621355968000000000 + now_ms() * 10000
DATA = b'photo'
DIGEST = hashlib.sha256(DATA).hexdigest()
DAY = 864000000000


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.repository = Repository(ARGS.core, ARGS.repository, self.root, True)
        self.call(58, 'aa')
        self.server = Store(self.root / 'server', [dict(account='test', token='test')])
        self.server.storage_origin = 'https://storage.test.invalid'
        self.counter = 0

    def tearDown(self):
        self.server.close()
        self.repository.close()
        self.temporary.cleanup()

    def call(self, command, *args):
        result = self.repository.call(command, *args)
        return result[-1] if result else []

    def prepare(self, count=1):
        self.counter += 1
        batch = '%032x' % self.counter
        ids = ['%032x' % (self.counter * 100 + i) for i in range(count)]
        values = [batch, 'aa', NOW, count]
        for task in ids:
            values.extend([task, 'file:' + task, 'v1', 'photo.jpg', 'image/jpeg', 0])
        self.call(2, *values)
        for task in ids:
            (self.root / IDENTITY / 'payloads' / (task + '.payload')).write_bytes(DATA)
            self.call(3, task, 'aa', len(DATA), DIGEST, 'image/jpeg', 512 * 1024 * 1024, NOW)
            self.call(96, task, 'aa', NOW)
            self.call(9, task, 0, 0, NOW)
            self.call(74, task, 1, 'protected-api-token')
        return ids

    def control(self, now=NOW, future=0, flush=1):
        self.counter += 1
        request = '%032x' % (100000 + self.counter)
        rows = self.call(80, request, 0, now, future, flush)
        if not rows: return None
        self.call(81, request)
        self.call(82, request, 'system-' + request)
        self.call(83, request)
        return rows[0]

    def response(self, control):
        operation = ['plans', 'status', 'cancellations'][control['kind']]
        return self.server.control('test', operation, json.loads(control['body']))

    def apply(self, control, response=None, now=NOW):
        response = response or self.response(control)
        descriptors = self.call(84, control['id'], encode(response).decode())
        values = [control['id'], encode(response).decode(), now, len(descriptors)]
        for row in descriptors: values.extend([row['task_id'], 'protected-descriptor-' + row['task_id']])
        self.call(85, *values)
        self.call(87, control['id'])
        return response

    def upload(self, result, now=NOW):
        task = result['clientTaskId']
        self.call(91, task, 1, 'upload-system', now)
        descriptor = result['upload']
        self.server.receive(descriptor['uploadId'], descriptor['headers'][0]['value'], len(DATA), io.BytesIO(DATA))
        self.call(92, task, 1, 1, '', now)
        self.call(93, task, 1, now)

    def test_batch_32_reordered_response_and_shared_receipts(self):
        tasks = self.prepare(32)
        control = self.control()
        self.assertEqual(32, len(json.loads(control['body'])['items']))
        response = self.response(control)
        response['items'].reverse()
        self.apply(control, response)
        self.assertEqual(32, len(self.call(90, 0, '')))
        for result in response['items']: self.upload(result)
        self.assertEqual([], self.call(90, 0, ''))
        self.assertEqual([], self.call(18, '', 32))
        self.server.confirm_pending()
        query = self.control(NOW + 10000000)
        self.assertEqual(1, query['kind'])
        confirmed = self.apply(query, now=NOW + 10000000)
        self.assertTrue(all(r['status'] == 'Confirmed' for r in confirmed['items']))
        for task in tasks: self.call(102, task, 1)
        self.assertEqual(32, len(self.call(18, '', 32)))
        self.assertEqual(32, len(self.call(16, 0, '', 32)))

    def test_invalid_batch_never_applies_earlier_good_item(self):
        tasks = self.prepare(2)
        control = self.control()
        response = self.response(control)
        response['items'][1]['upload']['sha256'] = '0' * 64
        with self.assertRaises(AssertionError):
            self.call(85, control['id'], encode(response).decode(), NOW, 2,
                      tasks[0], 'first', tasks[1], 'second')
        self.assertEqual([], self.call(90, 0, ''))
        for task in tasks: self.assertEqual(0, self.call(7, task)[0]['protocol_phase'])

    def test_missing_extra_duplicate_wrong_account_are_rejected(self):
        self.prepare(2)
        control = self.control()
        good = self.response(control)
        cases = [dict(good, items=good['items'][:1]), dict(good, items=good['items'] + [good['items'][0]]),
                 dict(good, items=[good['items'][0]] * 2), dict(good, account='another')]
        for value in cases:
            with self.assertRaises(AssertionError): self.call(84, control['id'], encode(value).decode())

    def test_empty_put_waits_for_receipt_and_does_not_occupy_upload_slot(self):
        task = self.prepare()[0]
        planned = self.apply(self.control())['items'][0]
        self.upload(planned)
        value = self.call(7, task)[0]
        self.assertEqual(2, value['state'])
        self.assertEqual(1, value['payload_released'])
        self.assertEqual(1, value['upload_released'])
        self.assertEqual(1, value['file_state'])
        self.assertEqual([], self.call(18, '', 32))

    def test_future_query_does_not_block_new_plan(self):
        self.prepare()
        result = self.apply(self.control())['items'][0]
        self.upload(result)
        future = self.control(NOW, future=1)
        self.assertGreater(future['not_before_utc'], NOW)
        self.prepare()
        ready = self.control(NOW)
        self.assertIsNotNone(ready)
        self.assertEqual(0, ready['kind'])

    def test_pending_obeys_server_next_check_under_pressure(self):
        task = self.prepare()[0]
        result = self.apply(self.control())['items'][0]
        self.upload(result)
        query = self.control(NOW + 10000000)
        # Align the fixture server clock to the deterministic repository clock.
        value = self.response(query)
        value['serverTime'] = (NOW - 621355968000000000) // 10000 + 1000
        value['items'][0]['nextCheckAt'] = value['serverTime'] + 5000
        self.apply(query, value, NOW + 10000000)
        self.assertIsNone(self.control(NOW + 20000000))
        self.assertEqual(NOW + 60000000, self.call(7, task)[0]['next_check_utc'])

    def pending(self, control, at, next_at):
        value = self.response(control)
        value['serverTime'] = (at - 621355968000000000) // 10000
        value['items'] = [dict(clientTaskId=item['clientTaskId'], attemptGeneration=item['attemptGeneration'],
                               status='Pending', nextCheckAt=(next_at - 621355968000000000) // 10000)
                          for item in value['items']]
        return value

    def test_next_check_at_or_beyond_deadline_never_queues_a_later_query(self):
        for delay in (DAY, 7 * DAY):
            with self.subTest(delay=delay):
                task = self.prepare()[0]
                planned = self.apply(self.control())['items'][0]
                self.upload(planned)
                query = self.control(NOW + 10000000)
                self.apply(query, self.pending(query, NOW + 10000000, NOW + delay), NOW + 10000000)
                self.assertEqual(6, self.call(7, task)[0]['state'])
                self.assertIsNone(self.control(NOW + 20000000, future=1))
                self.call(102, task, 1)
                self.assertEqual([], self.call(18, '', 32))  # unknown outcome retains the photo

    def test_bound_future_query_exposes_deadline_without_faking_release(self):
        task = self.prepare()[0]
        self.upload(self.apply(self.control())['items'][0])
        query = self.control(NOW + 10000000)
        due = NOW + DAY - 10000000
        self.apply(query, self.pending(query, NOW + 10000000, due), NOW + 10000000)
        future = self.control(NOW + 20000000, future=1)
        self.assertEqual(due, future['not_before_utc'])
        self.assertEqual(NOW + DAY, future.get('deadline_utc', 0))
        self.assertEqual(NOW + DAY, self.call(105, 0)[0]['next_utc'])
        self.call(94, 0, NOW + DAY)
        self.assertEqual(0, self.call(104, future['id'])[0]['released'])
        with self.assertRaises(AssertionError): self.call(102, task, 1)
        self.call(86, future['id'], 'Confirmation deadline reached', NOW + DAY, 0)
        with self.assertRaises(AssertionError): self.call(102, task, 1)
        self.call(87, future['id']); self.call(102, task, 1)
        self.assertEqual(6, self.call(7, task)[0]['state'])
        self.assertIsNone(self.control(NOW + DAY, future=1))

    def test_late_pending_and_upload_required_stop_but_confirmed_receipt_is_valid(self):
        for status in ('Pending', 'UploadRequired', 'Confirmed'):
            with self.subTest(status=status):
                task = self.prepare()[0]
                control = self.control()
                planned = self.response(control)
                # Start the confirmation window without uploading, so each
                # response branch uses the same protocol facts and no SQL edits.
                self.apply(control, self.pending(control, NOW, NOW + 10000000))
                query = self.control(NOW + 10000000)
                if status == 'Pending': value = self.pending(query, NOW + DAY, NOW + DAY + 10000000)
                elif status == 'UploadRequired':
                    value = planned; value['requestId'] = query['id']
                else:
                    item = planned['items'][0]['upload']
                    self.server.receive(item['uploadId'], item['headers'][0]['value'], len(DATA), io.BytesIO(DATA))
                    self.server.confirm_pending(); value = self.response(query)
                    self.assertEqual('Confirmed', value['items'][0]['status'])
                self.apply(query, value, NOW + DAY)
                self.assertEqual(3 if status == 'Confirmed' else 6, self.call(7, task)[0]['state'])
                self.assertFalse(any(row['task_id'] == task for row in self.call(90, 0, '')))
                self.call(102, task, 1)

    def test_check_budget_stops_on_256th_pending_response(self):
        task = self.prepare()[0]
        for index in range(256):
            at = NOW + index * 10000000
            control = self.control(at)
            self.assertIsNotNone(control)
            self.apply(control, self.pending(control, at, at + 10000000), at)
            self.assertEqual(6 if index == 255 else 2, self.call(7, task)[0]['state'])
        self.assertIsNone(self.control(NOW + 2560000000, future=1))

    def test_resume_cannot_extend_the_confirmation_deadline(self):
        task = self.prepare()[0]
        self.upload(self.apply(self.control())['items'][0])
        self.call(15, 1); self.call(94, 0, NOW); self.call(102, task, 1)
        self.call(15, 0); self.call(103, task, 'renewed', NOW + DAY)
        self.assertIsNone(self.control(NOW + DAY, future=1))
        self.call(94, 0, NOW + DAY)
        self.assertEqual(6, self.call(7, task)[0]['state'])

    def test_no_pending_wake_is_numeric_zero_for_native_consumers(self):
        self.assertEqual(0, self.call(105, 0)[0]['next_utc'])
        task = self.prepare()[0]
        self.assertGreater(self.call(105, 0)[0]['next_utc'], 0)
        control = self.control()
        self.assertEqual(0, self.call(105, 0)[0]['next_utc'])
        self.call(86, control['id'], 'request ended', NOW, 0); self.call(87, control['id'])
        self.call(102, task, 1)
        self.assertEqual(0, self.call(105, 0)[0]['next_utc'])

    def test_history_page_skips_failed_control_cleanup_and_returns_after_explicit_repair(self):
        tasks, controls = [], []
        for index in range(2):
            task = self.prepare()[0]; tasks.append(task)
            self.call(14, task, 2, NOW)
            control = self.control(); controls.append(control)
            self.apply(control); self.call(102, task, 1)
            if index == 0:
                broken = self.root / IDENTITY / control['relative_path']
                broken.unlink(); broken.mkdir(); (broken / 'blocker').write_text('x')
            self.call(100, NOW, 32)
            row = self.call(18, '', 32)[0]
            self.call(19, task, row['updated_utc'], NOW)
        page = self.call(50, 0, NOW + 1, 0, 1)
        self.assertEqual([tasks[1]], [row['id'] for row in page])
        self.call(51, tasks[1], page[0]['updated_utc'])
        self.assertEqual([], self.call(50, 0, NOW + 1, 0, 1))
        self.assertEqual(tasks[0], self.call(7, tasks[0])[0]['id'])
        (broken / 'blocker').unlink(); broken.rmdir()
        self.call(108, controls[0]['id'], NOW)
        page = self.call(50, 0, NOW + 1, 0, 1)
        self.assertEqual([tasks[0]], [row['id'] for row in page])
        self.call(51, tasks[0], page[0]['updated_utc'])
        self.assertEqual([], self.call(7, tasks[0]))

    def test_recover_partial_preparation_preserves_already_accepted_payload(self):
        self.call(2, 'ff', 'aa', NOW, 2, 'a1', 'file:first', 'v1', 'one.jpg', 'image/jpeg', 0,
                  'a2', 'file:second', 'v1', 'two.jpg', 'image/jpeg', 0)
        for task in ('a1', 'a2'):
            (self.root / IDENTITY / ('payloads/' + task + '.payload')).write_bytes(DATA)
            self.call(3, task, 'aa', len(DATA), DIGEST, 'image/jpeg', 1024, NOW)
        self.call(96, 'a1', 'aa', NOW)
        with self.assertRaises(AssertionError): self.call(97, 'a1', 'aa', 'cannot discard accepted item', NOW)
        self.repository.close()
        self.repository = Repository(ARGS.core, ARGS.repository, self.root, False)
        self.call(58, 'ee'); self.call(55, 'ee', NOW, 32)
        self.assertEqual([0, 7], [row['state'] for row in self.call(98, 'ff')])
        rows = self.call(18, '', 32)
        self.assertEqual(['a2'], [row['id'] for row in rows])
        self.call(19, 'a2', rows[0]['updated_utc'], NOW)
        self.assertTrue((self.root / IDENTITY / 'payloads/a1.payload').is_file())
        self.assertFalse((self.root / IDENTITY / 'payloads/a2.payload').exists())

    def test_continuous_registration_does_not_starve_due_confirmation(self):
        first = self.prepare()[0]
        self.upload(self.apply(self.control())['items'][0])
        current = NOW + 10000000
        for _ in range(4):
            self.prepare()
            query = self.control(current)
            self.assertEqual(1, query['kind'])
            self.assertIn(first, [x['clientTaskId'] for x in json.loads(query['body'])['items']])
            value = self.response(query)
            value['serverTime'] = (current - 621355968000000000) // 10000
            for item in value['items']: item['nextCheckAt'] = value['serverTime'] + 5000
            self.apply(query, value, current)
            plan = self.control(current)
            self.assertEqual(0, plan['kind'])
            self.upload(self.apply(plan, now=current)['items'][0], current)
            current += 60000000

    def test_cancel_unknown_needs_authoritative_tombstone(self):
        task = self.prepare()[0]
        self.call(14, task, 2, NOW)
        cancel = self.control()
        self.assertEqual(2, cancel['kind'])
        self.apply(cancel)
        self.assertEqual(8, self.call(7, task)[0]['state'])
        self.assertEqual([], self.call(18, '', 32))
        self.call(102, task, 1)
        self.assertEqual(1, len(self.call(18, '', 32)))

    def test_single_preparation_failure_keeps_other_acceptance(self):
        self.call(2, 'ff', 'aa', NOW, 2, 'a1', 'file:first', 'v1', 'one.jpg', 'image/jpeg', 0,
                  'a2', 'file:second', 'v1', 'two.jpg', 'image/jpeg', 0)
        self.call(97, 'a1', 'aa', 'source read failed', NOW)
        (self.root / IDENTITY / 'payloads/a2.payload').write_bytes(DATA)
        self.call(3, 'a2', 'aa', len(DATA), DIGEST, 'image/jpeg', 1024, NOW)
        self.call(96, 'a2', 'aa', NOW)
        results = self.call(98, 'ff')
        self.assertEqual([7, 0], [r['state'] for r in results])
        self.assertEqual('source read failed', results[0]['error'])

    def test_response_loss_and_actual_system_release_are_separate(self):
        task = self.prepare()[0]
        control = self.control()
        self.call(86, control['id'], 'response lost', NOW, 0)
        self.assertEqual(6, self.call(7, task)[0]['state'])
        with self.assertRaises(AssertionError): self.call(102, task, 1)
        self.call(87, control['id'])
        self.call(102, task, 1)
        self.assertEqual(0, self.call(7, task)[0]['server_outcome'])
        cleanup = self.call(100, NOW, 32)[0]
        self.assertEqual(1, cleanup['cleaned'])

    def test_idle_pause_keeps_stage_and_resume_reauthorizes_same_attempt(self):
        task = self.prepare()[0]
        self.call(15, 1)
        self.call(94, 0, NOW)
        self.assertEqual(4, self.call(7, task)[0]['state'])
        self.call(102, task, 1)
        self.assertEqual([], self.call(18, '', 32))
        self.call(15, 0)
        self.call(103, task, 'renewed-protected-api', NOW)
        self.assertEqual(1, self.call(7, task)[0]['current_generation'])
        self.assertEqual(0, self.control()['kind'])

    def test_expired_unused_descriptor_requires_release_before_refresh(self):
        task = self.prepare()[0]
        self.apply(self.control())
        expires = self.call(7, task)[0]['upload_expires_utc']
        self.call(94, 0, expires)
        self.assertEqual(1, self.call(7, task)[0]['protocol_phase'])
        self.call(93, task, 1, expires)
        # iOS schedules once per system callback. Release itself must expose
        # the next Plan; no unrelated wake or second action sweep is promised.
        self.assertEqual(0, self.call(7, task)[0]['protocol_phase'])
        self.assertEqual(0, self.control(expires)['kind'])

    def test_development_http_rejects_hostname_prefix_bypass(self):
        self.call(99, 1, 0)
        self.prepare()
        control = self.control()
        response = self.response(control)
        for host in ['127.evil', '10.evil', '192.168.evil', '172.16.evil', '127.1', '0177.0.0.1']:
            response['items'][0]['upload']['url'] = 'http://' + host + '/upload'
            with self.assertRaises(AssertionError): self.call(84, control['id'], encode(response).decode())
        for host in ['127.0.0.1', '10.0.0.1', '192.168.1.2', '172.31.0.1', 'localhost', '[::1]']:
            response['items'][0]['upload']['url'] = 'http://' + host + '/upload'
            self.assertEqual(1, len(self.call(84, control['id'], encode(response).decode())))

    def test_pause_and_expiration_races_do_not_claim_upload(self):
        task = self.prepare()[0]
        self.apply(self.control())
        expires = self.call(7, task)[0]['upload_expires_utc']
        self.assertEqual(0, self.call(91, task, 1, 'late', expires)[0]['started'])
        self.call(15, 1)
        self.assertEqual(0, self.call(91, task, 1, 'paused', NOW)[0]['started'])
        self.call(15, 0)
        self.assertEqual(1, self.call(91, task, 1, 'normal', NOW)[0]['started'])
        self.assertEqual(0, self.call(91, task, 1, 'duplicate', NOW)[0]['started'])

    def test_unknown_result_cannot_retry_and_reconcile_does_not_block_other_tasks(self):
        task = self.prepare()[0]
        control = self.control()
        self.call(86, control['id'], 'lost', NOW, 0)
        self.call(87, control['id']); self.call(102, task, 1)
        with self.assertRaises(AssertionError): self.call(14, task, 3, NOW)
        self.call(101, task, NOW, 0, 'renewed')
        query = self.control()
        other = self.prepare()[0]
        self.assertEqual(1, self.call(7, other)[0]['submission_state'])
        self.assertEqual(1, self.call(7, task)[0]['current_generation'])
        self.apply(query)

    def test_local_unstarted_upload_failure_isolated(self):
        first, second = self.prepare(2)
        self.apply(self.control())
        self.call(106, first, 1, 'file unavailable', NOW)
        self.call(93, first, 1, NOW); self.call(102, first, 1)
        self.assertEqual(6, self.call(7, first)[0]['state'])
        self.assertEqual(1, self.call(91, second, 1, 'independent', NOW)[0]['started'])

    def test_control_cleanup_failure_isolated_and_explicit_target_retry(self):
        task = self.prepare()[0]
        control = self.control()
        self.call(86, control['id'], 'lost', NOW, 0); self.call(87, control['id'])
        path = self.root / IDENTITY / control['relative_path']
        path.unlink(); path.mkdir(); (path / 'unexpected').write_text('x')
        self.assertEqual(1, self.call(100, NOW, 32)[0]['failed'])
        failure = self.call(107, '', 32)[0]
        self.assertEqual(control['id'], failure['id']); self.assertTrue(failure['cleanup_error'])
        self.assertEqual(0, self.call(100, NOW, 32)[0]['failed'])
        self.prepare(); independent = self.control(); self.apply(independent)
        self.assertEqual(1, self.call(100, NOW, 32)[0]['cleaned'])
        (path / 'unexpected').unlink(); path.rmdir()
        self.call(108, control['id'], NOW)
        self.assertEqual([], self.call(107, '', 32))
        self.assertEqual('', self.call(104, control['id'])[0]['body'])
        self.call(100, NOW + 31 * 864000000000, 32)
        self.assertEqual([], self.call(104, control['id']))

    def test_first_query_aggregation_wait_and_wake_are_persisted(self):
        first, second = self.prepare(2)
        response = self.apply(self.control())
        self.upload(response['items'][0], NOW)
        self.upload(response['items'][1], NOW + 1000000)
        self.assertEqual(NOW + 10000000, self.call(105, 0)[0]['next_utc'])
        self.assertIsNone(self.control(NOW + 9000000))
        query = self.control(NOW + 11000000)
        self.assertEqual(2, len(json.loads(query['body'])['items']))

    def test_pause_unsent_plan_preserves_plan_and_future_query_preserves_due_time(self):
        task = self.prepare()[0]
        request = 'abcdef'
        self.call(80, request, 0, NOW, 0, 1)
        self.call(86, request, 'paused before OS admission', NOW, 1)
        self.call(87, request); self.call(102, task, 1)
        self.assertEqual(0, self.call(7, task)[0]['protocol_phase'])
        self.call(103, task, 'renewed', NOW)
        planned = self.apply(self.control())['items'][0]
        self.upload(planned)
        due = self.call(7, task)[0]['next_check_utc']
        query = self.control(NOW, future=1)
        self.call(86, query['id'], 'paused future Query', NOW, 1)
        self.call(87, query['id']); self.call(102, task, 1)
        self.call(103, task, 'renewed', NOW)
        self.assertEqual(due, self.call(7, task)[0]['next_check_utc'])
        self.assertIsNone(self.control(NOW))

    def test_failed_cancel_is_not_automatically_reissued(self):
        task = self.prepare()[0]
        self.call(14, task, 2, NOW)
        cancel = self.control()
        self.call(86, cancel['id'], 'Cancel response lost', NOW, 0)
        self.call(87, cancel['id']); self.call(102, task, 1)
        self.assertEqual(6, self.call(7, task)[0]['state'])
        self.assertIsNone(self.control(NOW + 10000000))
        # The public ReconcileTaskAsync entry uses cancel=false. Reconciliation
        # must retain the user's already-persisted cancel intent.
        self.call(101, task, NOW, 0, 'explicit-new-credential')
        self.assertEqual(2, self.call(7, task)[0]['desired_action'])
        self.assertEqual(2, self.control()['kind'])

    def test_reconcile_requires_previous_protected_resources_released(self):
        task = self.prepare()[0]
        control = self.control()
        self.call(86, control['id'], 'Plan response lost', NOW, 0)
        self.call(87, control['id'])
        with self.assertRaises(AssertionError):
            self.call(101, task, NOW, 0, 'replacement')
        self.assertEqual('protected-api-token', self.call(7, task)[0]['credential_reference'])
        self.call(102, task, 1)
        self.call(101, task, NOW, 0, 'replacement')
        self.assertEqual(1, self.control()['kind'])

    def test_saved_cancel_follows_interrupted_upload_without_reupload(self):
        task = self.prepare()[0]
        self.apply(self.control())
        self.assertEqual(1, self.call(91, task, 1, 'upload', NOW)[0]['started'])
        self.call(14, task, 2, NOW)
        self.call(92, task, 1, 0, 'Stopped for cancellation', NOW)
        self.call(93, task, 1, NOW)
        self.call(15, 1)
        cancel = self.control()
        self.assertEqual(2, cancel['kind'])
        self.apply(cancel); self.call(102, task, 1)
        self.assertEqual(8, self.call(7, task)[0]['state'])
        self.assertEqual([], self.call(90, 0, ''))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    ARGS, rest = parser.parse_known_args()
    unittest.main(argv=[sys.argv[0], *rest])
