import json, tempfile, unittest
from pathlib import Path
from unittest.mock import patch
from gui_fixture_evidence import inside, sha256, verify_files, snapshot_preferences, restore_preferences, preference_hash

class EvidenceTests(unittest.TestCase):
    def test_preference_restore_refuses_intervening_change(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); path=root/'ui.json'; path.write_text('original')
            with patch('gui_fixture_evidence.preference_path',return_value=path):
                snapshot_preferences(root); path.write_text('owned'); owned=preference_hash()
                path.write_text('user-change')
                with self.assertRaises(ValueError): restore_preferences(root,owned)
                self.assertEqual('user-change',path.read_text())
                path.write_text('owned'); restore_preferences(root,owned)
                self.assertEqual('original',path.read_text())

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
