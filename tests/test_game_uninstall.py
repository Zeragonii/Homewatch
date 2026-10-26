"""v0.5.5: installation removal state, audit, and source safety boundaries."""
from pathlib import Path
from test_smoke import client, _make_app_device, _app_candidate

ROOT = Path(__file__).resolve().parents[1]


def test_uninstall_is_device_scoped_and_keeps_approval_history():
    assert client.post('/api/login', json={'username':'admin','password':'testpass'}).status_code == 200
    first, auth = _make_app_device()
    second, other_auth = _make_app_device()
    import uuid
    new_digest = uuid.uuid4().hex * 2
    new = client.post('/api/catalogue', json={
        'name':'Demo Game Removal',
        'installer_path':r'\\192.168.1.250\installers\demo\setup.exe',
        'installer_sha256':new_digest, 'description':'Demo', 'enabled': True
    })
    assert new.status_code == 200, new.text
    cid = new.json()['id']
    exe = r'D:\Games\Demo Game\Content\Win64\game.exe'
    candidate = _app_candidate('1',path=exe)
    candidate['file_name'] = 'game.exe'
    request = client.post('/agent/applications/requests',headers=auth,json=candidate)
    assert request.status_code == 200
    saved = client.put(f'/agent/catalogue/{cid}/installation',headers=auth,json={
        'status':'awaiting_approval', 'installed_path':r'D:\Games\Demo Game',
        'install_root':r'D:\Games\Demo Game', 'executable_path':exe,
        'executable_sha256':'1'*64,'request_id':request.json()['id']})
    assert saved.status_code == 200,saved.text
    catalog = client.get(f'/agent/catalogue/{cid}',headers=auth).json()
    assert catalog['installation']['install_root'] == r'D:\Games\Demo Game'
    assert client.get(f'/agent/catalogue/{cid}',headers=other_auth).json()['installation'] is None
    assert client.put(f'/agent/catalogue/{cid}/installation',headers=other_auth,
               json={'status':'uninstalled'}).status_code == 409
    assert client.put(f'/agent/catalogue/{cid}/installation',headers=auth,
               json={'status':'uninstalling','executable_path':exe,
                     'install_root':r'D:\Games\Demo Game'}).status_code == 200
    assert client.put(f'/agent/catalogue/{cid}/installation',headers=auth,
               json={'status':'uninstall_failed'}).status_code == 200
    state = client.get(f'/agent/catalogue/{cid}',headers=auth).json()['installation']
    assert state['status']=='uninstall_failed' and state['executable_path']==exe
    assert client.put(f'/agent/catalogue/{cid}/installation',headers=auth,
               json={'status':'uninstalled'}).status_code == 200
    state = client.get(f'/agent/catalogue/{cid}',headers=auth).json()['installation']
    assert state['status']=='uninstalled'
    assert state['install_root']=='' and state['approved'] is False
    assert state['executable_path']==exe  # prevents a stale standalone app tile
    assert client.post(f'/agent/catalogue/{cid}/attempts',headers=auth,
               json={'status':'removed','detail':'Windows uninstaller completed'}).status_code == 200
    logs = client.get('/api/catalogue').json()['attempts']
    assert any(x['catalogue_id']==cid and x['status']=='removed' for x in logs)
    assert client.get(f'/agent/catalogue/{cid}',headers=other_auth).json()['installation'] is None


def test_source_only_uses_normal_user_uninstall_with_safe_folder_limits():
    installer = (ROOT/'agent/HomeWatchAgent/HouseholdGameUninstaller.cs').read_text()
    ui = (ROOT/'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
    assert 'SafeDeleteRoot' in installer
    assert 'Path.GetDirectoryName(gameRoot)' in installer
    assert 'FileAttributes.ReparsePoint' in installer
    assert 'CommandLineToArgvW' in installer
    assert 'Directory.Delete(safeRoot, recursive: true)' in ui
    assert 'MessageBoxButtons.YesNo' in ui
    assert 'UninstallCatalogueAsync' in ui
    assert 'agent/catalogue/{app.CatalogueId}/attempts' in ui
    assert 'HomeWatchService' not in installer
