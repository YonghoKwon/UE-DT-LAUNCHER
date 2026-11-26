"""Test-only signed HTTP streaming driver. Requires Python 3.10+ and cryptography."""
import argparse
import base64
from contextlib import contextmanager, nullcontext
from datetime import datetime, timedelta, timezone
import hashlib
import http.client
import ipaddress
import json
import math
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
from tempfile import NamedTemporaryFile
import time
from urllib.parse import quote, urlsplit

from cryptography.exceptions import InvalidSignature, UnsupportedAlgorithm
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils


CHUNK = 1024 * 1024
METADATA_LIMIT = 8 * CHUNK
IDENTIFIER = r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}"
RELEASE_SEGMENT = r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}"
CATALOG_PATH = "/api/v1/catalog"


class DriverError(Exception):
    """Only fixed, non-sensitive error classifications reach the report."""


def require(condition, reason):
    if not condition:
        raise DriverError(reason)


def validate_origin(origin):
    uri = urlsplit(origin)
    host = uri.hostname
    require(uri.scheme == "http" and host and uri.port != 0
            and re.fullmatch(r"(?:[A-Za-z0-9._-]+|\[[a-fA-F0-9:.]+\])(?::[0-9]+)?", uri.netloc)
            and origin == f"http://{uri.netloc}",
            "invalid-origin")
    return uri


def canonical_host(host):
    if ":" not in host:
        try:
            return socket.inet_ntoa(socket.inet_aton(host))
        except OSError:
            return host
    address = ipaddress.IPv6Address(host)
    blocks = tuple(int(block, 16) for block in address.exploded.split(":"))
    marker = blocks[4:6]
    # System.Uri renders the IPv4 tail in dotted form for these embedded address forms.
    embedded = not any(blocks[:4]) and blocks[6] and marker in ((0, 0), (0, 0xFFFF), (0xFFFF, 0))
    if embedded or marker == (0, 0x5EFE):
        prefix = ipaddress.IPv6Address(address.packed[:12] + b"\x00\x01\x00\x01").compressed.rsplit(":", 2)[0]
        return f"[{prefix}:{ipaddress.IPv4Address(address.packed[-4:])}]"
    return f"[{address.compressed}]"


def request_signature_base(target, key_id, nonce, challenge, range_value=None):
    components = '("@method" "@target-uri" "x-ue-dt-challenge"'
    if range_value is not None:
        components += ' "range"'
    params = (components + f');keyid="{key_id}";nonce="{nonce}";'
              'alg="ecdsa-p256-sha256";tag="ue-dt-request-v1"')
    message = f'"@method": GET\n"@target-uri": {target}\n"x-ue-dt-challenge": {challenge}\n'
    if range_value is not None:
        message += f'"range": {range_value}\n'
    return "sig1=" + params, (message + '"@signature-params": ' + params).encode("utf-8")


def p256(key):
    require(isinstance(getattr(key, "curve", None), ec.SECP256R1), "invalid-key-curve")
    return key


def verify_document(document, data, key_id, key):
    signature = json.loads(document)
    require((signature["schemaVersion"], signature["algorithm"], signature["keyId"]) ==
            (2, "ECDSA-P256-SHA256", key_id), "untrusted-metadata-signer")
    raw = base64.b64decode(signature["signature"], validate=True)
    require(len(raw) == 64, "invalid-metadata-signature")
    der = utils.encode_dss_signature(int.from_bytes(raw[:32], "big"), int.from_bytes(raw[32:], "big"))
    key.verify(der, data, ec.ECDSA(hashes.SHA256()))


def catalog_timestamp(value):
    # .NET round-trip format has seven fractional digits. Python 3.10 accepts six.
    timestamp = datetime.fromisoformat(re.sub(r"(\.\d{6})\d([+-])", r"\1\2", value))
    require(timestamp.tzinfo is not None, "invalid-catalog-freshness")
    return timestamp


