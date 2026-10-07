import unittest
from markdown_anchors import anchors
class AnchorTests(unittest.TestCase):
    def test_korean_inline_markup_duplicate_and_explicit_anchors(self):
        values=anchors('# 현재 **GUI** 수용 체크리스트\n## 같은 제목\n## 같은 제목\n<a id="legacy"></a>\n```text\n# 코드 제목\n```')
        self.assertIn('현재-gui-수용-체크리스트',values);self.assertIn('같은-제목',values);self.assertIn('같은-제목-1',values)
        self.assertIn('legacy',values);self.assertNotIn('코드-제목',values)
    def test_linked_header_uses_label_not_url(self):self.assertEqual({'현재-참고-문서'},anchors('## 현재 [참고](guide.md) 문서'))
if __name__=='__main__':unittest.main()
