"""Fast contracts only; never launches a published server/launcher."""
from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
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
            loaded, original, probe, size = benchmark.load_fixture(fixture)
            self.assertEqual((probe, size), ("app.bin", 9))
            upload = benchmark.prepare_upload(root / "incoming", "job-0", loaded, original, "1.0.0", "linux-x64", True)
            self.assertFalse((upload / "release.json").exists())
            self.assertFalse((upload / package.name).exists())
            self.assertEqual(len(list(upload.glob("*.uploading"))), 2)
            benchmark.release_upload(upload, package.name)
            self.assertEqual((upload / package.name).read_bytes(), package.read_bytes())
            self.assertEqual(json.loads((upload / "release.json").read_text())["platform"], "linux-x64")
            self.assertEqual(json.loads((fixture / "release.json").read_text()), metadata)

    def test_readonly_poll_excludes_seed_and_cannot_mutate_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            database = root / "distribution.db"
            first, second = root / "first", root / "second"
            with closing(sqlite3.connect(database)) as writable, writable:
                writable.execute("CREATE TABLE jobs(source TEXT,state TEXT)")
                writable.executemany("INSERT INTO jobs VALUES(?,?)", [(str(first), "pending"), (str(second), "validating"), ("seed", "published")])
            connection = benchmark.open_readonly_database(database)
            try:
                self.assertEqual(benchmark.intake_states(connection, [first, second]), {str(first): "pending", str(second): "validating"})
                with self.assertRaises(sqlite3.OperationalError):
                    connection.execute("DELETE FROM jobs")
            finally:
                connection.close()

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
