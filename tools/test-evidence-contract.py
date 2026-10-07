import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from evidence_contract import Evidence, provenance
from stream_hash import sha256_stream
import io
import hashlib


class EvidenceTests(unittest.TestCase):
    def test_error_classification_keeps_native_platform_and_rejects_other_errors(self):
        from evidence_contract import _retryable_replace_error
        from types import SimpleNamespace
        import os
        for code in (5,32,33):self.assertEqual(os.name=='nt',_retryable_replace_error(SimpleNamespace(winerror=code)))
        self.assertFalse(_retryable_replace_error(SimpleNamespace(winerror=2)))
        self.assertFalse(_retryable_replace_error(PermissionError('native failure without a Windows error code')))
    def test_atomic_windows_replace_has_bounded_retry_without_nonatomic_fallback(self):
        from evidence_contract import atomic
        import os
        with tempfile.TemporaryDirectory() as root:
            path=Path(root)/'proof.json';path.write_text('old');real=os.replace;calls=[]
            def transient(source,target):
                calls.append(1)
                if len(calls)<3:
                    error=PermissionError('owned reader');error.winerror=5;raise error
                return real(source,target)
            # Emulate only the error classifier, not os.name (pathlib must retain host semantics).
            with patch('evidence_contract._retryable_replace_error',return_value=True),patch('evidence_contract.os.replace',side_effect=transient),patch('evidence_contract.time.sleep'):
                atomic(path,{'new':True})
            self.assertEqual(3,len(calls));self.assertTrue(json.loads(path.read_text())['new'])
            before=path.read_bytes();error=PermissionError('persistent denial');error.winerror=5
            with patch('evidence_contract._retryable_replace_error',return_value=True),patch('evidence_contract.os.replace',side_effect=error) as replace,patch('evidence_contract.time.sleep'):
                with self.assertRaises(PermissionError):atomic(path,{'mustNotAppear':True})
                self.assertEqual(5,replace.call_count)
            self.assertEqual(before,path.read_bytes());self.assertEqual([path],list(Path(root).iterdir()))
    def test_missing_git_is_unavailable_not_clean_or_zero(self):
        class Missing:
            returncode=1
            stdout=''
        with patch('evidence_contract.subprocess.run',return_value=Missing()):
            report=provenance({})
        self.assertEqual('unavailable',report['source'])
        self.assertIsNone(report['sourceDirty'])
        self.assertIsNone(report['sourceDiffSha256'])
    def test_timeout_and_not_run_are_preserved(self):
        with tempfile.TemporaryDirectory() as root:
            path=Path(root)/'summary.json';evidence=Evidence(path,{},['first','second'])
            evidence.start('first');evidence.finish('first',False,kind='timeout');evidence.complete(False)
            report=json.loads(path.read_text())
            self.assertFalse(report['success']);self.assertEqual(1,report['counts']['failed'])
            self.assertEqual(1,report['counts']['not-run']);self.assertEqual('timeout',report['checks'][0]['failureType'])
    def test_streaming_hash_matches_standard_without_file_digest_api(self):
        data=b'bounded-data'*200000
        self.assertEqual(hashlib.sha256(data).hexdigest(),sha256_stream(io.BytesIO(data)))


if __name__=='__main__':unittest.main()
