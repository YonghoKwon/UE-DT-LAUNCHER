import json
import os
from pathlib import Path
import tempfile
import unittest
from fixture_contract import claim,seal,verify,inside,private_windows_acl
from evidence_contract import Evidence


class ContractTests(unittest.TestCase):
    @unittest.skipUnless(os.name=='nt','Actual Windows ACL regression')
    def test_empty_owned_fixture_removes_explicit_extra_access_before_marker(self):
        import subprocess
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)/'fixture';root.mkdir()
            subprocess.run(['icacls.exe',str(root),'/grant','*S-1-1-0:(OI)(CI)R'],check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
            claim(root);private_windows_acl(root)
            self.assertTrue((root/'.headless-owner.json').exists())
    def test_marker_not_name_authorizes_contained_config_and_binary(self):
        with tempfile.TemporaryDirectory() as temp:
            root=claim(Path(temp)/'fixture');binary=root/'server-exe';binary.write_bytes(b'binary')
            config=root/'server.json';config.write_text(json.dumps({'root':str(root/'server'),'policyPath':str(root/'policy.json'),'signingKeyPath':str(root/'key.pem')}))
            seal(root,{'server':binary},config);verify(root,config,binary)
            config.write_text(json.dumps({'root':temp,'policyPath':str(root/'policy.json'),'signingKeyPath':str(root/'key.pem')}));seal(root,{'server':binary},config)
            with self.assertRaises(ValueError):verify(root,config,binary)
            with self.assertRaises(ValueError):claim(root)
    def test_changed_binary_and_link_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=claim(Path(temp)/'fixture');binary=root/'bin';binary.write_bytes(b'one');config=root/'server.json'
            config.write_text(json.dumps({'root':str(root/'server'),'policyPath':str(root/'policy'),'signingKeyPath':str(root/'key')}));seal(root,{'server':binary},config)
            binary.write_bytes(b'two')
            with self.assertRaises(ValueError):verify(root,config,binary)
            if os.name!='nt':
                (root/'alias').symlink_to(temp,target_is_directory=True)
                with self.assertRaises(ValueError):inside(root,root/'alias/file')
    def test_failed_evidence_has_atomic_checkpoint_without_error_text(self):
        with tempfile.TemporaryDirectory() as temp:
            binary=Path(temp)/'bin';binary.write_bytes(b'one');path=Path(temp)/'report.json'
            result=Evidence(path,{'server':binary},['prepare','run']);result.start('prepare');result.finish('prepare',False,kind='timeout');result.complete(False)
            data=json.loads(path.read_text());self.assertTrue(data['complete']);self.assertFalse(data['success']);self.assertEqual(1,data['counts']['failed']);self.assertEqual(1,data['counts']['not-run'])
            self.assertNotIn(temp,path.read_text());self.assertNotIn('PRIVATE KEY',path.read_text())


if __name__=='__main__':unittest.main()