def classify(error):
    if isinstance(error, DriverError):
        return str(error)
    if isinstance(error, TimeoutError):
        return "timeout"
    if isinstance(error, (ConnectionError, http.client.IncompleteRead)):
        return "connection-dropped"
    if isinstance(error, InvalidSignature):
        return "invalid-metadata-signature"
    if isinstance(error, (ValueError, KeyError, TypeError)):
        return "invalid-input-or-metadata"
    if isinstance(error, http.client.HTTPException):
        return "invalid-http"
    return "io-error"


REPORTABLE = (DriverError, OSError, http.client.HTTPException, InvalidSignature,
              UnsupportedAlgorithm, ValueError, KeyError, TypeError, subprocess.SubprocessError)


class Client:
    def __init__(self, origin, key_id, key, timeout, report):
        uri = validate_origin(origin)
        self.origin, self.key_id, self.key = origin, key_id, key
        authority = canonical_host(uri.hostname)
        # The server signs Uri.GetLeftPart(Authority), which elides default ports.
        self.signed_origin = f"http://{authority}" + (f":{uri.port}" if uri.port not in (None, 80) else "")
        self.connection = http.client.HTTPConnection(uri.hostname, uri.port, timeout=timeout)
        self.requests = report["requests"]
        self.challenge = None
        self.challenge_at = 0
        self.report = report

    @contextmanager
    def request(self, kind, path, headers, expected, retry=0):
        row = {"kind": kind, "status": None, "bytes": 0, "startUnixNs": time.time_ns(),
               "retry": retry, "range": headers.get("Range"), "error": None}
        self.requests.append(row)
        if kind == "file" or kind.startswith("file-"):
            self.report.setdefault("firstFileGetUnixNs", row["startUnixNs"])
        start = time.monotonic_ns()
        try:
            self.connection.request("GET", path, headers=headers)
            with self.connection.getresponse() as response:
                row["status"] = response.status
                if response.status != expected:
                    self.connection.close()
                    if response.status == 401 and response.getheader("X-UE-DT-Auth-Error") == "stale-challenge":
                        raise DriverError("stale-challenge")
                    if 500 <= response.status <= 599:
                        raise DriverError("5xx")
                    raise DriverError(str(response.status))
                yield response, row
        except REPORTABLE as error:
            if isinstance(error, http.client.IncompleteRead):
                row["bytes"] += len(error.partial)
            row["error"] = classify(error)
            self.connection.close()
            raise
        finally:
            row["endUnixNs"] = time.time_ns()
            elapsed = (time.monotonic_ns() - start) / 1e9
            row["elapsedSeconds"] = elapsed
            row["bytesPerSecond"] = row["bytes"] / elapsed if elapsed else None

    def challenge_value(self):
        if self.challenge is not None and time.monotonic() - self.challenge_at < 45:
            return self.challenge
        start = time.monotonic()
        with self.request("challenge", "/api/v1/auth/challenge?keyId=" + self.key_id, {}, 200) as (response, row):
            data = json.loads(read_metadata(response, row, 4096))
            challenge = data["challenge"]
            require(data["expiresInSeconds"] == 60 and re.fullmatch(r"[A-Za-z0-9._-]{1,2048}", challenge),
                    "invalid-challenge")
        self.challenge, self.challenge_at = challenge, start
        return challenge

    @contextmanager
    def signed(self, kind, path, range_value=None):
        for retry in (0, 1):
            challenge = self.challenge_value()
            nonce = base64.urlsafe_b64encode(secrets.token_bytes(32)).decode("ascii").rstrip("=")
            target = self.signed_origin + path
            signature_input, message = request_signature_base(target, self.key_id, nonce, challenge, range_value)
            r, s = utils.decode_dss_signature(self.key.sign(message, ec.ECDSA(hashes.SHA256())))
            signature = base64.b64encode(r.to_bytes(32, "big") + s.to_bytes(32, "big")).decode("ascii")
            headers = {"X-UE-DT-Challenge": challenge, "Signature-Input": signature_input,
                       "Signature": f"sig1=:{signature}:"}
            if range_value is not None:
                headers["Range"] = range_value
            try:
                with self.request(kind, path, headers, 206 if range_value else 200, retry) as (response, row):
                    yield response, row, nonce
                return
            except DriverError as error:
                if retry or str(error) != "stale-challenge":
                    raise
                self.report["retryCount"] += 1
                self.challenge = None

    def metadata(self, kind, path):
        with self.signed(kind, path) as (response, row, nonce):
            return read_metadata(response, row, METADATA_LIMIT), nonce

    def catalog(self, key_id, key):
        envelope, nonce = self.metadata("catalog", CATALOG_PATH)
        envelope = json.loads(envelope)
        payload = base64.b64decode(envelope["payload"], validate=True)
        verify_document(envelope["signatureDocument"], payload, key_id, key)
        binding = envelope["requestBinding"]
        fields = ("keyId", "nonce", "method", "targetUri", "payloadSha256")
        expected = [self.key_id, nonce, "GET", self.signed_origin + CATALOG_PATH, hashlib.sha256(payload).hexdigest()]
        require([binding[field] for field in fields] == expected, "catalog-binding-mismatch")
        message = json.dumps(["ue-dt-catalog-request-binding-v1", *expected], separators=(",", ":")).encode("ascii")
        verify_document(binding["signatureDocument"], message, key_id, key)
        catalog = json.loads(payload)
        sequence = catalog["sequence"]
        issued = catalog_timestamp(catalog["issuedAtUtc"])
        expires = catalog_timestamp(catalog["expiresAtUtc"])
        now = datetime.now(timezone.utc)
        require(catalog["schemaVersion"] == 2 and catalog["selectionPolicy"] == "explicit-promotion-v1"
                and type(sequence) is int and sequence > 0
                and now - timedelta(hours=24) <= issued <= now + timedelta(minutes=5)
                and expires > max(now, issued), "invalid-catalog-freshness")
        return catalog


