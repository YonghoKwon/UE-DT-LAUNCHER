"""Bounded streaming SHA-256, including Python 3.10 Linux operator tools."""
import hashlib


def sha256_stream(stream):
    digest = hashlib.sha256()
    while True:
        block = stream.read(1024 * 1024)
        if not block:
            return digest.hexdigest()
        digest.update(block)
