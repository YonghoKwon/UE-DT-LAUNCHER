"""Real IPC restore of a healthy synthetic backup; isolated CLI fixture only."""
import json,uuid
from gui_fixture_evidence import agent_status,verify_files

def prove_managed_rollback(run,launcher,client,config,endpoint):
    platform=config['targetPlatform'];selection={'projectId':'demo','environment':'prod','channel':'stable','platform':platform,'version':'2.0.0'}
    profile=client/'minimal-rollback-client.json'
    profile.write_text(json.dumps(dict(schemaVersion=3,deploymentMode='managed-agent',manifestUrl='',projectId='demo',environment='prod',channel='stable',targetPlatform=platform,versionPolicy='exact',requestedVersion='2.0.0')))
    run(launcher,'run','--config',profile,'--repair','--no-launch')
    state=client/'state/demo/prod/stable/2.0.0'/platform;app=client/'apps/demo/prod/stable/2.0.0'/platform
    files=verify_files(app,state/'installed-manifest.json')
    def send(command,**options):
        response=agent_status(endpoint,dict(protocolVersion=1,correlationId=uuid.uuid4().hex,command=command,projectId='demo',selection=selection,clientCapabilities=['runtime-supervision-v1'],**options))
        if not response.get('success'):raise RuntimeError('Managed synthetic rollback command was rejected')
        return response
    preview=send('rollback-preview')['rollbackPreview']
    backup=state/'backups'/preview['backupId']
    if verify_files(backup,backup/'.uedt-meta/installed-manifest.json')!=files:raise RuntimeError('Backup is not a complete healthy payload')
    (app/'version.txt').write_text('synthetic-damage')
    restored=send('rollback',expectedBackupId=preview['backupId'],expectedBackupFingerprint=preview['metadataFingerprint'])
    if restored.get('installationCommitted') is not True or any(restored.get('selectedRelease',{}).get(k)!=v for k,v in selection.items()):raise RuntimeError('Restore completion/selection was not preserved')
    verify_files(app,state/'installed-manifest.json')
    return {'committed':True,'selectedReleaseConfirmed':True,'verifiedFiles':files}