def read_metadata(response, row, limit):
    data = bytearray()
    while len(data) <= limit:
        block = response.read(min(CHUNK, limit + 1 - len(data)))
        if not block:
            require(response.length in (None, 0), "connection-dropped")
            return data
        data.extend(block)
        row["bytes"] += len(block)
    raise DriverError("metadata-too-large")


def file_metadata(client, args, key):
    catalog = client.catalog(args.signing_key_id, key)
    segments = (args.project, args.environment, args.channel, args.version, args.platform)
    releases = [release for project in catalog["projects"] if project["projectId"] == args.project
                for release in project["releases"]
                if tuple(release[field] for field in ("environment", "channel", "version", "platform")) == segments[1:]]
    require(len(releases) == 1, "release-unavailable")
    base = "/releases/" + "/".join(segments)
    manifest_path = base + "/manifest.json"
    signature_path = manifest_path + ".sig"
    release = releases[0]
    require((release["manifestUrl"], release["manifestSignatureUrl"]) ==
            (client.origin + manifest_path, client.origin + signature_path), "unexpected-manifest-url")
    manifest, _ = client.metadata("manifest", manifest_path)
    signature, _ = client.metadata("manifest-signature", signature_path)
    verify_document(signature, bytes(manifest), args.signing_key_id, key)
    manifest = json.loads(manifest)
    require(tuple(manifest[field] for field in ("appId", "channel", "version", "platform")) ==
            (args.project, args.channel, args.version, args.platform), "manifest-selection-mismatch")
    files = [item for item in manifest["files"] if item["path"] == args.path]
    require(len(files) == 1, "file-unavailable")
    item = files[0]
    require(type(item["size"]) is int and item["size"] >= 0
            and re.fullmatch(r"[0-9a-fA-F]{64}", item["sha256"]), "invalid-file-metadata")
    path = base + "/files/" + quote(args.path)
    require(manifest["baseUrl"] == client.origin + base + "/files"
            and item.get("url") in (None, args.path, client.origin + path), "unexpected-file-url")
    return path, item["size"], item["sha256"].lower()


