"""Identity-free numeric authority aggregates. Elapsed phases may overlap."""
import math
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
