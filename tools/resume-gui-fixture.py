"""Resume a stopped, isolated GUI fixture without recreating releases or client state."""
import argparse, json, os, subprocess, time
from pathlib import Path
p=argparse.ArgumentParser(); p.add_argument('--root',required=True); p.add_argument('--server',required=True); a=p.parse_args()
root=Path(a.root).resolve(); assert json.loads((root/'summary.json').read_text())['gui_prepared_empty']
control=root/'control'; control.mkdir(exist_ok=True)
for name in ('stop-all','stop-agent','start-agent','agent-stopped'): (control/name).unlink(missing_ok=True)
env=dict(os.environ,**json.loads((root/'test-environment.json').read_text()))
flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
agent=root/'client'/'agent'/('UeDtLauncher.Agent.exe' if os.name=='nt' else 'UeDtLauncher.Agent')
with (root/'resume-server.log').open('a',encoding='utf-8') as sl, (root/'resume-agent.log').open('a',encoding='utf-8') as al:
    server=subprocess.Popen([str(Path(a.server).resolve()),'serve','--config',str(root/'server.json')],env=env,stdout=sl,stderr=sl,creationflags=flags)
    worker=subprocess.Popen([str(agent)],env=env,stdout=al,stderr=al,creationflags=flags)
    try:
        time.sleep(1)
        assert server.poll() is None and worker.poll() is None,'Fixture process did not start'
        print('READY: '+str(root),flush=True)
        while server.poll() is None and not (control/'stop-all').exists():
            if (control/'stop-agent').exists() and worker.poll() is None:
                worker.terminate(); worker.wait(timeout=10); (control/'agent-stopped').touch()
            if (control/'start-agent').exists() and worker.poll() is not None:
                (control/'stop-agent').unlink(missing_ok=True); (control/'start-agent').unlink()
                worker=subprocess.Popen([str(agent)],env=env,stdout=al,stderr=al,creationflags=flags)
            time.sleep(.1)
    finally:
        for process in (worker,server):
            if process.poll() is None: process.terminate(); process.wait(timeout=10)
