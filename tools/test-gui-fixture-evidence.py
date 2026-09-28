import json, tempfile, unittest
from pathlib import Path
from gui_fixture_evidence import inside, sha256, verify_files

class EvidenceTests(unittest.TestCase):
    def test_paths_and_hashes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); (root/'file').write_bytes(b'good')
            manifest=root/'manifest.json'
            manifest.write_text(json.dumps({'files':[{'path':'file','size':4,'sha256':sha256(root/'file')}]}))
            self.assertEqual(1,verify_files(root,manifest))
            with self.assertRaises(ValueError): inside(root,'../outside')
            (root/'file').write_bytes(b'bad!')
            with self.assertRaises(ValueError): verify_files(root,manifest)

if __name__=='__main__': unittest.main()
