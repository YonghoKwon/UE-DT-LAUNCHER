import unittest
from metric_contract import sanitize
class Metrics(unittest.TestCase):
    def test_nested_phase_survives_without_paths_secrets_or_nonfinite_values(self):
        value=sanitize({'requests':1,'phaseElapsedMs':{'signing':2,'secret':'Bearer token','catalog-build':float('nan')},'root':'/private','token':'secret'})
        self.assertEqual({'requests':1,'phaseElapsedMs':{'signing':2}},value)
        self.assertIsNone(sanitize({'secret':'token'}))
if __name__=='__main__':unittest.main()
