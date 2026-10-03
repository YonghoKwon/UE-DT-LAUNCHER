"""Allowlisted synthetic comparison, not a company SLA or automatic release approval."""
import argparse
import json
import statistics
from pathlib import Path
from evidence_contract import atomic

PROFILES = ('small', 'large', 'mixed')
SCENARIOS = ('install', 'unchanged', 'next-version', 'repair')


def summarize(report):
    if report.get('complete') is not True:
        raise ValueError('Cannot compare an incomplete measurement')
    clients = {}
    for profile in PROFILES:
        clients[profile] = {}
        for scenario in SCENARIOS:
            rows = [row for row in report['client'] if row['profile']==profile and row['scenario']==scenario and not row['warmup']]
            if len(rows) != 3:
                raise ValueError('Exactly three measured samples are required')
            clients[profile][scenario] = {'medianMs':statistics.median(row['wallMs'] for row in rows),
                'medianContentBytes':statistics.median(row['contentNetworkBytes'] for row in rows)}
    api = {str(row['clients']): {'medianP95Ms':row['median_p95_ms'], 'failedRequests':row['failed_requests'],
            'startupFailures':row.get('startup_failures',0)} for row in report['api'] if row['clients'] in (1,10,30)}
    if len(api)!=3 or any(row['medianP95Ms'] is None or row['failedRequests']!=0 or row['startupFailures'] for row in api.values()):
        raise ValueError('Incomplete or failed API comparison')
    return {'binarySha256':report['binarySha256'], 'client':clients, 'api':api}


def compare(baseline, candidate):
    rows = []
    for profile in PROFILES:
        for scenario in SCENARIOS:
            before=baseline['client'][profile][scenario]['medianMs']; after=candidate['client'][profile][scenario]['medianMs']
            rows.append({'profile':profile,'scenario':scenario,'beforeMs':before,'afterMs':after,'ratio':after/before})
    api={count:candidate['api'][count]['medianP95Ms']/baseline['api'][count]['medianP95Ms'] for count in ('1','10','30')}
    return {'client':rows,'maxRatio':max(*[row['ratio'] for row in rows],*api.values()),
        'withinTenPercent':all(row['ratio']<=1.10 for row in rows) and all(ratio<=1.10 for ratio in api.values()),
        'apiP95Ratios':api}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('baseline','preoptimization','candidate','output'):parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args()
    baseline, before, candidate = [summarize(json.loads(path.read_text())) for path in (args.baseline,args.preoptimization,args.candidate)]
    original, optimization = compare(baseline,candidate),compare(before,candidate)
    record={'schemaVersion':1,'scope':'Windows-synthetic-HTTP-signature-private-proxy-5ms',
        'warmupRuns':1,'measuredRuns':3,'cache':'fresh install/state; warm OS cache; order/ambient-load uncontrolled',
        'baseline':baseline,'preoptimization':before,'candidate':candidate,
        'againstOriginal':original,'againstPreoptimization':optimization,
        'candidatePassesOriginalBaselineTimeGate':original['withinTenPercent'] and original['apiP95Ratios']['10']<1,
        'candidatePassesLocalTimeGates':original['withinTenPercent'] and optimization['withinTenPercent'] and optimization['apiP95Ratios']['10']<1,
        'companySlaAccepted':False}
    atomic(args.output,record)
    print(json.dumps({'againstOriginal':original,'againstPreoptimization':optimization,'candidatePassesLocalTimeGates':record['candidatePassesLocalTimeGates']}))


if __name__=='__main__':main()
