"""Generate deterministic incompressible files in the isolated benchmark volume."""

import hashlib
import json
import random
import sys
from pathlib import Path

CHUNK = 1024 * 1024
SIZES = {"20gb": 20_000_000_000, "1gb": 1_000_000_000}


def generate(root, name):
    size = SIZES[name]
    seed = 20261127 + size
    rng = random.Random(seed)
    digest = hashlib.sha256()
    remaining = size
    with (root / f"{name}.bin").open("xb") as stream:
        while remaining:
            block = rng.randbytes(min(CHUNK, remaining))
            stream.write(block)
            digest.update(block)
            remaining -= len(block)
    return {"bytes": size, "seed": seed, "sha256": digest.hexdigest()}


if __name__ == "__main__":
    root = Path(sys.argv[1])
    print(json.dumps({name: generate(root, name) for name in SIZES}))
