import importlib.util
from pathlib import Path
import stat
import unittest

spec = importlib.util.spec_from_file_location('rpm_contract', Path(__file__).with_name('verify-rpm-payload.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def entry(name, body=b'', mode=stat.S_IFREG | 0o755, links=1):
    name = name.encode() + b'\0'
    fields = [1, mode, 0, 0, links, 0, len(body), 0, 0, 0, 0, len(name), 0]
    header = b'070701' + ''.join(f'{value:08x}' for value in fields).encode()
    prefix = header + name
    prefix += b'\0' * (-len(prefix) % 4)
    return prefix + body + b'\0' * (-len(body) % 4)


class Archive(unittest.TestCase):
    def test_normal_hashable_payload(self):
        rows = list(module.entries(entry('./opt/app', b'payload') + entry('TRAILER!!!')))
        self.assertEqual(('opt/app', stat.S_IFREG | 0o755, b'payload'), rows[0])

    def test_paths_links_duplicates_and_truncation_rejected(self):
        bad = [entry('../app'), entry('/app'), entry('./app', mode=stat.S_IFLNK | 0o777),
               entry('./app', links=2), entry('./app') + entry('./app'), entry('./app', b'data')[:-2]]
        for data in bad:
            with self.subTest(data=data[:10]), self.assertRaises(ValueError):
                list(module.entries(data + entry('TRAILER!!!')))

    def test_official_pipeline_has_no_stale_glob(self):
        root = Path(__file__).resolve().parents[1]
        build = (root/'scripts/build-rpm.sh').read_text()
        release = (root/'.github/workflows/release.yml').read_text()
        self.assertIn('mktemp -d', build)
        self.assertIn('verify-rpm-payload.py', build)
        self.assertIn('Unverified prepublished payload reuse is not supported', build)
        self.assertNotIn('find artifacts/linux-rpm', release)
        self.assertEqual(2, release.count('needs: [verify-signing-gate, functional-gate]'))


if __name__ == '__main__':
    unittest.main()
