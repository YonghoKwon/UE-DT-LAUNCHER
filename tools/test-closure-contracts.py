import contextlib,hashlib,json,os,tempfile,unittest,uuid
from pathlib import Path
from unittest.mock import patch
from cohort_contract import read_publication,validate_inputs,fixture_provenance,verify_fixture_publication
from gui_fixture_evidence import sha256
from restore_file_witness import arm,observe
from acceptance_ledger import create,record_case,summary,aggregate,CHECKS,load_proof

def publication(root):
    root.mkdir();binaries={}
    for role,edition in [('launcher','General'),('developer','Developer'),('agent',None),('server',None)]:
        path=root/(role+'.bin');path.write_text(role);binaries[role]={'path':str(path),'sha256':sha256(path),'edition':edition}
    synthetic=root/'synthetic.bin';synthetic.write_text('synthetic')
    sidecar=root/'libe_sqlite3.so';sidecar.write_text('sqlite')
    value={'schemaVersion':2,'success':True,'sourceHead':'a'*40,'productSourceHash':'b'*64,'rid':'win-x64','binaries':binaries,
           'fixtureAssets':{'synthetic':{'path':str(synthetic),'sha256':sha256(synthetic),'edition':None}},
           'supportFiles':{'server/libe_sqlite3.so':{'path':str(sidecar),'sha256':sha256(sidecar)}}}
    path=root/'cohort.json';path.write_text(json.dumps(value));return path,value

class CohortTests(unittest.TestCase):
    def test_membership_rejects_mixed_publication_and_changed_binary(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);path,value=publication(root/'one');other,second=publication(root/'two')
            validate_inputs(path,{'launcher':value['binaries']['launcher']['path']})
            with self.assertRaises(ValueError):validate_inputs(path,{'launcher':second['binaries']['launcher']['path']})
            Path(value['binaries']['launcher']['path']).write_text('changed')
            with self.assertRaises(ValueError):read_publication(path)
    def test_missing_sidecar_wrong_edition_and_failed_publication_are_rejected(self):
        for failure in ('sidecar','edition','failed'):
            with self.subTest(failure=failure),tempfile.TemporaryDirectory() as temp:
                path,value=publication(Path(temp)/'publication')
                if failure=='sidecar':Path(value['supportFiles']['server/libe_sqlite3.so']['path']).unlink()
                elif failure=='edition':value['binaries']['developer']['edition']='General'
                else:value['success']=False
                path.write_text(json.dumps(value))
                with self.assertRaises(ValueError):read_publication(path)
    def test_copied_fixture_keeps_product_provenance_and_requires_all_sidecars(self):
        with tempfile.TemporaryDirectory() as temp:
            path,value=publication(Path(temp)/'publication');read_publication(path)
            fixture={'binaries':dict(value['binaries'],synthetic=value['fixtureAssets']['synthetic']),
                     'supportFiles':value['supportFiles'],'productPublication':fixture_provenance(value)}
            verify_fixture_publication(fixture)
            fixture['supportFiles']={}
            with self.assertRaises(ValueError):verify_fixture_publication(fixture)

