"""Read-only source-copy comparison; no Git state or generated files are changed."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from fixture_contract import unlinked, digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--copy', type=Path, required=True)
    args = parser.parse_args()
    source, copied = unlinked(args.source), unlinked(args.copy)
    scope = ['src', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config']
    names = subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard',*scope],cwd=source,text=True).splitlines()
    inventory = {}
    for name in names:
        original, replica = unlinked(source/name), unlinked(copied/name)
        if not original.is_relative_to(source) or not replica.is_relative_to(copied) or digest(original) != digest(replica):
            raise ValueError('Product source copy mismatch')
        inventory[name] = digest(original)
    record = {'schemaVersion':1,'source':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip(),
        'productSourceHash':hashlib.sha256(json.dumps(inventory,sort_keys=True).encode()).hexdigest(),
        'comparedFiles':len(inventory),'matched':True,'scope':'product-source-copy-at-capture-not-build-attestation'}
    print(json.dumps(record))


if __name__ == '__main__':
    main()
