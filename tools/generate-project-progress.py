"""Generate evidence-bounded progress from the four authoritative documents, never an average score."""
import argparse,hashlib,json,re
from pathlib import Path
from acceptance_ledger import aggregate
from evidence_contract import atomic


def build(repo,summaries,validation):
    documents={name:hashlib.sha256((repo/name).read_bytes()).hexdigest() for name in ('README.md','AGENTS.md','IMPROVEMENTS.md','PROJECT_GOALS.md')}
    text=(repo/'IMPROVEMENTS.md').read_text(encoding='utf-8')
    rows=[]
    for line in text.splitlines():
        match=re.match(r'^\| ([A-Z]+-\d+)/P\d \| (0|25|50|75|100)% \|',line)
        if not match:continue
        cells=[c.strip() for c in line.strip('|').split('|')]
        rows.append({'id':match[1],'percent':int(match[2]),'remainingCheckpointPercent':100-int(match[2]),'state':cells[2],'remainingCondition':cells[-1]})
    if len(rows)!=28 or len({r['id'] for r in rows})!=28:raise ValueError('Expected 28 unique authoritative items')
    counts={'complete':sum(r['percent']==100 for r in rows),'partial':sum(0<r['percent']<100 for r in rows),'waiting':sum(r['percent']==0 for r in rows)}
    counts['open']=28-counts['complete'];counts['total']=28
    gui=aggregate(summaries) if summaries else {'complete':False,'combinations':[],'reason':'no current acceptance evidence'}
    if summaries and gui['productSourceHash']!=validation.get('productSourceHash'):raise ValueError('Validation and GUI products differ')
    allowed=('productSourceHash','sourceHead','windowsTests','linuxTests','linuxSkipped','windowsPublished','linuxPublished','httpE2e','httpsE2e','companyValidated')
    proof={k:validation.get(k) for k in allowed}
    if proof.get('companyValidated') not in (False,None):raise ValueError('This local report cannot certify company acceptance')
    return {'schemaVersion':1,'authoritativeDocuments':documents,'before':{'complete':6,'partial':19,'waiting':3,'open':22,'total':28},'after':counts,
            'automation':proof,'gui':gui,'items':rows,'percentageMeaning':'evidence checkpoints, not effort or overall product completion'}


def markdown(result):
    lines=['# 현재 전체 진행 상태','', '4종 정본과 현재 게시본 근거에서 생성한 자료입니다. 항목 퍼센트 평균을 제품 완성률로 해석하지 않습니다.','',
           '| 구분 | 완료 | 부분 | 대기 | 열린 항목 | 전체 |','|---|---:|---:|---:|---:|---:|']
    for label,key in [('작업 전','before'),('현재','after')]:
        c=result[key];lines.append(f"| {label} | {c['complete']} | {c['partial']} | {c['waiting']} | {c['open']} | {c['total']} |")
    p=result['automation'];lines+=['','| 검증 구분 | 현재 근거 | 회사 인수 |','|---|---|---|',
        f"| Windows 자동화/게시 | 테스트 {p.get('windowsTests')} / 게시 {p.get('windowsPublished')} | 별도 |",
        f"| Linux 자동화/게시 | 테스트 {p.get('linuxTests')} / 제외 {p.get('linuxSkipped')} / 게시 {p.get('linuxPublished')} | 실제 RHEL 별도 |",
        f"| 인증 E2E | HTTP {p.get('httpE2e')} / HTTPS {p.get('httpsE2e')} | 회사 CA/프록시/계정 별도 |",'','| 현재 GUI 조합 | 통과 | 실패 | 미실행 | 진행 | 환경 제약 |','|---|---:|---:|---:|---:|---:|']
    for row in result['gui']['combinations']:
        c=row['counts'];lines.append('| '+row['mode']+' / '+row['profile']+' | '+' | '.join(str(c[k]) for k in ('passed','failed','not-run','running','blocked'))+' |')
    if not result['gui']['combinations']:lines.append('| 현재 게시본 근거 없음 | 0 | 0 | 미확인 | 0 | 0 |')
    lines+=['','| 항목 | 진행 | 남은 체크포인트 | 완료 조건 |','|---|---:|---:|---|']
    for row in result['items']:
        if row['percent']<100:lines.append(f"| {row['id']} | {row['percent']}% | {row['remainingCheckpointPercent']}% | {row['remainingCondition']} |")
    return '\n'.join(lines)+'\n'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--validation',required=True);parser.add_argument('--gui-summary',action='append',default=[]);parser.add_argument('--output',required=True)
    args=parser.parse_args();repo=Path(__file__).resolve().parents[1]
    value=build(repo,[json.loads(Path(p).read_text(encoding='utf-8-sig')) for p in args.gui_summary],json.loads(Path(args.validation).read_text(encoding='utf-8-sig')))
    output=Path(args.output);atomic(output.with_suffix('.json'),value)
    # Derived Markdown is the generated report, not a fifth maintained source of truth.
    output.with_suffix('.md').write_text(markdown(value),encoding='utf-8')
