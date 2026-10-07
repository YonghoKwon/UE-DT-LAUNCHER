"""Identity-free numeric aggregate allowlist; phases overlap and are not native SQLite wait."""
import math
FIELDS={'requests','httpElapsedMs','databaseOperations','databaseElapsedMs','sequenceWaitMs','databaseBusyErrors','authenticationOperations','authenticationElapsedMs','policyCompilations','policyReuses','assetCacheHits','assetCacheMisses'}
PHASES={'policy-read','database-wait','signing','catalog-build'}
def numeric(value):return isinstance(value,(int,float)) and not isinstance(value,bool) and math.isfinite(value) and value>=0
def sanitize(value):
    if not isinstance(value,dict):return None
    output={name:value[name] for name in FIELDS if name in value and numeric(value[name])}
    phases=value.get('phaseElapsedMs',{})
    if isinstance(phases,dict):output['phaseElapsedMs']={name:amount for name,amount in phases.items() if name in PHASES and numeric(amount)}
    return output if 'requests' in output else None
