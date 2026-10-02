import os
import unittest
from unittest.mock import patch
from benchmark_resources import ProcessSampler


class ResourceTests(unittest.TestCase):
    def test_owned_process_has_attributed_samples(self):
        with ProcessSampler(os.getpid()) as sampler:
            sampler.sample()
        result = sampler.result()
        self.assertGreater(result["samples"], 0)
        self.assertGreater(result["peak_rss_bytes"], 0)
        self.assertEqual(result["source"], "psutil-process-sampled")

    def test_missing_dependency_is_not_zero(self):
        with patch.dict("sys.modules", {"psutil": None}):
            sampler = ProcessSampler(1)
            sampler.sample()
        result = sampler.result()
        self.assertIsNone(result["peak_rss_bytes"])
        self.assertIsNone(result["cpu_seconds"])
        self.assertEqual(result["missing_reason"], "psutil-unavailable")

    def test_missing_process_does_not_leak_error_path(self):
        sampler = ProcessSampler(2147483647)
        sampler.sample()
        self.assertEqual(sampler.result()["missing_reason"], "NoSuchProcess")


if __name__ == "__main__":
    unittest.main()
