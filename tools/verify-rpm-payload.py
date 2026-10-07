"""Non-installing RPM contract check. No scriptlets or extracted programs run."""
import argparse
import hashlib
from pathlib import Path, PurePosixPath
import stat
import subprocess
from evidence_contract import atomic
from stream_hash import sha256_stream


def entries(data):
    offset, count = 0, 0
    seen = set()
    while offset < len(data):
        header = data[offset:offset+110]
        if len(header) != 110 or header[:6] not in (b'070701', b'070702'):
            raise ValueError('Invalid newc archive')
        fields = [int(header[6+i*8:14+i*8], 16) for i in range(13)]
        mode, links, size, namesize = fields[1], fields[4], fields[6], fields[11]
        offset += 110
        if namesize < 1 or namesize > 4096 or size > 256*1024*1024:
            raise ValueError('Archive entry exceeds cap')
        raw = data[offset:offset+namesize]
        if len(raw) != namesize or raw[-1:] != b'\0':
            raise ValueError('Invalid archive name')
        name = raw[:-1].decode('utf-8')
        offset = (offset + namesize + 3) & ~3
        body = data[offset:offset+size]
        if len(body) != size:
            raise ValueError('Truncated archive')
        offset = (offset + size + 3) & ~3
        if name == 'TRAILER!!!':
            return
        path = PurePosixPath(name)
        normalized = str(path)
        if path.is_absolute() or '..' in path.parts or normalized in seen:
            raise ValueError('Unsafe or duplicate archive path')
        seen.add(normalized)
        count += 1
        if count > 2000 or not (stat.S_ISDIR(mode) or stat.S_ISREG(mode)) or (stat.S_ISREG(mode) and links != 1):
            raise ValueError('Unsupported link/type/count in RPM')
        yield normalized, mode, body
    raise ValueError('Missing archive trailer')


def verify(rpm, payload, version, developer=False):
    def query(format):
        return subprocess.check_output(['rpm', '-qp', '--qf', format, str(rpm)], stderr=subprocess.DEVNULL, text=True)
    name='ue-dt-launcher-developer' if developer else 'ue-dt-launcher'
    if query('%{NAME}|%{VERSION}|%{RELEASE}|%{ARCH}') != f'{name}|{version}|1|x86_64':
        raise ValueError('Unexpected RPM identity')
    paths = {}
    for line in query('[%{FILENAMES}\t%{FILEMODES:octal}\t%{FILEUSERNAME}\t%{FILEGROUPNAME}\t%{FILEFLAGS}\n]').splitlines():
        name, mode, user, group, flags = line.split('\t')
        paths[name] = (int(mode, 8) & 0o7777, user, group, int(flags))
    config = paths.get('/etc/ue-dt-launcher/launcher.config.json')
    if developer:
        requirements=query('[%{REQUIRENAME} %{REQUIREFLAGS:depflags} %{REQUIREVERSION}\n]')
        if f'ue-dt-launcher = {version}-1' not in requirements:raise ValueError('Developer RPM base-version dependency is missing')
    else:
        verify_base_permissions(paths)
    process = subprocess.Popen(['rpm2cpio', str(rpm)], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    try:
        data = process.stdout.read(512*1024*1024+1)
        if len(data) > 512*1024*1024: raise ValueError('RPM payload exceeds extraction cap')
        if process.wait(timeout=60) != 0: raise ValueError('rpm2cpio failed')
    finally:
        if process.poll() is None:process.kill();process.wait()
    expected = {'opt/ue-dt-launcher/UeDtLauncher.Developer':payload/'UeDtLauncher.Developer'} if developer else {
        'opt/ue-dt-launcher/UeDtLauncher': payload/'UeDtLauncher',
        'opt/ue-dt-launcher/UeDtLauncher.Agent': payload/'UeDtLauncher.Agent',
        'etc/ue-dt-launcher/launcher.config.json': payload/'launcher.config.json',
        'usr/lib/systemd/system/ue-dt-launcher-agent.service': payload/'ue-dt-launcher-agent.service'}
    hashes={}
    for name,mode,body in entries(data):
        if stat.S_ISDIR(mode):continue
        if name not in expected or name in hashes:raise ValueError('Unapproved RPM file')
        with expected[name].open('rb') as source:original=sha256_stream(source)
        actual=hashlib.sha256(body).hexdigest()
        if actual!=original:raise ValueError('RPM payload hash mismatch')
        if name.startswith('opt/') and paths['/'+name][:3]!=(0o755,'root','root'):raise ValueError('Executable permission mismatch')
        hashes[name]=actual
    if hashes.keys()!=expected.keys():raise ValueError('RPM payload missing a required file')
    return hashes


def verify_base_permissions(paths):
    config=paths['/etc/ue-dt-launcher/launcher.config.json']
    if config[:3] != (0o640, 'root', 'uedt') or config[3] & 17 != 17:
        raise ValueError('Config permissions/noreplace changed')
    for name, owner in [('etc/ue-dt-launcher/credentials', 'root'), ('var/lib/ue-dt-launcher/state', 'uedt'), ('var/log/ue-dt-launcher', 'uedt')]:
        if paths['/'+name][:3] != (0o750, owner, 'uedt'):
            raise ValueError('Managed directory ownership changed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rpm', type=Path, required=True)
    parser.add_argument('--payload', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--developer',action='store_true')
    args = parser.parse_args()
    hashes = verify(args.rpm, args.payload, args.version,args.developer)
    head = subprocess.run(['git', 'rev-parse', 'HEAD'], capture_output=True, text=True)
    with args.rpm.open('rb') as stream:
        digest = sha256_stream(stream)
    atomic(args.output, {'schemaVersion': 1, 'version': args.version, 'edition':'Developer' if args.developer else 'General', 'classification': 'UNSIGNED-DEV',
        'source': head.stdout.strip() if head.returncode == 0 else 'unavailable',
        'rpmFile': args.rpm.name, 'rpmSha256': digest,
        'payloadSha256': hashes, 'nonInstallingVerification': True})
    print('PASS: exact RPM, safe archive, payload hashes, config preservation and ownership')


if __name__ == '__main__':
    main()
