import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from prepare_versioned_real_ue_fixture import archive_release, inventory, validate_versions, require_stopped, release_paths, validate_cohort, select_snapshot_scope
from gui_fixture_evidence import sha256


class VersionedRealFixtureTests(unittest.TestCase):
    def test_snapshot_scopes_keep_install_logs_and_separate_normal_runtime_writes(self):
        value = {'version': 'v1', 'managed': {'Logs/packaged.log': 'signed'}, 'unmanaged': {'Logs/CustomLogs/runtime.log': 'mutable'},
            'state': {'runtime-state.json': 'running', 'update.lock': '', 'transaction.json': 'protected'},
            'data': {'users/owner/releases/project/prod/stable/v1/windows-x64/user/SaveGames/test.sav': 'save',
                     'users/owner/releases/project/prod/stable/v1/windows-x64/logs/attempt.log': 'log',
                     'users/owner/releases/project/prod/stable/v2/windows-x64/user/SaveGames/test.sav': 'other-version'}}
        protected = select_snapshot_scope(value, 'protected')
        self.assertEqual({'Logs/packaged.log': 'signed'}, protected['managed'])
        self.assertEqual({'transaction.json': 'protected'}, protected['state'])
        data = select_snapshot_scope(value, 'data')
        self.assertEqual(1, len(data['data'])); self.assertTrue(next(iter(data['data'])).endswith('.sav'))
        self.assertEqual(value, select_snapshot_scope(value, 'all'))
    def test_bad_and_duplicate_versions(self):
        for versions in (['v1', 'v1'], ['../escape', 'v2'], ['v1'], ['.', 'v2']):
            with self.assertRaises(ValueError): validate_versions(versions)

    def test_archives_same_payload_and_explicit_nonexecuting_delta(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); package = root/'Windows'; package.mkdir()
            (package/'app.exe').write_bytes(b'unchanged payload')
            before = inventory(package)
            for index in (1, 2): archive_release(package, root/f'{index}.zip', f'v{index}', index, before)
            self.assertEqual(before, inventory(package))
            with zipfile.ZipFile(root/'1.zip') as a, zipfile.ZipFile(root/'2.zip') as b:
                self.assertEqual(a.read('app.exe'), b.read('app.exe'))
                self.assertNotEqual(a.read('FixtureAcceptance/changed.txt'), b.read('FixtureAcceptance/changed.txt'))
                self.assertIn('FixtureAcceptance/v1-only.txt', a.namelist())
                self.assertNotIn('FixtureAcceptance/v1-only.txt', b.namelist())

    def test_source_acceptance_files_are_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); (root/'FixtureAcceptance').mkdir(); (root/'FixtureAcceptance/x').write_text('x')
            with self.assertRaises(ValueError): archive_release(root, root/'test.zip', 'v1', 1, inventory(root))

    def test_missing_unknown_or_wrong_install_runtime_does_not_authorize_damage(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); _, state = release_paths(root, 'v1'); state.mkdir(parents=True)
            with self.assertRaises(FileNotFoundError): require_stopped(root, 'v1')
            (state/'runtime-state.json').write_text(json.dumps({'schemaVersion': 1, 'state': 0, 'origin': 'supervisor-completed', 'installationId': 'wrong'}))
            with self.assertRaises(ValueError): require_stopped(root, 'v1')

    def test_frozen_cohort_rejects_dirty_source_or_changed_binary(self):
        with tempfile.TemporaryDirectory() as d:
            # The real preparation resolves publication inputs; CI TEMP can use a Windows alias.
            root = Path(d).resolve(); binary = root/'UeDtLauncher.exe'; binary.write_bytes(b'fixture binary')
            record = {'schemaVersion': 1, 'rid': 'win-x64', 'sourceHead': 'a'*40,
                'productSourceHash': 'b'*64, 'productSourceDirty': False,
                'binaries': {'launcher': {'path': str(binary), 'sha256': sha256(binary)}}}
            path = root/'cohort.json'; path.write_text(json.dumps(record))
            self.assertEqual('a'*40, validate_cohort(path, {'launcher': binary})['sourceHead'])
            binary.write_bytes(b'changed')
            with self.assertRaises(ValueError): validate_cohort(path, {'launcher': binary})
            binary.write_bytes(b'fixture binary'); record['productSourceDirty'] = True; path.write_text(json.dumps(record))
            with self.assertRaises(ValueError): validate_cohort(path, {'launcher': binary})


if __name__ == '__main__': unittest.main()
