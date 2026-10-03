"""Publish a frozen product cohort and record source/binary provenance (no installer)."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import shutil
from gui_fixture_evidence import sha256

REPO = Path(__file__).resolve().parents[1]


def source_inventory():
    names = subprocess.check_output(['git', 'ls-files', '--cached','--others','--exclude-standard','src', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config'], cwd=REPO, text=True).splitlines()
    return {n: sha256(REPO/n) for n in names}


def main():
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('--output', required=True)
    p.add_argument('--rid', choices=['win-x64', 'linux-x64'], default='win-x64'); args = p.parse_args()
    root = Path(args.output).resolve()
    if root.exists(): raise ValueError('Use a new publication directory')
    before = source_inventory(); head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO, text=True).strip()
    dirty = bool(subprocess.check_output(['git', 'diff', 'HEAD', '--', 'src', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config'], cwd=REPO))
    dirty |= bool(subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard', '--', 'src', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config'], cwd=REPO))
    root.mkdir(parents=True)
    suffix = '.exe' if args.rid == 'win-x64' else ''
    binaries = {}
    for role, project in [('launcher', 'UeDtLauncher'), ('developer','UeDtLauncher'), ('agent', 'UeDtLauncher.Agent'), ('server', 'UeDtLauncher.DistributionServer')]:
        output = root/role
        result = subprocess.run(['dotnet', 'publish', str(REPO/'src'/project/(project+'.csproj')), '-c', 'Release', '-r', args.rid,
            '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:LauncherEdition='+('Developer' if role=='developer' else 'General'), '-o', str(output)], cwd=REPO,
            capture_output=True, text=True, encoding='utf-8', errors='replace')
        (root/(role+'-publish.log')).write_text(result.stdout+result.stderr, encoding='utf-8')
        if result.returncode: raise RuntimeError('Publication failed; inspect private publication log')
        executable = output/(('UeDtLauncher.Developer' if role=='developer' else project)+suffix)
        binaries[role] = {'path': str(executable), 'sha256': sha256(executable)}
    shutil.copy2(binaries['launcher']['path'],root/'developer'/('UeDtLauncher'+suffix))
    if before != source_inventory(): raise ValueError('Product source changed during publication; cohort is invalid')
    record = {'schemaVersion': 1, 'sourceHead': head, 'productSourceDirty': dirty,
        'productSourceHash': hashlib.sha256(json.dumps(before, sort_keys=True).encode()).hexdigest(), 'rid': args.rid, 'binaries': binaries}
    (root/'cohort.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
    print(json.dumps(record))


if __name__ == '__main__': main()
