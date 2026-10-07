"""Fast supplemental benchmark contracts; no published processes or real HTTP connections."""
import base64
from contextlib import closing
import importlib.util
import itertools
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest import mock


spec = importlib.util.spec_from_file_location("distribution_api_benchmark", Path(__file__).with_name("benchmark-distribution-api.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


def fixture(root):
    server = root / "server"
    directory = server / "releases/bench-small/prod/stable/1.0.0/windows-x64"
    (directory / "files").mkdir(parents=True)
    (directory / "files/data.bin").write_bytes(b"a" * 2048)
    benchmark.write_json(directory / "manifest.json", {"files": [{"path": "data.bin", "size": 2048}]})
    benchmark.write_json(root / "results.json", {"schemaVersion": 1, "transport": "loopback HTTP + test", "api": [{}], "intake": [{}]})
    benchmark.write_json(root / "policy.json", {"clients": [{"id": "bench", "addresses": ["127.0.0.1"], "grants": [
        {"projectId": "bench-small", "environment": "prod", "channel": "stable", "versions": []}]}]})
    (root / "key.pem").write_text("unit-test placeholder, not a key", encoding="utf-8")
    benchmark.write_json(root / "server.json", {
        "root": str(server), "policyPath": str(root / "policy.json"), "signingKeyPath": str(root / "key.pem"),
        "publicUrl": "http://127.0.0.1:15000", "listenUrl": "http://127.0.0.1:15001"})
    with closing(sqlite3.connect(server / "distribution.db")) as db:
        db.execute("CREATE TABLE releases(id TEXT, directory TEXT, metadata TEXT)")
        db.execute("INSERT INTO releases VALUES(?,?,?)", ("bench-small/prod/stable/1.0.0/windows-x64", str(directory), '{"projectId":"bench-small"}'))
        db.commit()
    return root / "server.json", directory


class DistributionApiBenchmarkTests(unittest.TestCase):
    def test_fixture_is_bounded_and_read_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            config, _ = fixture(root)
            before = {str(path): path.read_bytes() for path in root.rglob("*") if path.is_file()}
            settings, selection = benchmark.validate_fixture(config)
            self.assertEqual(settings["root"], str(root / "server"))
            self.assertEqual(selection["rangeHeader"], "bytes=0-1023")
            self.assertEqual(selection["contentRange"], "bytes 0-1023/2048")
            self.assertEqual(selection["expectedBytes"], b"a" * 1024)
            self.assertEqual(before, {str(path): path.read_bytes() for path in root.rglob("*") if path.is_file()})

    def test_company_policy_and_unfinished_benchmark_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            config, _ = fixture(root)
            report = benchmark.read_json(root / "results.json")
            report["api"] = []
            benchmark.write_json(root / "results.json", report)
            with self.assertRaises(ValueError):
                benchmark.validate_fixture(config)
            report["api"] = [{}]
            benchmark.write_json(root / "results.json", report)
            policy = benchmark.read_json(root / "policy.json")
            policy["clients"][0]["id"] = "company-pc"
            benchmark.write_json(root / "policy.json", policy)
            with self.assertRaises(ValueError):
                benchmark.validate_fixture(config)

    def test_external_root_and_traversing_manifest_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            config, directory = fixture(root)
            settings = benchmark.read_json(config)
            settings["root"] = str(root)
            benchmark.write_json(config, settings)
            with self.assertRaises(ValueError):
                benchmark.validate_fixture(config)
            settings["root"] = str(root / "server")
            benchmark.write_json(config, settings)
            benchmark.write_json(directory / "manifest.json", {"files": [{"path": "../secret", "size": 1}]})
            with self.assertRaises(ValueError):
                benchmark.validate_fixture(config)

    def test_document_size_limit(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "oversize.json"
            path.write_text(" " * 100, encoding="utf-8")
            with self.assertRaises(ValueError):
                benchmark.read_json(path, limit=64)

    def test_summary_does_not_hide_errors_in_success_latency(self):
        samples = [
            {"elapsedMs": 10, "status": 200, "bytes": 20, "error": None},
            {"elapsedMs": 30, "status": 206, "bytes": 10, "error": None},
            {"elapsedMs": 1, "status": 500, "bytes": 0, "error": "unexpected-status"},
            {"elapsedMs": 2, "status": 0, "bytes": 0, "error": "transport"},
        ]
        result = benchmark.summarize(samples)
        self.assertEqual(result["requests"], 4)
        self.assertEqual(result["errors"], 2)
        self.assertEqual(result["statusCounts"], {"0": 1, "200": 1, "206": 1, "500": 1})
        self.assertEqual(result["receivedBytes"], 30)
        self.assertEqual(result["p50Ms"], 2)
        self.assertEqual(result["successP50Ms"], 10)

    def test_persistent_workers_alternate_catalog_and_exact_range(self):
        connections = []
        sequences = itertools.count(1)
        selection = {"filePath": "/releases/fixture/files/data.bin", "rangeHeader": "bytes=0-2", "contentRange": "bytes 0-2/20", "expectedBytes": b"abc"}

        class Response:
            def __init__(self, catalog):
                self.status = 200 if catalog else 206
                self.catalog = catalog

            def read(self, limit):
                if not self.catalog:
                    return b"abc"
                payload = base64.b64encode(json.dumps({"sequence": next(sequences)}).encode()).decode()
                return json.dumps({"payload": payload, "signatureDocument": "fixture-signature"}).encode()

            def getheader(self, name):
                return {"Content-Range": "bytes 0-2/20", "Cache-Control": "no-store"}.get(name)

        class Connection:
            def __init__(self, *_args, **_kwargs):
                self.paths = []
                self.closed = False
                connections.append(self)

            def request(self, method, path, headers):
                self.paths.append(path)
                self.catalog = path == "/api/v1/catalog"
                if not self.catalog:
                    self.assert_range = headers["Range"]

            def getresponse(self):
                return Response(self.catalog)

            def close(self):
                self.closed = True

        with mock.patch.object(benchmark.http.client, "HTTPConnection", Connection):
            result = benchmark.load(15000, "unit-test-placeholder", 3, selection)
        self.assertEqual(len(connections), 3)
        self.assertTrue(all(connection.closed for connection in connections))
        self.assertTrue(all(len(connection.paths) == 20 for connection in connections))
        self.assertTrue(all(connection.assert_range == "bytes=0-2" for connection in connections))
        self.assertEqual(result["requests"], 60)
        self.assertEqual(result["statusCounts"], {"200": 30, "206": 30})
        self.assertEqual(result["errors"], 0)
        self.assertEqual(result["uniqueCatalogSequences"], 30)
        self.assertEqual(result["duplicateCatalogSequences"], 0)
        self.assertEqual(result["workloads"]["range"]["receivedBytes"], 90)

    def test_missing_shutdown_metrics_are_null_not_zero(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "server.log"
            path.write_text("SQLite Error 5: database is locked\n", encoding="utf-8")
            result = benchmark.log_metrics(path)
            self.assertEqual(result["busyErrorLogLines"], 1)
            self.assertIsNone(result["gracefulShutdownAggregate"])
            self.assertIsNone(result["nativeSqliteWaitMs"])
            path.write_text('DistributionPerformance {"requests": 3, "sequenceWaitMs": 1.5}\n', encoding="utf-8")
            self.assertEqual(benchmark.log_metrics(path)["gracefulShutdownAggregate"]["requests"], 3)

    def test_malformed_catalogs_are_counted_without_aborting(self):
        for body in (b"[]", b"null", b'"text"', json.dumps({"payload": base64.b64encode(b"[]").decode(), "signatureDocument": "signature"}).encode()):
            with self.subTest(body=body):
                result = self.run_catalog_body(body)
                self.assertEqual(result["requests"], 20)
                self.assertEqual(result["workloads"]["catalog"]["errorKinds"], {"catalog-shape": 10})
                self.assertEqual(result["workloads"]["range"]["errors"], 0)

    def test_replayed_catalog_sequences_are_failures(self):
        body = json.dumps({"payload": base64.b64encode(b'{"sequence":1}').decode(), "signatureDocument": "signature"}).encode()
        result = self.run_catalog_body(body)
        self.assertEqual(result["errors"], 9)
        self.assertEqual(result["duplicateCatalogSequences"], 9)
        self.assertEqual(result["workloads"]["catalog"]["errorKinds"], {"catalog-replayed-sequence": 9})

    @staticmethod
    def run_catalog_body(body):
        selection = {"filePath": "/files/data", "rangeHeader": "bytes=0-2", "contentRange": "bytes 0-2/3", "expectedBytes": b"abc"}
        connection = mock.Mock()

        def request(_method, path, headers):
            response = mock.Mock()
            catalog = path == "/api/v1/catalog"
            response.status = 200 if catalog else 206
            response.read.return_value = body if catalog else b"abc"
            response.getheader.side_effect = lambda name: {"Content-Range": "bytes 0-2/3", "Cache-Control": "no-store"}.get(name)
            connection.getresponse.return_value = response

        connection.request.side_effect = request
        with mock.patch.object(benchmark.http.client, "HTTPConnection", return_value=connection):
            return benchmark.load(15000, "unit-test-placeholder", 1, selection)


if __name__ == "__main__":
    unittest.main()
