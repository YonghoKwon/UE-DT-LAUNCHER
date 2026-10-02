import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from prepare_versioned_real_ue_fixture import archive_release, inventory, validate_versions, require_stopped, release_paths


class VersionedRealFixtureTests(unittest.TestCase):
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


if __name__ == '__main__': unittest.main()
