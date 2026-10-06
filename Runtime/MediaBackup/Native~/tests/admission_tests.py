"""Race-boundary checks through the production repository, with no mocked SQL."""
import argparse
import hashlib
import tempfile
import unittest
from pathlib import Path
from process_windows import Repository, IDENTITY
from protocol_fixture import NOW, control, apply

DATA = b'photo'
HASH = hashlib.sha256(DATA).hexdigest()


class AdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.db = Repository(ARGS.core, ARGS.repository, self.root, True)
        self.call(58, 'ee')

    def tearDown(self):
        self.db.close()
        self.temp.cleanup()

    def call(self, command, *args):
        result = self.db.call(command, *args)
        return result[-1] if result else []

    def scope(self, scope='aa', source='file:photo'):
        self.call(30, scope, 'directory:' + scope, 'bb', 1, 1, 1, 1)
        run = scope + 'f'
        self.call(31, scope, run, 'bb', 1, 1, 1, 0)
        self.call(32, scope, run, 1, source, 'v1', 'photo.jpg', 'image/jpeg', source, len(DATA))
        while self.call(33, scope, run, 'bb', 1, 1, 1, 0, 0, 0)[0]['pending']:
            pass
        return self.call(73, scope)[0]['admission_epoch']

    def register(self, task='cc', scope='aa', source='file:photo'):
        epoch = self.call(73, scope)[0]['admission_epoch'] if scope else 0
        self.call(2, task+'e', 'ee', NOW, 1, task, source, 'v1', 'photo.jpg', 'image/jpeg', scope, epoch)

    def begin(self, task='cc', size=len(DATA), budget=100):
        return self.call(77, task, 'ee', size, budget, budget, NOW)[0]

    def seal(self, task='cc'):
        (self.root / IDENTITY / 'payloads' / (task+'.payload')).write_bytes(DATA)
        self.call(3, task, 'ee', len(DATA), HASH, 'image/jpeg', 100, NOW)

    def accept(self, task='cc'):
        self.assertEqual(1, self.call(96, task, 'ee', NOW)[0]['accepted'])
        self.call(9, task, 0, 0, NOW)
        self.call(74, task, 1, 'protected-api')

    def test_revocation_before_reservation_refuses_io_and_survives_reopen(self):
        self.scope(); self.register()
        self.call(69, 'aa', 0, NOW)
        self.assertEqual(32, self.begin()['reasons'])
        self.assertEqual(0, self.call(7, 'cc')[0]['preparation_started'])
        self.db.close(); self.db = Repository(ARGS.core, ARGS.repository, self.root, False)
        self.call(58, 'ff'); self.call(55, 'ff', NOW, 32)
        self.assertEqual(7, self.call(7, 'cc')[0]['state'])
        self.assertEqual(0, self.call(35, 'aa', '', '', 0, 32)[0]['disposition'])

    def test_revocation_after_seal_blocks_acceptance_and_preserves_owned_file(self):
        self.scope(); self.register(); self.begin(); self.seal()
        self.call(69, 'aa', 0, NOW)
        self.assertEqual(0, self.call(96, 'cc', 'ee', NOW)[0]['accepted'])
        self.assertTrue((self.root/IDENTITY/'payloads/cc.payload').exists())
        self.call(97, 'cc', 'ee', 'scope closed', NOW)
        self.assertEqual(len(DATA), self.call(19, 'cc', NOW, NOW)[0]['freed_bytes'])

    def test_rejoining_source_does_not_adopt_sealed_but_unaccepted_failed_task(self):
        self.scope(); self.register(); self.begin(); self.seal()
        self.call(69, 'aa', 0, NOW); self.call(97, 'cc', 'ee', 'scope closed', NOW)
        self.call(14, 'cc', 2, NOW)  # A subsequent cancel cannot fabricate acceptance.
        for before, after, kind in [(0, 1, 3), (1, 2, 0)]:
            self.call(34, 'aa', 'bb', 1, 1, 1, before, after, 0, 1, after, kind,
                      'file:photo', 'v1', 'photo.jpg', 'image/jpeg', 'photo', len(DATA))
        candidate = self.call(35, 'aa', '', '', 0, 32)[0]
        self.assertIsNone(candidate['task_id'])
        self.call(109, 'aa')
        self.call(31, 'aa', 'af', 'bb', 1, 1, 1, 2)
        self.call(32, 'aa', 'af', 1, 'file:photo', 'v1', 'photo.jpg', 'image/jpeg', 'photo', len(DATA))
        self.call(33, 'aa', 'af', 'bb', 1, 1, 1, 2, 2, 0)
        self.register('cd')
        self.assertEqual(1, self.begin('cd')['admitted'])

    def test_confirmation_does_not_revalidate_old_registration(self):
        self.scope(); self.register(); self.call(69, 'aa', 0, NOW)
        self.call(109, 'aa')
        self.assertEqual(32, self.begin()['reasons'])
        self.assertEqual(0, self.call(78, 'cc')[0]['scope_valid'])

    def test_scope_pause_cannot_adopt_manual_or_other_scope_task(self):
        self.scope('aa'); self.scope('ab')
        for task, scope in [('c1', ''), ('c2', 'aa'), ('c3', 'ab')]:
            self.register(task, scope); self.begin(task); self.seal(task); self.accept(task)
        self.call(69, 'aa', 0, NOW)
        self.assertEqual(1, self.call(78, 'c1')[0]['scope_valid'])
        self.assertEqual(1, self.call(78, 'c3')[0]['scope_valid'])
        self.assertEqual(0, self.call(78, 'c2')[0]['scope_valid'])
        self.assertEqual('c3', self.call(35, 'ab', '', '', 2, 32)[0]['task_id'])

    def test_sealed_control_cannot_start_after_revocation(self):
        self.scope(); self.register(); self.begin(); self.seal(); self.accept()
        request = control(self.db, 'ce', now=NOW, submit=False)
        self.call(81, request['id']); self.call(69, 'aa', 0, NOW)
        self.assertEqual(0, self.call(82, 'ce', 'system')[0]['admitted'])
        self.call(86, 'ce', 'closed', NOW, 1); self.call(87, 'ce')
        self.assertEqual(4, self.call(7, 'cc')[0]['state'])

    def test_submitted_control_rechecks_scope_before_network_start(self):
        self.scope(); self.register(); self.begin(); self.seal(); self.accept()
        control(self.db, 'ce', now=NOW, submit=False); self.call(81, 'ce')
        self.assertEqual(1, self.call(82, 'ce', 'system')[0]['admitted'])
        self.call(69, 'aa', 0, NOW)
        self.assertEqual(0, self.call(83, 'ce')[0]['admitted'])
        self.call(86, 'ce', 'closed', NOW, 1); self.call(87, 'ce')
        self.assertEqual(4, self.call(7, 'cc')[0]['state'])

    def test_explicit_reconciliation_can_confirm_unknown_result_after_scope_closes(self):
        self.scope(); self.register(); self.begin(); self.seal(); self.accept()
        control(self.db, 'ce', now=NOW)
        self.call(86, 'ce', 'network result unknown', NOW, 0); self.call(87, 'ce')
        self.call(102, 'cc', 1); self.call(69, 'aa', 0, NOW)
        self.call(101, 'cc', NOW, 0, 'current-credential')
        request = control(self.db, 'cf', now=NOW)
        self.assertEqual(1, request['kind'])
        apply(self.db, 'cf'); self.call(87, 'cf')
        self.assertEqual(3, self.call(7, 'cc')[0]['state'])

    def test_pause_then_resume_does_not_revive_unaccepted_preparations(self):
        self.register(scope=''); self.call(15, 1); self.call(15, 0)
        self.assertEqual(16, self.begin()['reasons'])
        self.assertEqual(1, self.call(78, 'cc')[0]['stop_requested'])
        self.register('cd', '')
        self.assertEqual(1, self.begin('cd')['admitted'])

    def test_controls_have_one_scope_owner_and_late_receipt_remains_authoritative(self):
        self.scope('aa'); self.scope('ab')
        for task, scope in [('c1', 'aa'), ('c2', 'ab')]:
            self.register(task, scope); self.begin(task); self.seal(task); self.accept(task)
        import json
        request = control(self.db, 'ce', now=NOW)
        members = json.loads(request['body'])['items']; self.assertEqual(1, len(members))
        task = members[0]['clientTaskId']; scope = self.call(7, task)[0]['scope_id']
        self.call(69, scope, 0, NOW)
        apply(self.db, 'ce'); self.call(87, 'ce')
        self.assertEqual(3, self.call(7, task)[0]['state'])
        next_request = control(self.db, 'cf', now=NOW)
        self.assertNotEqual(task, json.loads(next_request['body'])['items'][0]['clientTaskId'])

    def test_upload_description_does_not_bypass_closed_scope(self):
        self.scope(); self.register(); self.begin(); self.seal(); self.accept()
        control(self.db, 'ce', now=NOW); apply(self.db, 'ce', 'UploadRequired'); self.call(87, 'ce')
        self.call(69, 'aa', 0, NOW)
        self.assertEqual(0, self.call(91, 'cc', 1, 'system', NOW)[0]['started'])

    def test_capacity_wait_is_not_an_attempt_or_a_failure(self):
        self.register('c1', ''); self.begin('c1', budget=10); self.seal('c1'); self.accept('c1')
        self.register('c2', '')
        waiting = self.begin('c2', size=10, budget=10)
        self.assertEqual(0, waiting['admitted']); self.assertEqual(1, waiting['reasons'])
        self.assertEqual(5, waiting['available_bytes'])
        self.assertEqual(0, self.call(7, 'c2')[0]['preparation_started'])
        self.call(79, 'c2e')
        self.assertTrue(self.begin('c2', size=10, budget=10)['reasons'] & 16)

    def test_routine_reconciliation_does_not_revoke_retained_task(self):
        self.scope(); self.register(); self.begin(); self.seal(); self.accept()
        self.call(31, 'aa', 'ad', 'bb', 1, 1, 1, 0)
        self.assertEqual(1, self.call(78, 'cc')[0]['scope_valid'])
        self.call(94, 0, NOW)
        self.assertEqual(1, self.call(7, 'cc')[0]['state'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--core', required=True)
    parser.add_argument('--repository', required=True)
    ARGS, remaining = parser.parse_known_args()
    unittest.main(argv=['admission_tests', *remaining])
