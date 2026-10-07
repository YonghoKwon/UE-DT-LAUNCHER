"""Prepare a private operator plan only. Never install, restart or provision here."""
import argparse
import json
from pathlib import Path
import shlex
from fixture_contract import claim, unlinked, digest
from evidence_contract import atomic


def plan(platform, package, next_package=None):
    quote = (lambda value: '"' + str(value).replace('"', '""') + '"') if platform == 'windows' else shlex.quote
    current = next_package or package
    if platform == 'windows':
        steps = [
            ('preflight', 'read-only', f'Get-AuthenticodeSignature {quote(package)} | Format-List Status,SignerCertificate,TimeStamperCertificate', 'Approved signer/timestamp; UNSIGNED-DEV is not a production signature'),
            ('install', 'guest-apply', f'msiexec /i {quote(package)} /qn /norestart /L*v install-private.log', 'Exit 0 or 3010; no unapproved reboot; correct files/service installed'),
            ('service', 'read-only', 'Get-Service UeDtLauncherAgent; sc.exe qc UeDtLauncherAgent', 'Automatic start and LocalService; protected credentials are not shared with GUI'),
            ('connect', 'read-only', 'UeDtLauncher.exe agent status; UeDtLauncher.exe doctor --config "<PROTECTED_CONFIG>" --online --format text', 'Exact allowed target; online auth, signatures, IPC and actual service identity'),
            ('upgrade', 'guest-apply' if next_package else 'waiting-artifact', f'msiexec /i {quote(next_package or "<NEXT_SIGNED_MSI>")} /qn /norestart /L*v upgrade-private.log', 'Next version; config/app/state/data/backup preserved'),
            ('repair', 'guest-apply', f'msiexec /fomus {quote(current)} /qn /norestart /L*v repair-private.log', 'Installer files restored; protected content/credentials unchanged'),
            ('reboot', 'manual-confirmation', 'No automatic reboot command is generated', 'After explicit guest-owner approval: service starts and installed launcher connects'),
            ('uninstall', 'guest-apply', f'msiexec /x {quote(current)} /qn /norestart /L*v uninstall-private.log', 'Launcher/service removed; preservation policy for config, content, state, data and backup verified')]
    else:
        steps = [
            ('preflight', 'read-only', f'cat /etc/os-release; rpm --checksig --verbose {quote(package)}', 'Actual RHEL 8 guest and approved package signer; Ubuntu/WSL is not RHEL acceptance'),
            ('install', 'guest-apply', f'sudo rpm -Uvh {quote(package)}', 'Exact package; credentials root:uedt0750, config root:uedt0640'),
            ('service', 'guest-apply', 'sudo systemctl enable --now ue-dt-launcher-agent.service; systemctl status ue-dt-launcher-agent.service', 'Actual uedt identity, automatic start, no unapproved policy bypass'),
            ('connect', 'read-only', '/opt/ue-dt-launcher/UeDtLauncher agent status; /opt/ue-dt-launcher/UeDtLauncher doctor --config /etc/ue-dt-launcher/launcher.config.json --online --format text', 'IPC/online verification; actual account credential access and unrelated-account denial'),
            ('upgrade', 'guest-apply' if next_package else 'waiting-artifact', f'sudo rpm -Uvh {quote(next_package or "<NEXT_SIGNED_RPM>")}', 'Version updated; config(noreplace), app/state/data/backup preserved'),
            ('repair', 'guest-apply', f'sudo rpm --replacepkgs -Uvh {quote(current)}', 'Exact package reinstalled; config/content/state/data/backup preserved'),
            ('verify', 'read-only', 'rpm -V ue-dt-launcher; getenforce; sudo ausearch -m AVC -ts recent', 'SELinux/ownership evidence; do not disable enforcing to make tests pass'),
            ('reboot', 'manual-confirmation', 'No automatic reboot command is generated', 'After explicit guest-owner approval: service auto-start and exact target connect'),
            ('uninstall', 'guest-apply', 'sudo rpm -e ue-dt-launcher', 'Package/service removed; preserved config/content/state/data/backup checked')]
    return [{'name': name, 'kind': kind, 'command': command, 'expected': expected, 'status': 'not-run'}
            for name, kind, command, expected in steps]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--platform', choices=['windows', 'rhel8'], required=True)
    parser.add_argument('--package', type=Path, required=True)
    parser.add_argument('--next-package', type=Path)
    parser.add_argument('--output', type=Path, required=True, help='New private plan directory')
    args = parser.parse_args()
    packages = [unlinked(path).resolve(strict=True) for path in (args.package, args.next_package) if path]
    suffix = '.msi' if args.platform == 'windows' else '.rpm'
    if any(not path.is_file() or path.suffix.lower() != suffix for path in packages):
        raise ValueError('Expected exact package files for the selected platform')
    root = claim(args.output)
    rows = plan(args.platform, str(packages[0]), str(packages[1]) if len(packages) > 1 else None)
    result = {'schemaVersion': 1, 'platform': args.platform, 'execution': 'operator-plan-only',
        'requirements': ['Disposable approved guest; never current development host',
            'Guest snapshot and separate backup before applying installer actions',
            'Administrator-approved signer/credential/proxy/data policies; no guessed production values',
            'Explicit authorization for each install/upgrade/repair/uninstall/reboot',
            'Hash/ACL inventory before and after; runtime observations separate from payload/data'],
        'packages': [{'name': path.name, 'sha256': digest(path)} for path in packages], 'checks': rows}
    atomic(root/'plan.json', result)
    lines = ['# Company acceptance operator plan', '', 'All checks are **not run**. Commands are not executed by this tool.', '',
        *['- ' + value for value in result['requirements']], '', '| Check | Kind | Command | Expected |', '|---|---|---|---|']
    lines += [f"| {row['name']} | {row['kind']} | `{row['command']}` | {row['expected']} |" for row in rows]
    (root/'plan.md').write_text('\n'.join(lines)+'\n', encoding='utf-8')
    print('PASS: private operator plan prepared; no installation, service, account or reboot performed')


if __name__ == '__main__':
    main()