def transfer_plan(size, offset=None, abort=None):
    """Yield (kind, start, inclusive end, Range, intentional stop byte count)."""
    if abort is not None:
        require(0 < abort < size and offset is None, "invalid-abort-offset")
        return [("file-initial", 0, size - 1, None, abort),
                ("file-resume", abort, size - 1, f"bytes={abort}-", None)]
    if offset is None:
        return [("file", 0, size - 1, None, None)]
    require(0 <= offset < size, "invalid-range-offset")
    prefix = [("file-prefix", 0, offset - 1, f"bytes=0-{offset - 1}", None)] if offset else []
    return prefix + [("file-range", offset, size - 1, f"bytes={offset}-", None)]


def validate_file_headers(response, row, start, end, size, is_range):
    ranges = response.headers.get_all("Content-Range", [])
    if len(ranges) == 1:
        # Preserve actual numeric evidence. Never copy arbitrary header text into a report.
        parsed = re.fullmatch(r"bytes ([0-9]{1,20})-([0-9]{1,20})/([0-9]{1,20})", ranges[0])
        if parsed:
            row["contentRange"] = dict(zip(("start", "end", "total"), map(int, parsed.groups())))
    require(ranges == ([f"bytes {start}-{end}/{size}"] if is_range else []), "content-range-mismatch")
    lengths = response.headers.get_all("Content-Length", [])
    require(not lengths or lengths == [str(end - start + 1)], "content-length-mismatch")
    require(response.getheader("Content-Encoding") in (None, "identity"), "unexpected-content-encoding")


def stream_bytes(response, row, count, digest, sink):
    while row["bytes"] < count:
        block = response.read(min(CHUNK, count - row["bytes"]))
        require(block, "connection-dropped")
        row["bytes"] += len(block)
        digest.update(block)
        if sink is not None:
            sink.write(block)


def download(client, path, size, plan, sink):
    digest = hashlib.sha256()
    for kind, start, end, range_value, abort in plan:
        with client.signed(kind, path, range_value) as (response, row, _):
            validate_file_headers(response, row, start, end, size, range_value is not None)
            stream_bytes(response, row, abort if abort is not None else end - start + 1, digest, sink)
            if abort is not None:
                client.connection.close()
                response.close()
                row["error"] = "intentional-disconnect"
            else:
                extra = response.read(1)
                row["bytes"] += len(extra)
                require(not extra, "unexpected-trailing-data")
    return digest.hexdigest()


def barrier(directory, group, participants, slot, timeout):
    require(directory.is_dir(), "invalid-barrier")
    markers = [directory / f"{group}.{participants}.{number}.ready" for number in range(1, participants + 1)]
    with markers[slot - 1].open("xb"):
        pass
    deadline = time.monotonic() + timeout
    while not all(marker.is_file() for marker in markers):
        require(time.monotonic() < deadline, "barrier-timeout")
        time.sleep(0.005)
    return time.time_ns()


def peak_rss_bytes():
    if os.name != "nt":
        import resource
        scale = 1 if sys.platform == "darwin" else 1024
        return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * scale
    import ctypes
    from ctypes import wintypes

    class Counters(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("faults", wintypes.DWORD),
                    ("peak", ctypes.c_size_t), ("other", ctypes.c_size_t * 7)]

    current = ctypes.windll.kernel32.GetCurrentProcess
    current.restype = wintypes.HANDLE
    measure = ctypes.windll.psapi.GetProcessMemoryInfo
    measure.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    counters = Counters()
    counters.size = ctypes.sizeof(counters)
    return counters.peak if measure(current(), ctypes.byref(counters), counters.size) else None


