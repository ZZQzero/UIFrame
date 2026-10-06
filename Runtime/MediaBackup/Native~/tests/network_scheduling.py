"""Network groups, bounded control ownership and fairness through the public ABI."""
import argparse
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from process_windows import Repository, IDENTITY
from protocol_fixture import NOW, response


class NetworkSchedulingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.db = Repository(ARGS.core, ARGS.repository, self.root, True)
        self.db.call(58, 'aa')
        self.next_id = 100

    def tearDown(self):
        self.db.close()
        self.temp.cleanup()

    def call(self, command, *args):
        return self.db.call(command, *args)[-1]

    def prepare(self, task, wifi, executor=1, now=NOW):
        data = task.encode()
        self.call(2, task + 'a', 'aa', now, 1, task, 'file:' + task, 'v1', 'photo.jpg', 'image/jpeg', '', 0)
        self.call(77, task, 'aa', len(data), len(data), 1024, now)
        (self.root / IDENTITY / 'payloads' / (task + '.payload')).write_bytes(data)
        self.call(3, task, 'aa', len(data), hashlib.sha256(data).hexdigest(), 'image/jpeg', 1024, now)
        self.call(96, task, 'aa', now)
        self.call(9, task, executor, wifi, now)
        self.call(74, task, 1, 'credential')

    def create(self, executor=1, now=NOW, future=0, flush=1):
        self.next_id += 1
        rows = self.call(80, hex(self.next_id)[2:], executor, now, future, flush)
        return rows[0] if rows else None

    def members(self, row):
        return {item['clientTaskId'] for item in json.loads(row['body'])['items']}

    def pending(self, row, next_ms=5000):
        self.call(81, row['id']); self.call(82, row['id'], 'system'); self.call(83, row['id'])
        body = json.loads(response(self.db, row['id'], 'Pending'))
        for item in body['items']:
            item['nextCheckAt'] = next_ms
        self.call(85, row['id'], json.dumps(body), NOW, 0)
        self.call(87, row['id'])

    def test_wifi_control_does_not_capture_or_block_any_network(self):
        for executor in (0, 1, 2):
            with self.subTest(executor=executor):
                wifi, any_task = f'{executor}c', f'{executor}d'
                self.prepare(wifi, 1, executor); self.prepare(any_task, 0, executor)
                controls = [self.create(executor), self.create(executor)]
                self.assertTrue(all(controls), 'Each policy needs its own bounded scheduling opportunity')
                by_wifi = {row['wifi_only']: row for row in controls}
                self.assertEqual({wifi}, self.members(by_wifi[1]))
                self.assertEqual({any_task}, self.members(by_wifi[0]))
                self.prepare(f'{executor}e', 0, executor)
                self.assertIsNone(self.create(executor), 'A live same-policy control must retain its slot')
                self.call(81, by_wifi[1]['id']); self.call(82, by_wifi[1]['id'], 'wifi-system')
                self.assertIsNone(self.create(executor), 'System submission must not invent release')

    def test_future_queries_and_due_work_have_separate_bounded_slots_per_policy(self):
        for wifi, first, second in [(0, 'a0', 'a1'), (1, 'b0', 'b1')]:
            self.prepare(first, wifi)
            plan = self.create(); self.pending(plan)
            queued = self.create(future=1)
            self.assertEqual(1, queued['kind']); self.assertEqual(wifi, queued['wifi_only'])
            self.assertEqual(NOW + 50000000, queued['not_before_utc'])
            self.prepare(second, wifi)
            due = self.create()
            self.assertEqual(0, due['kind']); self.assertEqual({second}, self.members(due))
        self.assertIsNone(self.create(future=1))
        self.assertIsNone(self.create(now=NOW + 50000000, future=1))

    def test_one_policy_aggregation_cannot_delay_other_policy_or_borrow_its_age(self):
        self.prepare('a0', 1, now=NOW)
        self.prepare('b0', 0, now=NOW + 2000000)
        row = self.create(now=NOW + 3000000, flush=0)
        self.assertEqual({'a0'}, self.members(row))
        self.assertIsNone(self.create(now=NOW + 3000000, flush=0), 'Fresh any-network Plan borrowed Wi-Fi aggregation time')
        row = self.create(now=NOW + 5000000, flush=0)
        self.assertEqual({'b0'}, self.members(row))

    def test_plan_query_fairness_is_independent_of_other_network_policy(self):
        self.prepare('a0', 0); self.prepare('b0', 1)
        any_plan, wifi_plan = self.create(), self.create()
        self.pending(any_plan); self.pending(wifi_plan)
        self.prepare('a1', 0); self.prepare('b1', 1)
        any_query = self.create(now=NOW + 50000000)
        wifi_query = self.create(now=NOW + 50000000)
        self.assertEqual((0, 1), (any_query['wifi_only'], any_query['kind']))
        self.assertEqual((1, 1), (wifi_query['wifi_only'], wifi_query['kind']))
        self.pending(any_query, 10000)
        # An independent policy completing a Plan must not change this policy's turn.
        self.pending(wifi_query, 10000)
        next_any = self.create(now=NOW + 100000000)
        self.assertEqual((0, 0), (next_any['wifi_only'], next_any['kind']))
        self.assertEqual({'a1'}, self.members(next_any))

    def test_pause_cancel_and_reopen_preserve_each_groups_real_ownership(self):
        self.prepare('a0', 0); self.prepare('b0', 1)
        controls = [self.create(), self.create()]
        self.db.close()
        self.db = Repository(ARGS.core, ARGS.repository, self.root, False)
        self.assertIsNone(self.create(), 'Reopen must not duplicate either queued group')
        self.call(15, 1)
        for row in controls:
            self.call(81, row['id'])
            self.assertEqual(0, self.call(82, row['id'], 'system')[0]['admitted'])
            self.call(86, row['id'], 'paused', NOW, 1)
        self.assertEqual([], self.call(18, '', 32), 'A paused response alone does not release owned files')
        for row in controls: self.call(87, row['id'])
        for task in ('a0', 'b0'):
            self.call(102, task, 1)
            self.assertEqual(4, self.call(7, task)[0]['state'])
        self.call(15, 0)
        for task in ('a0', 'b0'): self.call(103, task, 'renewed', NOW)
        self.call(14, 'b0', 2, NOW)
        controls = [self.create(), self.create()]
        by_wifi = {row['wifi_only']: row for row in controls}
        self.assertEqual(2, by_wifi[1]['kind'])
        self.assertEqual(0, by_wifi[0]['kind'])
        for row in controls:
            self.call(81, row['id']); self.call(82, row['id'], 'system'); self.call(83, row['id'])
            body = response(self.db, row['id'], 'Confirmed' if row['wifi_only'] == 0 else 'Canceled')
            self.call(85, row['id'], body, NOW, 0)
            self.call(87, row['id'])
        self.assertEqual(3, self.call(7, 'a0')[0]['state'])
        self.assertEqual(8, self.call(7, 'b0')[0]['state'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    ARGS, rest = parser.parse_known_args()
    unittest.main(argv=[__file__, *rest])
