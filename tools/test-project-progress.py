import importlib.util,tempfile,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('project_progress',Path(__file__).with_name('generate-project-progress.py'));module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class ProgressTests(unittest.TestCase):
    def test_missing_gui_is_not_completion_and_secrets_are_not_copied(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            for name in ('README.md','AGENTS.md','PROJECT_GOALS.md'):(root/name).write_text('authority',encoding='utf-8')
            (root/'IMPROVEMENTS.md').write_text('\n'.join(f'| OPS-{i:02}/P1 | 75% | partial | proof | company pending |' for i in range(1,29)),encoding='utf-8')
            value=module.build(root,[],{'windowsTests':849,'token':'SECRET','companyValidated':False})
            self.assertEqual(28,value['after']['open']);self.assertFalse(value['gui']['complete']);self.assertNotIn('SECRET',str(value));self.assertNotIn('overallPercent',value)
            self.assertEqual(25,value['items'][0]['remainingCheckpointPercent'])
            self.assertIn('현재 게시본 근거 없음',module.markdown(value))
    def test_inconsistent_item_count_or_company_claim_is_rejected(self):
        repo=Path(__file__).resolve().parents[1]
        with self.assertRaises(ValueError):module.build(repo,[],{'companyValidated':True})

if __name__=='__main__':unittest.main()
