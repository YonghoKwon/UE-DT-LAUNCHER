import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from prepare_real_ue_fixture import validate_package, fixture_endpoint


class RealPackagePreflightTests(unittest.TestCase):
    def test_endpoint_is_stable_but_distinguishes_equal_directory_names(self):
        self.assertEqual(fixture_endpoint('run-a/fixture'),fixture_endpoint('run-a/fixture'))
        self.assertNotEqual(fixture_endpoint('run-a/fixture'),fixture_endpoint('run-b/fixture'))

    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base=Path(self.temp.name)
        self.package=self.base/'Windows'
        (self.package/'ma0t10_dt/Binaries/Win64').mkdir(parents=True)
        (self.package/'ma0t10_dt/Content/Paks').mkdir(parents=True)
        (self.package/'ma0t10_dt.exe').write_bytes(b'test-bootstrap')
        (self.package/'ma0t10_dt/Binaries/Win64/ma0t10_dt.exe').write_bytes(b'test-payload')

    def test_accepts_separate_new_root_without_mutating_either(self):
        before=list(self.package.rglob('*'))
        package,root=validate_package(self.package,self.base/'fixture')
        self.assertEqual(self.package.resolve(),package)
        self.assertFalse(root.exists())
        self.assertEqual(before,list(self.package.rglob('*')))

    def test_rejects_bootstrap_only(self):
        (self.package/'ma0t10_dt/Binaries/Win64/ma0t10_dt.exe').unlink()
        with self.assertRaises(ValueError):validate_package(self.package,self.base/'fixture')

    def test_rejects_self_including_archive_destination(self):
        with self.assertRaises(ValueError):validate_package(self.package,self.package/'fixture')

    def test_preserves_existing_root(self):
        root=self.base/'fixture';root.mkdir()
        marker=root/'existing';marker.write_text('keep')
        with self.assertRaises(ValueError):validate_package(self.package,root)
        self.assertEqual('keep',marker.read_text())

    def test_rejects_link_before_resolution(self):
        with patch.object(Path,'is_symlink',return_value=True):
            with self.assertRaises(ValueError):validate_package(self.package,self.base/'fixture')


if __name__=='__main__':unittest.main()
