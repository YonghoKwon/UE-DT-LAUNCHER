"""Fast harness contract tests; does not run a launcher or server."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("benchmark", Path(__file__).with_name("benchmark-launcher-performance.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class BenchmarkTests(unittest.TestCase):
    def test_two_versions_are_deterministic_and_ninety_percent_identical(self):
        benchmark.PROFILES["test"] = [(10, 128), (10, 1024)]
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for name, version in (("first", "1.0.0"), ("repeat", "1.0.0"), ("second", "2.0.0")):
                benchmark.fixture(root / name, "test", version, "linux-x64")
            self.assertEqual((root / "first/Package.zip").stat().st_size, (root / "repeat/Package.zip").stat().st_size)
            with zipfile.ZipFile(root / "first/Package.zip") as first, zipfile.ZipFile(root / "repeat/Package.zip") as repeat, zipfile.ZipFile(root / "second/Package.zip") as second:
                total = same = 0
                for name in first.namelist():
                    data = first.read(name)
                    self.assertEqual(data, repeat.read(name))
                    total += len(data)
                    if data == second.read(name):
                        same += len(data)
                self.assertEqual(same * 10, total * 9)

    def test_pretty_printed_legacy_logs_are_parsed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            items = [{"stage": stage, "timestampUtc": f"2026-09-27T00:00:0{i}+00:00", "message": "fixture"}
                     for i, stage in enumerate(("Plan", "Download", "Apply", "Complete"))]
            (root / "log.jsonl").write_text("\n".join(json.dumps(item, indent=2) for item in items), encoding="utf-8")
            result = benchmark.phase_metrics(root)
            self.assertEqual(result["kind"], "legacy-log-boundaries")
            self.assertEqual(result["planAndHashMs"], 1000)
            self.assertEqual(result["applyMs"], 1000)

    def test_metrics_and_percentile(self):
        self.assertEqual(benchmark.percentile([40, 10, 30, 20], .5), 20)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "log.jsonl").write_text(json.dumps({"stage": "Performance", "message": '{"networkBytes":10,"reusedBytes":90}'}), encoding="utf-8")
            self.assertEqual(benchmark.phase_metrics(root)["reusedBytes"], 90)


if __name__ == "__main__":
    unittest.main()
