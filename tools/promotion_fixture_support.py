"""Use production promotion CLI explicitly in new-server acceptance fixtures."""
import json


def promote(run_server, config, project, version, platform, reason='isolated acceptance fixture'):
    # Old frozen cohorts remain useful for historical reproduction.
    if 'promotion inspect' not in run_server('--help'): return False
    track=['--project-id',project,'--environment','prod','--channel','stable','--platform',platform]
    state=json.loads(run_server('promotion','inspect',*track,'--config',config))
    run_server('promote',*track,'--version',version,'--expected-revision',str(state['revision']),'--reason',reason,'--config',config)
    return True
