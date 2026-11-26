"""Owned published server crash/restart; signed catalogs only, no GUI/install."""
from ci_text import configure_console
configure_console()
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import time
from fixture_contract import verify, claim
from evidence_contract import atomic, provenance
from benchmark_signed_client import Device, Client


def read_high_water(root):
    authority = Path(root) / 'authority'
    head_path = authority / 'HEAD'
    if not head_path.exists():
        return None
    head = json.loads(head_path.read_bytes())
    head_bytes = json.dumps({**head, 'headSha256': ''}, separators=(',', ':')).encode('utf-8')
    if head['schema'] != 1 or hashlib.sha256(head_bytes).hexdigest() != head['headSha256']:
        raise ValueError('Invalid authority HEAD checksum')
    reference = head['files']['sequence']
    if not re.fullmatch(r'sequence/[0-9]{6,19}\.json', reference):
        raise ValueError('Invalid sequence reference')
    document = (authority / reference).read_bytes()
    if hashlib.sha256(document).hexdigest() != head['sha256']['sequence']:
        raise ValueError('Sequence generation digest differs from HEAD')
    sequence = json.loads(document)
    high_water = sequence['highWater']
    if sequence['schemaVersion'] != 1 or type(high_water) is not int or not 0 <= high_water < 2**63:
        raise ValueError('Invalid sequence high-water')
    # A jobs-only S2 authority retains the initial sequence while SQLite allocates sequences.
    if high_water == 0 and reference == 'sequence/000000.json' and (Path(root) / 'distribution.db').is_file():
        return None
    return high_water


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('server','config','output'):parser.add_argument('--'+key,type=Path,required=True)
    parser.add_argument('--client-id', default='bench', help='Existing fixture policy identity; registration grants no new access')
    parser.add_argument('--require-file-authority', action='store_true',
                        help='Require verification of a committed file sequence reservation')
    args=parser.parse_args(); server=args.server.resolve(strict=True); config=args.config.resolve(strict=True)
    settings=verify(config.parent,config,server); root=claim(args.output)
    with socket.socket() as reservation:reservation.bind(('127.0.0.1',0));port=reservation.getsockname()[1]
    origin=f'http://127.0.0.1:{port}'
    value={**settings,'publicUrl':origin,'listenUrl':origin}
    path=root/'server.json';path.write_text(json.dumps(value),encoding='utf-8')
    command=[str(server),'--config',str(path)]
    flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
    (root/'public.pem').write_bytes((config.parent/'public.pem').read_bytes())
    def register(file):
        result=subprocess.run([*command,'client-key','add','--client',args.client_id,'--public-key',str(file)],capture_output=True,timeout=30,creationflags=flags)
        if result.returncode:raise RuntimeError('Synthetic device registration failed')
    device=Device(root,register)
    report={'schemaVersion':1,**provenance({'server':server}),'complete':False,'success':False,'runs':[]}
    atomic(root/'summary.json',report)
    previous, old_client=0,None
    for iteration in range(2):
        with (root/f'server-private-{iteration}.log').open('w', encoding='utf-8') as log:
            owned=subprocess.Popen([*command,'serve'],stdout=log,stderr=log,creationflags=flags)
            client=None
            try:
                deadline=time.monotonic()+15
                while True:
                    try:
                        with socket.create_connection(('127.0.0.1',port),timeout=.1):break
                    except OSError:
                        if owned.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Server readiness failed')
                        time.sleep(.05)
                if old_client is not None:
                    status,valid=old_client.get('/api/v1/catalog?selectionPolicy=explicit-promotion-v1')
                    if status!=401 or valid:raise RuntimeError('Previous challenge survived restart')
                    old_client.close()
                client=Client(origin,device);issued=[]
                for _ in range(3):
                    status,valid=client.get('/api/v1/catalog?selectionPolicy=explicit-promotion-v1')
                    if status!=200 or not valid:raise RuntimeError('Catalog signature/binding verification failed')
                    issued.append(client.sequence)
                high_water=read_high_water(settings['root'])
                if args.require_file_authority and high_water is None:
                    raise RuntimeError('Published server did not reserve a file authority sequence')
                # Transitional published hosts still run the restart check. Their internal
                # high-water is unobserved, so require skipping the entire observed 64-number block.
                expected_floor = ((issued[-1] - 1) // 64 + 1) * 64
                if (issued[0]<=previous or issued != list(range(issued[0], issued[0]+len(issued))) or
                        high_water is not None and high_water != expected_floor):
                    raise RuntimeError('Sequence was reused or not durable')
                report['runs'].append({'issued':issued,'highWater':high_water,'oldChallengeRejected':iteration==1})
                previous=high_water if high_water is not None else expected_floor
                client.close();old_client=client
            finally:
                if client is not None:client.close()
                if owned.poll() is None:owned.kill()
                owned.wait(timeout=10)
                atomic(root/'summary.json',report)
    file_verified=all(run['highWater'] is not None for run in report['runs'])
    report.update(complete=True,success=True,fileAuthorityVerified=file_verified);atomic(root/'summary.json',report)
    print('PASS: restart skips reserved sequence block and invalidates old challenge'
          + ('; file high-water verified' if file_verified else '; file high-water unobserved'))


if __name__=='__main__':main()
