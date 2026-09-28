import contextlib, importlib.util, io, json, os, runpy, subprocess, sys, tempfile, unittest
from pathlib import Path
from unittest.mock import patch, Mock
from gui_fixture_evidence import inside, sha256, verify_files, snapshot_preferences, restore_preferences, preference_hash, verify_cohort, validate_origin, hold_fixture, FixtureHarnessLock, copy_server_support

TOOLS=Path(__file__).resolve().parent

def make_fixture(root,mode='portable'):
    root.mkdir(parents=True,exist_ok=True);(root/'control').mkdir()
    kinds=['launcher','server','synthetic']+(['agent'] if mode=='managed' else [])
    binaries={}
    for kind in kinds:
        path=root/(kind+'.exe');path.write_text(kind)
        binaries[kind]={'path':str(path),'sha256':sha256(path)}
    fixture={'schemaVersion':2,'id':root.name,'deploymentMode':mode,'platform':'windows-x64','sourceHead':'test','binaries':binaries,'pendingJobs':{'2.0.0':'pending-v2'}}
    (root/'fixture.json').write_text(json.dumps(fixture))
    (root/'summary.json').write_text(json.dumps({'gui_prepared_empty':True}))
    (root/'server.json').write_text(json.dumps({'root':str(root/'server'),'publicUrl':'http://127.0.0.1:54321','listenUrl':'http://127.0.0.1:54321','policyPath':str(root/'policy.json'),'signingKeyPath':str(root/'release.pem')}))
    (root/'test-environment.json').write_text(json.dumps({'UE_DT_AGENT_DATA_ROOT':str(root/'private'),'UE_DT_AGENT_ENDPOINT':'fixture-only'}))
    policy={'clients':[{'id':'pc-test','addresses':['127.0.0.0/8'],'grants':[{'projectId':'demo'}]}]}
    for path in (root/'policy.json',root/'control'/'original-policy.json'):path.write_text(json.dumps(policy))
    for path in (root/'public.pem',root/'control'/'original-public.pem'):path.write_text('original-public')
    (root/'device-public.json').write_text(json.dumps({'publicKeyPem':'different-public'}))
    return fixture

def control(root,action):
    with patch.object(sys,'argv',['gui-fixture-control.py','--root',str(root),action]),contextlib.redirect_stdout(io.StringIO()):
        runpy.run_path(str(TOOLS/'gui-fixture-control.py'),run_name='__main__')

