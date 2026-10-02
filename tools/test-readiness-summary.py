import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('readiness_runner', Path(__file__).with_name('run-readiness-regression.py'))
runner = importlib.util.module_from_spec(spec); spec.loader.exec_module(runner)


class SummaryTests(unittest.TestCase):
    def evidence(self):
        return {'readiness_proofs': [dict(mode=mode, offline_inventory_unchanged=True, recommendation_checked=True,
            exact_checked=True, missing_project_checked=True) for mode in ('portable', 'managed-agent')],
            'versions_installed_and_launched': 2, 'repair': True, 'managed_asset_and_doctor': True,
            'readiness_revocation_rejected': True, 'secret': 'Bearer sentinel', 'privateKeyPem': 'PRIVATE KEY sentinel'}

    def test_only_allowlisted_values_leave_private_evidence(self):
        report = runner.sanitized_summary('a' * 40, 'Windows', True, self.evidence())
        text = json.dumps(report)
        self.assertNotIn('sentinel', text); self.assertNotIn('privateKey', text)
        self.assertEqual(8, report['counts']['passed'])

    def test_failure_does_not_publish_error_text_or_paths(self):
        report = runner.sanitized_summary('/private/user/secret', 'Linux', False, failure_scenario='Bearer sentinel')
        self.assertEqual('unavailable', report['source'])
        self.assertEqual(1, report['counts']['failed']); self.assertEqual(7, report['counts']['not-run'])
        self.assertNotIn('sentinel', json.dumps(report))

    def test_success_requires_both_modes_and_all_proofs(self):
        evidence = self.evidence(); evidence['readiness_proofs'].pop()
        with self.assertRaises(ValueError): runner.sanitized_summary('a' * 40, 'Windows', True, evidence)
        evidence = self.evidence(); evidence['repair'] = False
        with self.assertRaises(ValueError): runner.sanitized_summary('a' * 40, 'Windows', True, evidence)


if __name__ == '__main__': unittest.main()
