import importlib.util
from pathlib import Path
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


if __name__=='__main__':unittest.main()