class RestoreWitnessTests(unittest.TestCase):
    def fixture(self,root):
        state=root/'client/state/demo/prod/stable/2.0.0/windows-x64';app=root/'client/apps/demo/prod/stable/2.0.0/windows-x64'
        state.mkdir(parents=True);app.mkdir(parents=True);(root/'control').mkdir();(root/'client/state/operations').mkdir()
        backup=state/'backups/20261005000000';meta=backup/'.uedt-meta';meta.mkdir(parents=True)
        content=b'normal';digest=hashlib.sha256(content).hexdigest();(backup/'version.txt').write_bytes(content);(app/'version.txt').write_bytes(b'damaged')
        manifest={'files':[{'path':'version.txt','size':len(content),'sha256':digest}]}
        for folder in (state,meta):
            (folder/'installed-manifest.json').write_text(json.dumps(manifest));(folder/'install-state.json').write_text('state')
        (meta/'backup-info.json').write_text('{}');(state/'update.lock').write_text('lock')
        (state/'runtime-state.json').write_text(json.dumps({'schemaVersion':1,'state':0,'origin':'operator-confirmed','installationId':'c'*64}))
        fixture={'id':'fixture','deploymentMode':'portable','platform':'windows-x64','productPublication':{'productSourceHash':'b'*64}}
        return fixture,state,app,backup
    def prepare(self,root):
        fixture,state,app,backup=self.fixture(root)
        with patch('gui_fixture_safety.mutation_guard',return_value=contextlib.nullcontext()):witness=arm(root,fixture,'2.0.0')
        return fixture,state,app,backup,witness
    def test_only_complete_matching_file_transition_is_observed_once(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);fixture,state,app,backup,witness=self.prepare(root)
            self.assertIsNone(observe(root,fixture)) # confirmation cancellation / no apply
            (app/'version.txt').write_bytes((backup/'version.txt').read_bytes())
            result=observe(root,fixture)
            self.assertEqual(witness['attemptId'],result['attemptId']);self.assertEqual('restore-file-transition',result['kind'])
            self.assertNotIn('operationId',result);self.assertIsNone(observe(root,fixture))
    def test_changed_preview_other_operation_runtime_or_metadata_cannot_consume_fault(self):
        for failure in ('preview','operation','runtime','metadata','cohort','partial'):
            with self.subTest(failure=failure),tempfile.TemporaryDirectory() as temp:
                root=Path(temp);fixture,state,app,backup,_=self.prepare(root)
                (app/'version.txt').write_bytes((backup/'version.txt').read_bytes())
                if failure=='preview':(backup/'.uedt-meta/backup-info.json').write_text('changed')
                elif failure=='operation':(root/'client/state/operations'/('d'*32+'.json')).write_text('{}')
                elif failure=='runtime':(state/'runtime-state.json').write_text('{}')
                elif failure=='metadata':(state/'install-state.json').write_text('wrong')
                elif failure=='cohort':fixture['productPublication']['productSourceHash']='e'*64
                else:(app/'version.txt').write_text('partly restored')
                self.assertIsNone(observe(root,fixture));self.assertTrue((root/'control/proxy-fail-catalog-after-restore').exists())
    def test_busy_installation_and_reused_attempt_are_not_completion(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);fixture,state,app,backup,witness=self.prepare(root)
            (app/'version.txt').write_bytes((backup/'version.txt').read_bytes())
            with patch('restore_file_witness.existing_lock',return_value=contextlib.nullcontext(False)):self.assertIsNone(observe(root,fixture))
            history=root/'control/restore-witnesses';history.mkdir();(history/(witness['attemptId']+'.json')).write_text('{}')
            self.assertIsNone(observe(root,fixture))