class EvidenceTests(unittest.TestCase):
    def test_long_labels_are_deterministic_bounded_and_opt_in(self):
        spec=importlib.util.spec_from_file_location('intranet_long_labels',TOOLS/'test-intranet-auth.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        self.assertEqual([],module.gui_metadata_arguments(False,'1.0.0'))
        arguments=module.gui_metadata_arguments(True,'1.0.0')
        self.assertEqual(arguments,module.gui_metadata_arguments(True,'1.0.0'))
        self.assertEqual('--display-name',arguments[0]);self.assertTrue(40<len(arguments[1])<256)
        self.assertEqual('--notes',arguments[2]);self.assertTrue(200<len(arguments[3])<2048)
        self.assertIn('포스코DX',arguments[1]);self.assertIn('1.0.0',arguments[3])
        self.assertNotEqual(arguments[3],module.gui_metadata_arguments(True,'2.0.0')[3])
        self.assertEqual(['demo'],[g['projectId'] for g in module.fixture_access_policy()['clients'][0]['grants']])
        grants=module.fixture_access_policy(True)['clients'][0]['grants']
        self.assertEqual(['demo','demo-secondary'],[g['projectId'] for g in grants])
        self.assertEqual(['1.0.0'],grants[1]['versions'])

    def test_long_labels_require_gui_before_any_file_or_process_work(self):
        spec=importlib.util.spec_from_file_location('intranet_label_guard',TOOLS/'test-intranet-auth.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with patch.object(sys,'argv',['test-intranet-auth.py','--launcher','unused','--server','unused','--gui-long-labels']),patch.object(module.subprocess,'run') as run,contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as result:module.main()
            self.assertEqual(2,result.exception.code);run.assert_not_called()

    def test_preference_restore_refuses_intervening_change(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); path=root/'ui.json'; path.write_text('original')
            with patch('gui_fixture_evidence.preference_path',return_value=path):
                snapshot_preferences(root); path.write_text('owned'); owned=preference_hash()
                path.write_text('user-change')
                with self.assertRaises(ValueError): restore_preferences(root,owned)
                self.assertEqual('user-change',path.read_text())
                path.write_text('owned'); restore_preferences(root,owned)
                self.assertEqual('original',path.read_text())

    def test_paths_and_hashes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); (root/'file').write_bytes(b'good')
            manifest=root/'manifest.json'
            manifest.write_text(json.dumps({'files':[{'path':'file','size':4,'sha256':sha256(root/'file')}]}))
            self.assertEqual(1,verify_files(root,manifest))
            with self.assertRaises(ValueError): inside(root,'../outside')
            (root/'file').write_bytes(b'bad!')
            with self.assertRaises(ValueError): verify_files(root,manifest)

    def test_cohort_rejects_changed_and_external_binaries(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'fixture';fixture=make_fixture(root)
            self.assertEqual('portable',verify_cohort(root)['deploymentMode'])
            (root/'launcher.exe').write_text('changed')
            with self.assertRaises(ValueError):verify_cohort(root)
            external=Path(temporary)/'external.exe';external.write_text('external')
            fixture['binaries']['launcher']={'path':str(external),'sha256':sha256(external)}
            (root/'fixture.json').write_text(json.dumps(fixture))
            with self.assertRaises(ValueError):verify_cohort(root)

    def test_origin_rejects_remote_or_embedded_credentials(self):
        for origin in ('https://127.0.0.1:80','http://company.example:80','http://127.0.0.1:80@company.example','http://127.0.0.1:80/path'):
            with self.assertRaises(ValueError):validate_origin(origin)
        self.assertEqual('http://127.0.0.1:1234',validate_origin('http://127.0.0.1:1234/'))

    def test_server_support_is_allowlisted_and_hash_checked(self):
        with tempfile.TemporaryDirectory() as temporary:
            base=Path(temporary);root=base/'fixture';fixture=make_fixture(root)
            publish=base/'published';publish.mkdir();source=publish/'server.exe';source.write_text('server')
            for name in ('e_sqlite3.dll','server.deps.json','server.runtimeconfig.json','server.config.json','token.keycred','server.log'):
                (publish/name).write_text(name)
            support=copy_server_support(source,root/'server.exe')
            self.assertEqual({'server/e_sqlite3.dll','server/server.deps.json','server/server.runtimeconfig.json'},set(support))
            self.assertFalse((root/'token.keycred').exists());self.assertFalse((root/'server.config.json').exists());self.assertFalse((root/'server.log').exists())
            fixture['supportFiles']=support;(root/'fixture.json').write_text(json.dumps(fixture))
            verify_cohort(root)
            (root/'e_sqlite3.dll').write_text('altered')
            with self.assertRaises(ValueError):verify_cohort(root)
            (root/'e_sqlite3.dll').unlink()
            with self.assertRaises(FileNotFoundError):verify_cohort(root)

    def test_fixture_has_one_harness_owner(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            with FixtureHarnessLock(root):
                with self.assertRaises(RuntimeError):
                    with FixtureHarnessLock(root):pass
            with FixtureHarnessLock(root):pass
            self.assertTrue((root/'control'/'harness.lock').exists())

    def test_owned_server_downtime_and_restart_do_not_create_an_agent(self):
        class OwnedProcess:
            def __init__(self):self.running=True;self.stops=0
            def poll(self):return None if self.running else 0
            def terminate(self):self.running=False;self.stops+=1
            def wait(self,timeout):return 0
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'fixture';fixture=make_fixture(root)
            first=OwnedProcess();second=OwnedProcess();step=0
            (root/'control'/'stop-server').touch()
            def tick(_):
                nonlocal step
                if step==0:
                    self.assertEqual(1,first.stops);(root/'control'/'start-server').touch()
                else:(root/'control'/'stop-all').touch()
                step+=1
            with patch('gui_fixture_evidence.subprocess.Popen',return_value=second) as popen,patch('gui_fixture_evidence.wait_server_ready') as ready,patch('gui_fixture_evidence.wait_agent_ready',side_effect=AssertionError('No Agent')),patch('gui_fixture_evidence.time.sleep',side_effect=tick):
                hold_fixture(root,fixture,{},first,None,io.StringIO(),None)
            self.assertEqual(1,popen.call_count);ready.assert_called_once()
            self.assertEqual(1,second.stops);self.assertTrue((root/'control'/'server-ready').exists())

    def test_portable_status_never_contacts_an_agent(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'fixture';make_fixture(root)
            with patch('gui_fixture_evidence.agent_status',side_effect=AssertionError('Portable cannot use IPC')):
                control(root,'status')
            with self.assertRaises(ValueError):control(root,'start-agent')
            self.assertFalse((root/'control'/'start-agent').exists())

    def test_policy_and_trust_errors_are_fixture_scoped_and_reversible(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'fixture';make_fixture(root)
            control(root,'error-403');self.assertEqual(['192.0.2.0/24'],json.loads((root/'policy.json').read_text())['clients'][0]['addresses'])
            control(root,'error-empty');self.assertEqual([],json.loads((root/'policy.json').read_text())['clients'][0]['grants'])
            control(root,'policy-reset');self.assertEqual(json.loads((root/'control'/'original-policy.json').read_text()),json.loads((root/'policy.json').read_text()))
            control(root,'error-trust');self.assertEqual('different-public',(root/'public.pem').read_text())
            control(root,'trust-reset');self.assertEqual('original-public',(root/'public.pem').read_text())
            self.assertEqual(5,len((root/'control'/'events.jsonl').read_text().splitlines()))

    def test_resume_rejects_a_different_server_before_process_start(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'fixture';make_fixture(root)
            with patch.object(sys,'argv',['resume-gui-fixture.py','--root',str(root),'--server',str(root/'different.exe')]),patch('subprocess.Popen') as popen:
                with self.assertRaises(ValueError):runpy.run_path(str(TOOLS/'resume-gui-fixture.py'),run_name='__main__')
                popen.assert_not_called()

    def test_portable_prep_uses_portable_storage_and_never_runs_agent_or_installs(self):
        spec=importlib.util.spec_from_file_location('intranet_fixture',TOOLS/'test-intranet-auth.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as temporary:
            base=Path(temporary);root=base/'fixture'
            for name in ('UeDtLauncher.exe','Distribution.exe','Synthetic.exe'):(base/name).write_text(name)
            commands=[]
            def run(command,**kwargs):
                commands.append(command);verb=command[1];code=0;output='OK'
                def option(name):return Path(command[command.index(name)+1])
                if verb=='generate-signing-key':
                    option('--private-key').write_text('private');option('--public-key').write_text('public')
                elif verb=='credential' and command[2]=='keygen':
                    self.assertEqual('portable',command[command.index('--storage')+1])
                    self.assertTrue(kwargs['env']['UE_DT_AGENT_DATA_ROOT'].endswith('portable-private'))
                    option('--public-out').write_text(json.dumps({'publicKeyPem':'device-public'}))
                elif verb=='ingest':output=json.dumps({'state':'pending','id':'job-'+Path(command[2]).name})
                elif verb=='sample-config':
                    path=option('--output')
                    if path.exists():code=1
                    else:path.write_text(json.dumps({'security':{'credentialName':'device'},'deploymentMode':'portable'}))
                return subprocess.CompletedProcess(command,code,output,'')
            process=Mock();process.poll.return_value=None
            def terminated():process.poll.return_value=0
            process.terminate.side_effect=terminated
            arguments=['test-intranet-auth.py','--launcher',str(base/'UeDtLauncher.exe'),'--server',str(base/'Distribution.exe'),'--prepare-gui',str(base/'Synthetic.exe'),'--gui-mode','portable','--root',str(root)]
            import urllib.error
            with patch.object(sys,'argv',arguments),patch.object(module.subprocess,'run',side_effect=run),patch.object(module.subprocess,'Popen',return_value=process) as popen,patch.object(module.subprocess,'check_output',side_effect=lambda cmd,**kw: ('test-head\n' if 'rev-parse' in cmd else '') if kw.get('text') else b''),patch.object(module.urllib.request,'urlopen',side_effect=urllib.error.HTTPError('loopback',401,'unauthorized',{},None)),patch.object(module,'hold_fixture') as hold,patch.object(module,'snapshot_preferences'),contextlib.redirect_stdout(io.StringIO()):
                module.main()
            self.assertEqual(1,popen.call_count)
            self.assertTrue(all(command[1] not in ('agent','doctor','run') for command in commands))
            approvals=[command for command in commands if command[1]=='approve']
            self.assertEqual(['job-1.0.0'],[command[2] for command in approvals])
            value=json.loads((root/'client'/'general.json').read_text())
            self.assertEqual('portable',value['deploymentMode']);self.assertEqual('device',value['security']['credentialName'])
            self.assertNotIn('agent',verify_cohort(root)['binaries'])
            self.assertFalse((root/'client'/'apps').exists());hold.assert_called_once()

if __name__=='__main__': unittest.main()