def keygen(args):
    require(re.fullmatch(IDENTIFIER, args.key_id) and not args.work_dir.exists(), "invalid-key-output")
    if os.name == "nt":
        from fixture_contract import claim
        claim(args.work_dir)
    else:
        args.work_dir.mkdir(mode=0o700)
    key = ec.generate_private_key(ec.SECP256R1())
    public = key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo)
    private = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                serialization.NoEncryption())
    path = args.work_dir / "device-private.pem"
    with os.fdopen(os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600), "wb") as stream:
        stream.write(private)
    (args.work_dir / "device-public.json").write_text(json.dumps({
        "schemaVersion": 1, "keyId": args.key_id, "publicKeyPem": public.decode("ascii")}), encoding="utf-8")
    return 0


@contextmanager
def report_file(path):
    require(not path.exists(), "report-already-exists")
    with NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as output:
        temporary = Path(output.name)
        try:
            yield output
            output.close()
            if os.name == "nt":
                temporary.rename(path)
            else:
                # Publish a complete, newly generated report without overwriting another run.
                os.link(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)


def run(args):
    report = {"schemaVersion": 1, "requests": [], "retryCount": 0, "startUnixNs": time.time_ns(),
              "status": "failed", "error": None}
    client = None
    if args.file_out is not None:
        require(args.report.resolve() != args.file_out.resolve(), "report-and-sink-conflict")
    with report_file(args.report) as output:
        try:
            if any(value is not None for value in (args.barrier_dir, args.barrier_group, args.participants, args.slot)):
                require(args.barrier_dir is not None and args.barrier_group is not None
                        and re.fullmatch(IDENTIFIER, args.barrier_group)
                        and args.participants is not None and args.slot is not None
                        and 1 <= args.slot <= args.participants <= 1024, "invalid-barrier")
                report["barrier"] = {"groupId": args.barrier_group, "participants": args.participants, "slot": args.slot}
            require(all(re.fullmatch(IDENTIFIER, value) for value in (args.key_id, args.signing_key_id))
                    and all(re.fullmatch(RELEASE_SEGMENT, value) for value in
                            (args.project, args.environment, args.channel, args.version, args.platform)),
                    "invalid-identifier")
            require(all(part and part not in (".", "..") for part in args.path.split("/"))
                    and "\\" not in args.path and len(quote(args.path)) <= 4096, "invalid-file-path")
            require(math.isfinite(args.timeout) and args.timeout > 0, "invalid-timeout")
            require((args.sink == "file") == (args.file_out is not None), "invalid-sink")
            key = p256(serialization.load_pem_private_key((args.work_dir / "device-private.pem").read_bytes(), None))
            signer = p256(serialization.load_pem_public_key(args.signing_public_key.read_bytes()))
            client = Client(args.origin, args.key_id, key, args.timeout, report)
            path, size, expected = file_metadata(client, args, signer)
            report.update(expectedSha256=expected, expectedBytes=size)
            plan = transfer_plan(size, args.offset, args.abort_after_bytes)
            if args.barrier_dir is not None:
                report["barrierStartUnixNs"] = barrier(
                    args.barrier_dir, args.barrier_group, args.participants, args.slot, args.timeout)
            report["transferStartUnixNs"] = time.time_ns()
            transfer_start = time.monotonic_ns()
            with args.file_out.open("xb") if args.file_out is not None else nullcontext() as sink:
                report["sha256"] = download(client, path, size, plan, sink)
            report["transferEndUnixNs"] = time.time_ns()
            report["transferElapsedSeconds"] = (time.monotonic_ns() - transfer_start) / 1e9
            require(report["sha256"] == expected, "sha256-mismatch")
            report["status"] = "ok"
        except REPORTABLE as error:
            report["error"] = classify(error)
        finally:
            if client is not None:
                client.connection.close()
            report.update(endUnixNs=time.time_ns(), peakRssBytes=peak_rss_bytes(),
                          requestCount=len(report["requests"]))
            json.dump(report, output, indent=2)
            output.write("\n")
    return 0 if report["status"] == "ok" else 1