class AcceptanceTests(unittest.TestCase):
    def test_proof_reads_korean_utf8_and_optional_bom_independent_of_windows_locale(self):
        for encoding in ('utf-8','utf-8-sig'):
            with self.subTest(encoding=encoding),tempfile.TemporaryDirectory() as temp:
                root=Path(temp);path=root/'proof.json';value={'title':'파일 복구 완료 · 상태 재확인 필요'}
                path.write_text(json.dumps(value,ensure_ascii=False),encoding=encoding)
                self.assertEqual(value,load_proof(root,'proof.json'))
    def test_oversized_non_object_and_outside_proofs_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);path=root/'proof.json'
            for content in ('[]',' '*(1024*1024+1)):
                path.write_text(content,encoding='utf-8')
                with self.assertRaises(ValueError):load_proof(root,'proof.json')
            with self.assertRaises(ValueError):load_proof(root,'../proof.json')
    def fixture(self,root):
        (root/'control').mkdir();(root/'client/logs').mkdir(parents=True)
        fixture={'id':'fixture','deploymentMode':'portable','productPublication':{'productSourceHash':'b'*64,'binarySha256':{'launcher':'c'*64}}}
        (root/'control/gui-run-general.json').write_text(json.dumps({'runId':'run1','pid':123,'startedUtc':'2026-10-06T00:00:00+00:00'}))
        (root/'client/logs/ui.log').write_text('[UiDisplay] '+json.dumps({'profile':'general','screenPixels':'0, 0, 1920, 1080','renderScaling':1,'textScale':1,'highContrast':False,'sessionId':'session1','processId':123,'sessionStartedUtc':'2026-10-06T00:00:01+00:00'}))
        (root/'capture.png').write_bytes(b'\x89PNG\r\n\x1a\n')
        proof={'inputActor':'computer-use','fixtureId':'fixture','productSourceHash':'b'*64,'profile':'general','screen':{'pixels':[1920,1080],'renderScaling':1,'textScale':1,'highContrast':False,'runId':'run1','sessionId':'session1'},'result':True,'checks':{name:True for name in CHECKS['initial-configuration']},'screenshots':['capture.png']}
        return fixture,proof
    def test_gui_case_requires_matching_cohort_real_display_and_proof(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);fixture,proof=self.fixture(root)
            record_case(root,fixture,'general','initial-configuration','passed',proof)
            self.assertFalse(summary(root,fixture,'general')['complete'])
            for field,value in [('inputActor','cli'),('productSourceHash','d'*64),('runtimeEndReason','watchdog')]:
                with self.subTest(field=field),self.assertRaises(ValueError):record_case(root,fixture,'general','connection-errors','passed',dict(proof,**{field:value}))
            with self.assertRaises(ValueError):record_case(root,fixture,'general','connection-errors','passed',dict(proof,snapshotScope='all',beforeScope='protected',afterScope='all'))
    def test_missing_case_never_becomes_complete_and_another_cohort_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);fixture,proof=self.fixture(root);create(root,fixture)
            self.assertGreater(summary(root,fixture,'general')['counts']['not-run'],0)
            fixture['productPublication']['productSourceHash']='d'*64
            with self.assertRaises(ValueError):summary(root,fixture,'general')
    def test_aggregate_requires_four_complete_combinations_of_one_cohort(self):
        records=[{'mode':mode,'profile':profile,'productSourceHash':'b'*64,'binarySha256':{'launcher':'c'*64},'complete':False} for mode in ('managed','portable') for profile in ('general','developer')]
        self.assertFalse(aggregate(records)['complete'])
        with self.assertRaises(ValueError):aggregate(records[:3])
        records[0]['productSourceHash']='d'*64
        with self.assertRaises(ValueError):aggregate(records)
    def test_stale_process_or_session_display_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);fixture,proof=self.fixture(root)
            for changed in ({'pid':124},{'startedUtc':'2026-10-06T00:00:02+00:00'}):
                path=root/'control/gui-run-general.json';original=path.read_text();value=json.loads(original);value.update(changed);path.write_text(json.dumps(value))
                with self.assertRaises(ValueError):record_case(root,fixture,'general','initial-configuration','passed',proof)
                path.write_text(original)
            with self.assertRaises(ValueError):record_case(root,fixture,'general','initial-configuration','passed',dict(proof,fixtureId='other'))
    def test_lifetime_requires_gui_install_and_exact_requested_completion(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);fixture,proof=self.fixture(root)
            proof['checks']={name:True for name in CHECKS['v1-install-lifetime']}
            for changed in ({},{'guiInstall':True},{'guiInstall':True,'runtimeEndReason':'requested'}):
                with self.assertRaises(ValueError):record_case(root,fixture,'general','v1-install-lifetime','passed',dict(proof,**changed))
            record_case(root,fixture,'general','v1-install-lifetime','passed',dict(proof,guiInstall=True,runtimeEndReason='requested',runtimeAttemptId='a'*32))

if __name__=='__main__':unittest.main()
