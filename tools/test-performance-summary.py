from ci_text import configure_console
configure_console()
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('performance_summary',Path(__file__).with_name('summarize-performance-comparison.py'))
summary=importlib.util.module_from_spec(spec);spec.loader.exec_module(summary)


class Comparison(unittest.TestCase):
    def report(self):
        return {'client':{profile:{scenario:{'medianMs':10} for scenario in summary.SCENARIOS} for profile in summary.PROFILES},
                'api':{str(n):{'medianP95Ms':10} for n in (1,10,30)}}
    def test_single_client_regression_is_not_hidden_by_large_load_improvement(self):
        before,after=self.report(),self.report()
        after['api']['1']['medianP95Ms']=11.1;after['api']['30']['medianP95Ms']=1
        result=summary.compare(before,after)
        self.assertFalse(result['withinTenPercent'])
        self.assertAlmostEqual(1.11,result['maxRatio'])
    def test_partial_report_cannot_be_promoted_to_success(self):
        with self.assertRaises(ValueError):summary.summarize({'complete':False})

    def test_authority_aggregate_from_report_and_same_run_legacy_log(self):
        report = {'complete': True, 'binarySha256': {},
                  'client': [{'profile': profile, 'scenario': scenario, 'warmup': False,
                              'wallMs': 10, 'contentNetworkBytes': 12}
                             for profile in summary.PROFILES for scenario in summary.SCENARIOS for _ in range(3)],
                  'api': [{'clients': n, 'median_p95_ms': 10, 'failed_requests': 0} for n in (1, 10, 30)]}
        metrics = {'requests': 9, 'authorityReads': 7, 'authorityCommitAttempts': 2, 'secret': 'excluded'}
        expected = {'requests': 9, 'authorityReads': 7, 'authorityCommitAttempts': 2, 'phaseElapsedMs': {}}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'results.json'
            path.write_text(json.dumps(report), encoding='utf-8')
            self.assertIsNone(summary.summarize(summary.load_report(path))['authorityAggregate'])
            path.with_name('server.log').write_text(
                'DistributionPerformance ' + json.dumps(metrics) + '\nDistributionPerformance malformed\n',
                encoding='utf-8')
            self.assertEqual(expected, summary.summarize(summary.load_report(path))['authorityAggregate'])
            report['serverMetrics'] = {'gracefulShutdownAggregate': dict(metrics, requests=10)}
            path.write_text(json.dumps(report), encoding='utf-8')
            self.assertEqual(dict(expected, requests=10), summary.summarize(summary.load_report(path))['authorityAggregate'])


if __name__=='__main__':unittest.main()
