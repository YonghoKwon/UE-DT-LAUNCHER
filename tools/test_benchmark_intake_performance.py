"""Fast contracts only; never launches a published server/launcher."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("intake_benchmark", Path(__file__).with_name("benchmark-intake-performance.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class IntakeBenchmarkTests(unittest.TestCase):
    def test_fixture_copies_hold_both_inputs_until_atomic_renames(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = root / "fixture"
            fixture.mkdir()
            package = fixture / "Package.zip"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("app.bin", b"synthetic")
            metadata = {"packageFile": package.name, "packageSize": package.stat().st_size,
                        "packageSha256": hashlib.sha256(package.read_bytes()).hexdigest(), "payloadRoot": "."}
            benchmark.write_json(fixture / "release.json", metadata)
            loaded, original, probe, size, expected = benchmark.load_fixture(fixture)
            self.assertEqual((probe, size), ("app.bin", 9))
            self.assertEqual(expected, b"synthetic")
            upload = benchmark.prepare_upload(root / "incoming", "job-0", loaded, original, "1.0.0", "linux-x64", True)
            self.assertFalse((upload / "release.json").exists())
            self.assertFalse((upload / package.name).exists())
            self.assertEqual(len(list(upload.glob("*.uploading"))), 2)
            benchmark.release_upload(upload, package.name)
            self.assertEqual((upload / package.name).read_bytes(), package.read_bytes())
            self.assertEqual(json.loads((upload / "release.json").read_text())["platform"], "linux-x64")
            self.assertEqual(json.loads((fixture / "release.json").read_text()), metadata)

    def test_readonly_poll_excludes_seed_and_preserves_authority(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first, second = root / "incoming/one", root / "incoming/two"
            jobs = root / "authority/jobs/000003.json"
            jobs.parent.mkdir(parents=True)
            rows = [{"source": "incoming/one", "state": "pending"},
                    {"source": "incoming/two", "state": "validating"},
                    {"source": "incoming/seed", "state": "published"}]
            payload = json.dumps({"schemaVersion": 1, "jobs": rows}).encode()
            jobs.write_bytes(payload)
            head = root / "authority/HEAD"
            raw_head = self.make_head(payload)
            head.write_bytes(raw_head)
            (jobs.parent / "000004.json").write_bytes(b'{"schemaVersion":1,"jobs":[]}')
            before = {path.relative_to(root): path.read_bytes() for path in root.rglob("*") if path.is_file()}
            self.assertEqual(benchmark.intake_states(root, [first, second]), {str(first): "pending", str(second): "validating"})
            self.assertEqual(before, {path.relative_to(root): path.read_bytes() for path in root.rglob("*") if path.is_file()})
            jobs.write_bytes(payload + b" ")
            with self.assertRaisesRegex(ValueError, "checksum"):
                benchmark.intake_states(root, [first])

    def test_observer_retries_a_retired_generation_and_reports_a_stable_missing_generation(self):
        root = Path("fixture")
        old = self.make_head(b"old")
        new = self.make_head(b"new")
        with patch.object(benchmark, "read_authority_file", side_effect=[old, FileNotFoundError(), new]):
            with self.assertRaises(benchmark.SnapshotAdvanced):
                benchmark.intake_states(root, [])
        with patch.object(benchmark, "read_authority_file", side_effect=[old, FileNotFoundError(), old]):
            with self.assertRaises(FileNotFoundError):
                benchmark.intake_states(root, [])

    @staticmethod
    def make_head(payload):
        return json.dumps({"schema": 1, "files": {"jobs": "jobs/000003.json"},
                           "sha256": {"jobs": hashlib.sha256(payload).hexdigest()}}).encode()

    def test_latency_summary_separates_range_and_catalog_and_counts_failures(self):
        values = [{"kind": "catalog", "milliseconds": 10, "ok": True},
                  {"kind": "range", "milliseconds": 20, "ok": True},
                  {"kind": "range", "milliseconds": 100, "ok": False}]
        result = benchmark.traffic_summary(values)
        self.assertEqual(result["all"]["errors"], 1)
        self.assertEqual(result["range"]["p95Ms"], 100)
        self.assertEqual(result["catalog"]["requests"], 1)

    def test_parent_escape_package_name_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            benchmark.write_json(root / "release.json", {"packageFile": "../outside.zip"})
            with self.assertRaises(ValueError):
                benchmark.load_fixture(root)


if __name__ == "__main__":
    unittest.main()
