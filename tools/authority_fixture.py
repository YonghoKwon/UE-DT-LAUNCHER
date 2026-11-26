"""Verify the stopped synthetic authority used by benchmark and promotion tools."""
import hashlib
import json
from pathlib import Path
import re

from fixture_contract import inside


def bounded(path, size):
    with path.open('rb') as stream:
        data = stream.read(size + 1)
    if len(data) > size:
        raise ValueError('Authority document exceeds size limit')
    return data


def checked_document(raw, field):
    document = json.loads(raw)
    prefix, separator, tail = raw.rpartition(f',"{field}":"'.encode())
    digest = document[field]
    if (not separator or not re.fullmatch(r'[0-9a-f]{64}', digest) or
            tail != digest.encode() + b'"}' or
            hashlib.sha256(prefix + separator + b'"}').hexdigest() != digest):
        raise ValueError(f'Authority document has an invalid {field}')
    return document


def read_authority_catalog(root):
    authority = root / 'authority'
    head = checked_document(bounded(inside(authority, authority / 'HEAD'), 64 * 1024), 'headSha256')
    generation = head['generation']
    catalog_reference = head['files']['catalog']
    if (head['schema'] != 1 or type(generation) is not int or generation < 0 or
            not re.fullmatch(r'catalog/[0-9]{6,19}\.json', catalog_reference) or
            int(Path(catalog_reference).stem) > generation or
            head['files']['events'] != f'events/{generation:06}.json'):
        raise ValueError('Invalid catalog or audit reference in HEAD')
    catalog_generation = int(Path(catalog_reference).stem)
    if catalog_reference != f'catalog/{catalog_generation:06}.json':
        raise ValueError('Catalog generation path is not canonical')
    catalog_raw = bounded(inside(authority, authority / catalog_reference), 64 * 1024 * 1024)
    if hashlib.sha256(catalog_raw).hexdigest() != head['sha256']['catalog']:
        raise ValueError('Catalog digest differs from HEAD')
    return head, catalog_reference, json.loads(catalog_raw)
