"""Identity-free numeric authority aggregates. Elapsed phases may overlap."""
import math
import json
FIELDS={'requests','httpElapsedMs','sequenceWaitMs','authenticationOperations','authenticationElapsedMs',
        'authorityReads','authorityReadRetries','authorityReadElapsedMs','authorityCommitAttempts',
        'authorityCommitElapsedMs','authorityUnavailable','policyUnavailable','policyCompilations',
        'policyReuses','assetCacheHits','assetCacheMisses'}
PHASES={'policy-read','signing','catalog-build','authority-writer-wait','authority-release-read','upload-status-projection'}
def numeric(value):return isinstance(value,(int,float)) and not isinstance(value,bool) and math.isfinite(value) and value>=0
def sanitize(value):
    if not isinstance(value,dict):return None
    output={name:value[name] for name in FIELDS if name in value and numeric(value[name])}
    phases=value.get('phaseElapsedMs',{})
    if isinstance(phases,dict):output['phaseElapsedMs']={name:amount for name,amount in phases.items() if name in PHASES and numeric(amount)}
    return output if 'requests' in output else None


def read_shutdown_aggregate(path):
    """Read the last allowlisted shutdown aggregate from an owned run's server log."""
    try:
        with path.open(encoding='utf-8-sig', errors='replace') as log:
            aggregate = None
            for line in log:
                _, marker, payload = line.partition('DistributionPerformance ')
                if not marker:
                    continue
                try:
                    accepted = sanitize(json.loads(payload))
                except ValueError:
                    continue
                if accepted is not None:
                    aggregate = accepted
            return aggregate
    except FileNotFoundError:
        return None
