"""Tests for the standalone large download fixture client."""
import hashlib
from email.message import Message
from io import BytesIO
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import benchmark_signed_client as benchmark
import large_download_driver as driver


class CapturingKey:
    def sign(self, message, _algorithm):
        self.message = message
        return benchmark.utils.encode_dss_signature(1, 2)


class DriverTests(unittest.TestCase):
    def test_signature_matches_existing_client_with_and_without_range(self):
        for expected in (None, b"example"):
            with self.subTest(expected=expected):
                client = benchmark.Client.__new__(benchmark.Client)
                key = CapturingKey()
                client.device = SimpleNamespace(id="test-pc", key=key)
                client.origin = "http://127.0.0.1:5050"
                client.challenge = "test.challenge"
                captured = {}

                def request(method, path, headers):
                    captured.update(headers)

                client.connection = SimpleNamespace(
                    request=request, getresponse=lambda: SimpleNamespace(status=400, read=lambda _: b""))
                with patch.object(benchmark.secrets, "token_bytes", return_value=bytes(32)):
                    client.get("/example?q=1", expected=expected)
                signature_input, message = driver.request_signature_base(
                    client.origin + "/example?q=1", client.device.id, "A" * 43,
                    client.challenge, captured.get("Range"))
                self.assertEqual(captured["Signature-Input"], signature_input)
                self.assertEqual(key.message, message)

    def test_offset_plan_hashes_prefix_and_uses_open_ended_range(self):
        for offset in (0, 1, 5, 9):
            with self.subTest(offset=offset):
                plan = driver.transfer_plan(10, offset=offset)
                self.assertEqual(("file-range", offset, 9, f"bytes={offset}-", None), plan[-1])
                if offset:
                    self.assertEqual(("file-prefix", 0, offset - 1, f"bytes=0-{offset-1}", None), plan[0])
                else:
                    self.assertEqual(1, len(plan))

    def test_abort_resumes_at_first_unread_byte(self):
        self.assertEqual(driver.transfer_plan(1_000_000_000, abort=300_000_000), [
            ("file-initial", 0, 999_999_999, None, 300_000_000),
            ("file-resume", 300_000_000, 999_999_999, "bytes=300000000-", None)])

    def test_bad_offsets_are_rejected(self):
        for kwargs in ({"offset": -1}, {"offset": 10}, {"abort": 0}, {"abort": 10},
                       {"offset": 0, "abort": 1}):
            with self.subTest(kwargs=kwargs), self.assertRaises(driver.DriverError):
                driver.transfer_plan(10, **kwargs)

    def test_content_range_must_match_exact_start_end_total(self):
        for content_range in ("bytes 5-9/10", "bytes 0-9/10", "bytes 5-8/10", "bytes 5-9/11", None):
            with self.subTest(content_range=content_range):
                headers = Message()
                if content_range:
                    headers["Content-Range"] = content_range
                headers["Content-Length"] = "5"
                response = SimpleNamespace(headers=headers, getheader=headers.get)
                row = {}
                if content_range == "bytes 5-9/10":
                    driver.validate_file_headers(response, row, 5, 9, 10, True)
                    self.assertEqual(row["contentRange"], {"start": 5, "end": 9, "total": 10})
                else:
                    with self.assertRaisesRegex(driver.DriverError, "^content-range-mismatch$"):
                        driver.validate_file_headers(response, row, 5, 9, 10, True)

    def test_duplicate_range_rejected(self):
        headers = Message()
        headers["Content-Range"] = "bytes 5-9/10"
        headers["Content-Range"] = "bytes 5-9/10"
        response = SimpleNamespace(headers=headers, getheader=headers.get)
        with self.assertRaisesRegex(driver.DriverError, "^content-range-mismatch$"):
            driver.validate_file_headers(response, {}, 5, 9, 10, True)

    def test_chunk_hash_across_disconnected_bodies_matches_full_hash(self):
        data = bytes(range(256)) * 32
        digest = hashlib.sha256()
        output = BytesIO()
        first = BytesIO(data)
        with patch.object(driver, "CHUNK", 113):
            driver.stream_bytes(first, {"bytes": 0}, 300, digest, output)
            self.assertEqual(first.tell(), 300)
            driver.stream_bytes(BytesIO(data[300:]), {"bytes": 0}, len(data) - 300, digest, output)
        self.assertEqual(digest.hexdigest(), hashlib.sha256(data).hexdigest())
        self.assertEqual(output.getvalue(), data)

    def test_short_body_is_a_connection_drop_with_observed_bytes(self):
        row = {"bytes": 0}
        with self.assertRaisesRegex(driver.DriverError, "^connection-dropped$"):
            driver.stream_bytes(BytesIO(b"abc"), row, 4, hashlib.sha256(), None)
        self.assertEqual(row["bytes"], 3)

    def test_origin_uses_literal_server_authority(self):
        for value in ("http://fixture.internal:9876", "http://[::1]:9876"):
            self.assertEqual(driver.validate_origin(value).geturl(), value)
        for value in ("http://host:9876/", "http://host:9876?", "http://host:9876#",
                      "https://host:9876", "http://user@host:9876"):
            with self.subTest(value=value), self.assertRaises(driver.DriverError):
                driver.validate_origin(value)

    def test_request_target_uses_canonical_http_authority(self):
        for origin, authority in (("http://FIXTURE.internal:80", "http://fixture.internal"),
                                  ("http://127.001.0.1:90", "http://127.1.0.1:90"),
                                  ("http://[0:0:0:0:0:0:0:1]:9000", "http://[::1]:9000"),
                                  ("http://[0:0:0:0:0:ffff:c000:201]:90", "http://[::ffff:192.0.2.1]:90"),
                                  ("http://[2001:db8::5efe:c000:201]:90", "http://[2001:db8::5efe:192.0.2.1]:90")):
            client = driver.Client(origin, "test-pc", None, 1, {"requests": []})
            self.assertEqual(client.origin, origin)
            self.assertEqual(client.signed_origin, authority)

    def test_barrier_summary_preserves_failure_and_reports_observed_spread(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            first, second, report = [root / name for name in ("first.json", "second.json", "combined.json")]
            first.write_text(json.dumps({"status": "ok", "barrierStartUnixNs": 123, "transferStartUnixNs": 456}))
            second.write_text(json.dumps({"status": "failed", "barrierStartUnixNs": 2_000_123}))
            args = SimpleNamespace(report=report, reports=[first, second])
            self.assertEqual(driver.barrier_summary(args), 1)
            self.assertEqual(json.loads(report.read_text()), {"participants": 2, "failedParticipants": 1,
                                                              "barrierSpreadMs": 2.0, "transferSpreadMs": None})
            with self.assertRaisesRegex(driver.DriverError, "^report-already-exists$"):
                driver.barrier_summary(args)


if __name__ == "__main__":
    unittest.main()