def barrier_summary(args):
    require(re.fullmatch(IDENTIFIER, args.barrier_group) and 1 <= args.participants <= 1024, "invalid-barrier")
    slots = {number: [] for number in range(1, args.participants + 1)}
    invalid = []
    for index, path in enumerate(args.reports, 1):
        try:
            report = json.loads(path.read_text(encoding="utf-8"))
            identity = report["barrier"]
            slot = identity["slot"]
            if (identity["groupId"] != args.barrier_group or type(identity["participants"]) is not int
                    or identity["participants"] != args.participants or type(slot) is not int or slot not in slots):
                raise ValueError()
        except (OSError, ValueError, KeyError, TypeError):
            invalid.append(index)
            continue
        slots[slot].append(report)

    missing, duplicate, failed, starts = [], [], [], []
    for slot, reports in slots.items():
        if not reports:
            missing.append(slot)
        elif len(reports) > 1:
            duplicate.append(slot)
        else:
            report = reports[0]
            start = report.get("firstFileGetUnixNs")
            if type(start) is int and start > 0:
                starts.append(start)
            if report.get("status") != "ok" or type(start) is not int or start <= 0:
                failed.append(slot)
    failed_count = len(missing) + len(duplicate) + len(failed)
    summary = {"groupId": args.barrier_group, "participants": args.participants, "failedParticipants": failed_count,
               "missingSlots": missing, "duplicateSlots": duplicate, "failedSlots": failed, "invalidReports": invalid,
               "firstFileGetStartsObserved": len(starts),
               "barrierSpreadMs": (max(starts) - min(starts)) / 1e6 if len(starts) == args.participants else None}
    with report_file(args.report) as output:
        json.dump(summary, output, indent=2)
        output.write("\n")
    return int(bool(failed_count or invalid))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(required=True)
    key_parser = commands.add_parser("keygen", help="Create a fresh private work directory and public registration JSON")
    key_parser.add_argument("--work-dir", required=True, type=Path)
    key_parser.add_argument("--key-id", required=True)
    key_parser.set_defaults(handler=keygen)
    driver = commands.add_parser("download", help="Resolve a promoted release and verify a single streamed file")
    for option in ("origin", "key-id", "project", "platform", "version", "path"):
        driver.add_argument("--" + option, required=True)
    for option in ("work-dir", "report", "signing-public-key"):
        driver.add_argument("--" + option, required=True, type=Path)
    driver.add_argument("--environment", default="prod")
    driver.add_argument("--channel", default="stable")
    driver.add_argument("--signing-key-id", default="release-1")
    driver.add_argument("--timeout", type=float, default=60, help="Socket idle and barrier timeout in seconds")
    driver.add_argument("--sink", choices=("null", "file"), default="null")
    driver.add_argument("--file-out", type=Path, help="New path for file sink. Partial output can remain on failure.")
    ranges = driver.add_mutually_exclusive_group()
    ranges.add_argument("--offset", type=int, help="Request bytes=N- after fetching and hashing any preceding bytes")
    ranges.add_argument("--abort-after-bytes", type=int, help="Close initial response after N bytes and resume with bytes=N-")
    driver.add_argument("--barrier-dir", type=Path, help="Fresh shared directory, empty before the group launches")
    driver.add_argument("--barrier-group", help="Shared ID, required with --barrier-dir")
    driver.add_argument("--participants", type=int, help="Planned group size, required with --barrier-dir")
    driver.add_argument("--slot", type=int, help="Unique 1-based slot, required with --barrier-dir")
    driver.set_defaults(handler=run)
    summary = commands.add_parser("barrier-summary", help="Combine reports from one group and calculate start spread")
    summary.add_argument("--report", required=True, type=Path)
    summary.add_argument("--barrier-group", required=True)
    summary.add_argument("--participants", required=True, type=int)
    summary.add_argument("reports", nargs="*", type=Path)
    summary.set_defaults(handler=barrier_summary)
    args = parser.parse_args()
    try:
        return args.handler(args)
    except REPORTABLE as error:
        print(classify(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
