"""Kill only an owned test harness at deterministic production-persistence boundaries."""
import argparse, json, subprocess, tempfile
from pathlib import Path
p=argparse.ArgumentParser(); p.add_argument('--harness',required=True); p.add_argument('--launcher',required=True); a=p.parse_args()
root=Path(tempfile.mkdtemp(prefix='uedt-runtime-crash-')); results=[]
for operation in ('begin','attach','started','complete','recover','service-starting','service-healthy','service-failure','service-confirm'):
    for boundary in ('write','flush','replace','ack'):
        case=root/(operation+'-'+boundary)
        proc=subprocess.Popen([str(Path(a.harness).resolve()),str(case),operation,boundary],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
        try:
            line=proc.stdout.readline(); assert line,line+proc.stderr.read()
            event=json.loads(line); assert event['ready']
            proc.kill(); proc.wait(timeout=5)
            record=json.loads(Path(event['path']).read_text())
            if operation.startswith('service-'):
                if operation=='service-starting': expected=1 if boundary=='ack' else 0
                elif operation=='service-healthy': expected=0 if boundary=='ack' else 1
                elif operation=='service-failure': expected=2 if boundary=='ack' else 1
                else: expected=0 if boundary=='ack' else 2
                assert record['phase']==expected,(operation,boundary,record)
            else:
                check=subprocess.run([str(Path(a.launcher).resolve()),'runtime','inspect','--config',event['config']],capture_output=True,encoding='utf-8',timeout=15)
                assert check.returncode==0,check.stderr
                state=json.loads(check.stdout)['state']
                if operation=='begin': expected=1 if boundary=='ack' else 0
                elif operation=='attach': expected=1
                elif operation=='started': expected=3 if boundary=='ack' else 1
                elif operation=='complete': expected=0 if boundary=='ack' else 3
                else: expected=0
                assert state==expected,(operation,boundary,state)
            results.append(dict(operation=operation,boundary=boundary,passed=True))
        finally:
            if proc.poll() is None: proc.kill(); proc.wait(timeout=5)
(root/'summary.json').write_text(json.dumps(results,indent=2))
print('PASS: 36 controlled process-termination boundaries (no hardware power-loss claim): '+str(root))
