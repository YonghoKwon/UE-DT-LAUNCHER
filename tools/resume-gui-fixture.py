"""Resume only the recorded binary cohort of a stopped, isolated GUI fixture."""
import argparse, json, os, subprocess
import urllib.parse
from pathlib import Path
from gui_fixture_evidence import inside, verify_cohort, fixture_environment, fixture_mode, wait_server_ready, wait_agent_ready, hold_fixture, FixtureHarnessLock

p=argparse.ArgumentParser(); p.add_argument('--root',required=True)
p.add_argument('--server',help='Legacy assertion only; must equal the recorded server path')
a=p.parse_args();root=Path(a.root).resolve()
assert json.loads(inside(root,'summary.json').read_text())['gui_prepared_empty']
fixture=verify_cohort(root)
server_path=Path(fixture['binaries']['server']['path']).resolve()
if a.server and Path(a.server).resolve()!=server_path: raise ValueError('Cannot mix a different server into the recorded cohort')
ownership=FixtureHarnessLock(root);ownership.__enter__()
control=inside(root,'control');control.mkdir(exist_ok=True)
for name in ('stop-all','stop-agent','start-agent','agent-stopped','agent-ready','stop-server','start-server','server-stopped','server-ready'):
    (control/name).unlink(missing_ok=True)
env=fixture_environment(root,fixture)
flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
worker=None;server=None;download_proxy=None
with inside(root,'resume-server.log').open('a',encoding='utf-8') as sl, inside(root,'resume-agent.log').open('a',encoding='utf-8') as al:
    try:
        if fixture.get('guiDownloadProof'):
            from gui_download_proxy import DownloadProxy
            settings=json.loads(inside(root,'server.json').read_text())
            download_proxy=DownloadProxy(root,urllib.parse.urlsplit(settings['publicUrl']).port,urllib.parse.urlsplit(settings['listenUrl']).port)
            download_proxy.start()
        server=subprocess.Popen([str(server_path),'serve','--config',str(inside(root,'server.json'))],env=env,stdout=sl,stderr=sl,creationflags=flags)
        wait_server_ready(server,json.loads(inside(root,'server.json').read_text())['publicUrl'])
        if fixture_mode(fixture)=='managed':
            worker=subprocess.Popen([fixture['binaries']['agent']['path']],env=env,stdout=al,stderr=al,creationflags=flags)
            wait_agent_ready(worker,env['UE_DT_AGENT_ENDPOINT'])
        print('READY: '+str(root)+' mode='+fixture_mode(fixture),flush=True)
        hold_fixture(root,fixture,env,server,worker,sl,al)
    finally:
        if download_proxy is not None:download_proxy.close()
        for process in (worker,server):
            if process is not None and process.poll() is None: process.terminate();process.wait(timeout=10)
        ownership.close()
