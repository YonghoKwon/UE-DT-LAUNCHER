"""Verify the four-document progress ledger instead of hand-maintaining contradictory totals."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
text=(root/'IMPROVEMENTS.md').read_text(encoding='utf-8')
rows=re.findall(r'^\| ([A-Z]+-\d+)/P\d \| (\d+)% \|',text,re.M)
assert len(rows)==len(dict(rows))==28,rows
counts=[sum(int(v)==100 for _,v in rows),sum(0<int(v)<100 for _,v in rows),sum(int(v)==0 for _,v in rows)]
assert f'| {counts[0]}개 | {counts[1]}개 | {counts[2]}개 | 28개 |' in text
assert f'나머지 {counts[2]}개는 0%' in text
table=text.split('| 항목 | ① 구현/절차 |')[1].split('근거:')[0]
assert not re.search(r'\n\s*\n\|',table),'Broken checkpoint Markdown table'
for key,value in rows:
    if int(value): assert re.search(r'^\| '+key+r' \|.*\| '+value+r'% \|$',table,re.M),key
assert '이번에 수정한 것은 문서뿐' not in text
print('PASS: 28 unique items; totals, checkpoint rows and progress summary agree')
