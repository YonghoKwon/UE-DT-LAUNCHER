"""Owned standalone payload proof; not GUI installation or runtime-supervision evidence."""
import argparse,json,os,shutil,subprocess,tempfile,time
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument('--binary',required=True);a=p.parse_args()
with tempfile.TemporaryDirectory(prefix='uedt-synthetic-life-') as temporary:
    root=Path(temporary);binary=root/Path(a.binary).name;shutil.copy2(a.binary,binary)
    (root/'version.txt').write_text('1.0.0');control=root/'control';control.mkdir()
    attempt='a'*32
    def run(arguments):
        return subprocess.Popen([str(binary),'--control-root',str(control),'--smoke','--child',attempt,*arguments],creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    (control/('release-'+attempt)).touch()
    with run(['--watchdog-ms','0']) as process:assert process.wait(timeout=10)==0
    assert json.loads((control/(attempt+'.ended.json')).read_text())['reason']=='watchdog'
    (control/(attempt+'.ended.json')).unlink()
    with run([]) as process:assert process.wait(timeout=10)==0
    assert json.loads((control/(attempt+'.ended.json')).read_text())['reason']=='requested'
print('PASS: exact watchdog boundary is not mislabeled by a late release; requested exit remains supported')
