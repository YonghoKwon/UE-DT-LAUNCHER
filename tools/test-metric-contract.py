from ci_text import configure_console
configure_console()
import unittest
from metric_contract import sanitize
class Metrics(unittest.TestCase):
    def test_nested_phase_survives_without_paths_secrets_or_nonfinite_values(self):
        value=sanitize({'requests':1,'phaseElapsedMs':{'signing':2,'secret':'Bearer token','catalog-build':float('nan')},'root':'/private','token':'secret'})
        self.assertEqual({'requests':1,'phaseElapsedMs':{'signing':2}},value)
        self.assertIsNone(sanitize({'secret':'token'}))
    def test_authority_metrics_exclude_historical_database_counters(self):
        value=sanitize({'requests':3, 'authorityReads':4, 'authorityReadElapsedMs':2.5,
                        'authorityCommitAttempts':1, 'authorityCommitElapsedMs':5,
                        'databaseOperations':999, 'databaseElapsedMs':999,
                        'phaseElapsedMs':{'authority-writer-wait':1.25, 'database-wait':30}})
        self.assertEqual({'requests':3, 'authorityReads':4, 'authorityReadElapsedMs':2.5,
                          'authorityCommitAttempts':1, 'authorityCommitElapsedMs':5,
                          'phaseElapsedMs':{'authority-writer-wait':1.25}}, value)
if __name__=='__main__':unittest.main()
