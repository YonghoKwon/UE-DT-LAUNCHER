"""Published console-Agent proof using a selection-only client file, never operational settings."""
import json,time

def prove_managed_display(run,launcher,client,config):
    profile=client/'minimal-managed-client.json'
    profile.write_text(json.dumps({'schemaVersion':3,'deploymentMode':'managed-agent','manifestUrl':'','projectId':'demo','environment':'prod','channel':'stable','targetPlatform':config['targetPlatform'],'versionPolicy':'exact','requestedVersion':'1.0.0'}))
    report=json.loads(run(launcher,'doctor','--config',profile,'--online'))
    if not report['healthy']:raise RuntimeError('Selection-only client diagnostics failed')
    run(launcher,'run','--config',profile)
    deadline=time.monotonic()+20
    while time.monotonic()<deadline:
        observation=json.loads(run(launcher,'runtime','inspect','--config',profile,'--version','1.0.0'))
        if observation['state']==0:break
        time.sleep(.1)
    else:raise RuntimeError('Minimal managed launch did not finish under supervision')
    marker=client/'apps/demo/prod/stable/1.0.0'/config['targetPlatform']/'runtime-marker.txt'
    if not marker.exists() or 'UE_DT_FAKE_GAME_OK' not in marker.read_text():raise RuntimeError('Exact managed payload was not launched')
    return {'diagnostics':True,'exactLaunch':True,'supervisedCompletion':True,'operationalFieldsInClient':False}
