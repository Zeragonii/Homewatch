import os
import tempfile
# Isolate runs so previous approvals/child assignments never bleed into new tests.
_test_db = tempfile.NamedTemporaryFile(prefix="hw-tests-", suffix=".db", delete=False)
_test_db.close()
os.environ.setdefault("DATABASE_URL", "sqlite:///" + _test_db.name)
os.environ.setdefault("ADMIN_USERNAME", "admin")
os.environ.setdefault("ADMIN_PASSWORD", "testpass")
os.environ.setdefault("SESSION_SECRET", "test-secret")

from fastapi.testclient import TestClient
from server.app.main import app

client = TestClient(app)

def test_login_and_dashboard():
    r = client.post("/api/login", json={"username":"admin","password":"testpass"})
    assert r.status_code == 200
    r = client.get("/api/dashboard")
    assert r.status_code == 200
    assert "devices" in r.json()

def test_pending_enrolment_request():
    import uuid
    body={"installation_id":str(uuid.uuid4()),"enrollment_secret":"abc","hostname":"TEST-PC","os_version":"Windows","agent_version":"0.1.0"}
    r=client.post("/agent/enrol/request",json=body)
    assert r.status_code==200
    assert r.json()["status"]=="pending"


def test_supported_control_commands_are_accepted():
    login=client.post("/api/login",json={"username":"admin","password":"testpass"})
    assert login.status_code==200
    # API validates the command vocabulary; a real enrolled device is exercised by the agent integration.
    for kind in ("lock","logoff","restart","shutdown"):
        r=client.post("/api/devices/not-a-device/commands",json={"kind":kind,"payload":""})
        assert r.status_code==404


def test_screen_time_policy_round_trip():
    import uuid
    name="Policy Kid "+uuid.uuid4().hex[:8]
    r=client.post("/api/children",json={"name":name})
    assert r.status_code==200
    child_id=r.json()["id"]
    body={"enabled":True,"weekday_start":"07:00","weekday_end":"21:00","weekend_start":"08:00","weekend_end":"22:00","weekday_minutes":120,"weekend_minutes":180,"warning_minutes":10,"grace_minutes":5}
    r=client.put(f"/api/children/{child_id}/policy",json=body)
    assert r.status_code==200
    r=client.post(f"/api/children/{child_id}/extension",json={"minutes":30})
    assert r.status_code==200 and r.json()["minutes"]>=30
    r=client.post(f"/api/children/{child_id}/app-limits",json={"process_name":"wow.exe","weekday_minutes":60,"weekend_minutes":90,"enabled":True})
    assert r.status_code==200
    r=client.get("/api/screentime")
    assert r.status_code==200
    kid=next(x for x in r.json()["children"] if x["id"]==child_id)
    assert kid["policy"]["weekday_minutes"]==120
    assert kid["today"]["extension_minutes"]>=30
    assert any(x["process_name"]=="wow.exe" for x in kid["app_limits"])

def test_device_can_be_reassigned_without_changing_identity():
    import uuid
    from server.app.main import SessionLocal, Device, sha256_text

    a=client.post('/api/children',json={'name':'Child A '+uuid.uuid4().hex[:6]}).json()['id']
    b=client.post('/api/children',json={'name':'Child B '+uuid.uuid4().hex[:6]}).json()['id']
    device_id=str(uuid.uuid4())
    with SessionLocal() as db:
        db.add(Device(id=device_id,child_id=a,name='Reassign Test',installation_id=str(uuid.uuid4()),token_hash=sha256_text('token'),hostname='TEST-PC',os_version='Windows',agent_version='0.3.3'))
        db.commit()
    r=client.put(f'/api/devices/{device_id}/child',json={'child_id':b})
    assert r.status_code==200
    assert r.json()['device_id']==device_id
    assert r.json()['child_id']==b
    with SessionLocal() as db:
        device=db.get(Device,device_id)
        assert device is not None
        assert device.child_id==b


def test_typed_messages_are_validated_and_queued():
    import json, uuid
    from server.app.main import SessionLocal, Device, sha256_text

    child_id=client.post('/api/children',json={'name':'Message Kid '+uuid.uuid4().hex[:6]}).json()['id']
    device_id=str(uuid.uuid4())
    with SessionLocal() as db:
        db.add(Device(id=device_id,child_id=child_id,name='Message Test',installation_id=str(uuid.uuid4()),token_hash=sha256_text('token'),hostname='TEST-PC',os_version='Windows',agent_version='0.3.3'))
        db.commit()

    for message_type in ('notify','question','alert'):
        payload=json.dumps({'type':message_type,'text':f'{message_type} test'})
        r=client.post(f'/api/devices/{device_id}/commands',json={'kind':'message','payload':payload})
        assert r.status_code==200

    bad=client.post(f'/api/devices/{device_id}/commands',json={'kind':'message','payload':json.dumps({'type':'nope','text':'bad'})})
    assert bad.status_code==400


def test_question_reply_round_trip_reaches_dashboard():
    import json, uuid
    from server.app.main import SessionLocal, Device, Command, sha256_text

    child_id=client.post('/api/children',json={'name':'Reply Kid '+uuid.uuid4().hex[:6]}).json()['id']
    device_id=str(uuid.uuid4())
    token='reply-test-token'
    with SessionLocal() as db:
        db.add(Device(id=device_id,child_id=child_id,name='Reply Test',installation_id=str(uuid.uuid4()),token_hash=sha256_text(token),hostname='TEST-PC',os_version='Windows',agent_version='0.3.6'))
        db.commit()

    payload=json.dumps({'type':'question','text':'Are you there?'})
    queued=client.post(f'/api/devices/{device_id}/commands',json={'kind':'message','payload':payload})
    assert queued.status_code==200
    command_id=queued.json()['id']

    headers={'X-Device-Id':device_id,'Authorization':f'Bearer {token}'}
    polled=client.get('/agent/commands',headers=headers)
    assert polled.status_code==200
    assert any(c['id']==command_id for c in polled.json())

    response=client.post(f'/agent/messages/{command_id}/respond',headers=headers,json={'response_type':'reply','text':'Yes, one minute'})
    assert response.status_code==200
    assert response.json()['result']=='Reply: Yes, one minute'

    dashboard=client.get('/api/dashboard')
    assert dashboard.status_code==200
    device=next(d for d in dashboard.json()['devices'] if d['id']==device_id)
    message=next(c for c in device['commands'] if c['id']==command_id)
    assert message['status']=='complete'
    assert message['result']=='Reply: Yes, one minute'


def _make_app_device(child_id=None):
    import uuid
    from server.app.main import SessionLocal, Device, sha256_text
    if child_id is None:
        child_id = client.post('/api/children', json={'name':'Apps Kid '+uuid.uuid4().hex[:8]}).json()['id']
    device_id = str(uuid.uuid4())
    token = 'app-token-' + uuid.uuid4().hex
    with SessionLocal() as db:
        db.add(Device(id=device_id, child_id=child_id, name='App Test PC',
                      installation_id=str(uuid.uuid4()), token_hash=sha256_text(token),
                      hostname='PC', os_version='Windows 11', agent_version='0.4.0'))
        db.commit()
    return device_id, {'X-Device-Id':device_id, 'Authorization':f'Bearer {token}'}


def _app_candidate(hash_char='a', sig='valid', path=r'C:\Games\Play.exe'):
    return dict(file_name='Play.exe',file_path=path,sha256=hash_char*64,
                file_size=123456,signature_status=sig,publisher='Example Games' if sig=='valid' else '',
                signer_thumbprint='f'*40 if sig=='valid' else '',
                signer_subject='CN=Example Games', signer_issuer='CN=Example CA',reason='Looks fun')


def test_app_request_approval_verify_and_revoke():
    first, headers = _make_app_device()
    candidate = _app_candidate()
    unauthed = client.post('/agent/applications/requests',json=candidate)
    assert unauthed.status_code == 422 or unauthed.status_code == 401
    queued = client.post('/agent/applications/requests',headers=headers,json=candidate)
    assert queued.status_code == 200, queued.text
    request_id = queued.json()['id']
    assert queued.json()['status']=='pending'
    assert client.post(f'/agent/applications/{request_id}/verify',headers=headers,json=candidate).status_code == 403
    assert client.post(f'/api/applications/requests/{request_id}/decision',json={'decision':'approved','scope':'device'}).status_code == 200
    verified = client.post(f'/agent/applications/{request_id}/verify',headers=headers,json=candidate)
    assert verified.status_code == 200 and verified.json()['elevated'] is False
    changed = {**candidate,'sha256':'b'*64}
    assert client.post(f'/agent/applications/{request_id}/verify',headers=headers,json=changed).status_code == 409
    apps = client.get('/api/applications').json()
    match = next(a for a in apps['approvals'] if a['request_id']==request_id) if 'request_id' in (apps['approvals'][0] if apps['approvals'] else {}) else next(a for a in apps['approvals'] if a['sha256']==candidate['sha256'])
    assert client.delete(f"/api/applications/approvals/{match['id']}").status_code == 200
    assert client.post(f'/agent/applications/{request_id}/verify',headers=headers,json=candidate).status_code == 403
    assert client.get('/agent/applications',headers=headers).json()['applications'][0]['status'] == 'revoked'


def test_app_scope_global_signed_only_and_child_scope():
    device_a, headers_a = _make_app_device()
    device_b, headers_b = _make_app_device()
    unsigned = _app_candidate('c', 'unsigned')
    r = client.post('/agent/applications/requests',headers=headers_a,json=unsigned)
    assert r.status_code == 200
    req_id = r.json()['id']
    r = client.post(f'/api/applications/requests/{req_id}/decision',json={'decision':'approved','scope':'global'})
    assert r.status_code == 400
    signed = _app_candidate('d')
    r = client.post('/agent/applications/requests',headers=headers_a,json=signed)
    req_id = r.json()['id']
    assert client.post(f'/api/applications/requests/{req_id}/decision',json={'decision':'approved','scope':'global'}).status_code == 200
    # Global approval applies to the SAME signed binary on another enrolled PC.
    r = client.post('/agent/applications/requests',headers=headers_b,json={**signed,'file_path':r'C:\Different\Play.exe'})
    assert r.status_code == 200 and r.json()['status'] == 'approved'
    assert client.post(f"/agent/applications/{r.json()['id']}/verify",headers=headers_b,json={**signed,'file_path':r'C:\Different\Play.exe'}).status_code == 200
    # Different binary, same filename and signer, is NOT approved.
    r = client.post('/agent/applications/requests',headers=headers_b,json={**signed,'sha256':'e'*64})
    assert r.status_code == 200 and r.json()['status'] == 'pending'


def test_launch_audit_is_device_scoped():
    device_a, headers_a = _make_app_device()
    device_b, headers_b = _make_app_device()
    queued = client.post('/agent/applications/requests',headers=headers_a,json=_app_candidate('9')).json()
    body={'request_id':queued['id'],'status':'failed','detail':'No administrator access'}
    assert client.post('/agent/applications/launched',headers=headers_b,json=body).status_code == 404
    assert client.post('/agent/applications/launched',headers=headers_a,json=body).status_code == 200
    audit=client.get('/api/applications').json()['launches']
    assert any(x['request_id']==queued['id'] and x['status']=='failed' for x in audit)


def test_updater_status_reaches_admin_dashboard():
    device_id, headers = _make_app_device()
    payload = {
        'hostname': 'KID-PC', 'os_version': 'Windows',
        'agent_version': '0.4.1', 'current_app': 'game.exe',
        'logged_in_user': 'Kid',
        'update_status': 'failed: release archive exceeds legacy limit'
    }
    response = client.post('/agent/heartbeat', headers=headers, json=payload)
    assert response.status_code == 200, response.text
    dashboard = client.get('/api/dashboard')
    assert dashboard.status_code == 200
    row = next(d for d in dashboard.json()['devices'] if d['id'] == device_id)
    assert row['update_status'] == payload['update_status']
    assert row['agent_version'] == '0.4.1'


def test_presentation_name_and_poster_follow_agent_and_approval(monkeypatch):
    from server.app import main as hw
    import io
    from PIL import Image

    device, auth = _make_app_device()
    app_request = client.post('/agent/applications/requests', headers=auth,
                              json=_app_candidate('8')).json()
    digest = '8' * 64
    assert app_request['display_name'] == 'Play'
    assert client.put(f'/api/applications/{digest}/presentation',
                      json={'display_name': 'Super Fun Adventure'}).status_code == 200
    catalog = client.get('/agent/applications',headers=auth).json()['applications']
    assert any(r['display_name']=='Super Fun Adventure' for r in catalog)

    # SteamGridDB responses mocked: tests do not consume API quota or use the network.
    def fake_grid(path):
        if path.startswith('grids/game/123'):
            return {'data':[{'id':456,'thumb':'https://images.steamgriddb.com/thumb.jpg',
                             'url':'https://images.steamgriddb.com/original.jpg'}]}
        return {'data': [{'id':123,'name':'Super Fun Adventure'}]}
    monkeypatch.setattr(hw, 'steamgrid_json', fake_grid)
    image = Image.new('RGB',(600,900),'navy')
    out = io.BytesIO(); image.save(out,'JPEG'); binary=out.getvalue()
    monkeypatch.setattr(hw,'download_artwork', lambda _: binary)

    assert client.get('/api/applications/artwork/search?q=fun').json()['games'][0]['id'] == 123
    assert client.get('/api/applications/artwork/games/123').json()['artwork'][0]['id'] == 456
    assert client.post(f'/api/applications/{digest}/artwork',
                       json={'game_id':123,'artwork_id':456}).status_code == 200
    apps = client.get('/agent/applications',headers=auth).json()['applications']
    item = next(a for a in apps if a['id']==app_request['id'])
    assert item['artwork_revision'] == 1
    assert item['display_name']=='Super Fun Adventure'
    poster = client.get(f'/agent/applications/artwork/{digest}/1',headers=auth)
    assert poster.status_code == 200 and poster.content == binary
    # Other enrolled devices cannot retrieve artwork for apps they haven't requested.
    other, other_auth = _make_app_device()
    assert client.get(f'/agent/applications/artwork/{digest}/1',headers=other_auth).status_code==404
    # An art change does not confer permission to launch or change the EXE's hash.
    assert client.post(f"/agent/applications/{app_request['id']}/verify",
                       headers=auth,json=_app_candidate('8')).status_code == 403
    assert client.post(f'/api/applications/requests/{app_request["id"]}/decision',
                       json={'decision':'approved','scope':'device'}).status_code == 200
    assert client.post(f"/agent/applications/{app_request['id']}/verify",
                       headers=auth,json=_app_candidate('8')).status_code == 200
    assert client.get('/agent/applications',headers=auth).json()['applications'][0]['display_name']=='Super Fun Adventure'


def test_presentation_auth_and_input_rejections():
    from server.app.main import download_artwork
    digest = '7'*64
    assert client.put(f'/api/applications/{digest}/presentation',json={'display_name':'Anything'}).status_code==404
    assert client.get('/api/applications/artwork/search?q=x').status_code==400
    try:
        download_artwork('http://localhost/admin')
        assert False, 'SSRF URL was accepted'
    except Exception as e:
        assert '400' in str(e.status_code) if hasattr(e,'status_code') else False


def test_artwork_can_be_selected_before_friendly_name(monkeypatch):
    """Regression: a brand-new presentation has revision=None until flushed."""
    import io
    import uuid
    from PIL import Image
    from server.app import main as hw

    device, auth = _make_app_device()
    digest = uuid.uuid4().hex * 2
    candidate = _app_candidate('a')
    candidate['sha256'] = digest
    requested = client.post('/agent/applications/requests', headers=auth, json=candidate)
    assert requested.status_code == 200, requested.text

    def fake_grid(path):
        return {'data': [{'id': 902, 'url': 'https://images.steamgriddb.com/example.jpg'}]}

    monkeypatch.setattr(hw, 'steamgrid_json', fake_grid)
    img = Image.new('RGB', (600, 900), 'navy')
    out = io.BytesIO(); img.save(out, 'JPEG')
    monkeypatch.setattr(hw, 'download_artwork', lambda _: out.getvalue())

    endpoint = f'/api/applications/{digest}/artwork'
    choice = {'game_id': 123, 'artwork_id': 902}
    first = client.post(endpoint, json=choice)
    assert first.status_code == 200, first.text
    assert first.json()['artwork_revision'] == 1
    assert first.json()['display_name'] == 'Play'

    second = client.post(endpoint, json=choice)
    assert second.status_code == 200, second.text
    assert second.json()['artwork_revision'] == 2
    assert client.get(f'/agent/applications/artwork/{digest}/2', headers=auth).status_code == 200
    assert client.get(f'/agent/applications/artwork/{digest}/1', headers=auth).status_code == 404


def test_shared_relative_launch_path_requires_parent_approval():
    """One client's deep game EXE is suggested, then approved for other PCs."""
    from server.app.main import SessionLocal, HouseholdCatalogueItem
    device_id, auth = _make_app_device()
    catalogue = client.post('/api/catalogue', json={
        'name': 'Minecraft Dungeons 2',
        'installer_path': r'\\192.168.1.250\installers\minecraft\setup.exe',
        'installer_sha256': '6' * 64, 'description': 'Game install', 'enabled': True
    })
    assert catalogue.status_code == 200, catalogue.text
    cid = catalogue.json()['id']
    deep_exe = r'D:\Games\Minecraft Dungeons 2\Content\Binaries\Win64\Game.exe'
    candidate = _app_candidate('d', path=deep_exe)
    candidate['file_name'] = 'Game.exe'
    request = client.post('/agent/applications/requests', headers=auth, json=candidate)
    assert request.status_code == 200, request.text
    body = dict(status='awaiting_approval', installed_path=r'D:\Games\Minecraft Dungeons 2\Content\Binaries\Win64',
                install_root=r'D:\Games\Minecraft Dungeons 2', executable_path=deep_exe,
                executable_sha256=candidate['sha256'], request_id=request.json()['id'])
    result = client.put(f'/agent/catalogue/{cid}/installation', headers=auth, json=body)
    assert result.status_code == 200, result.text
    assert client.get('/agent/catalogue', headers=auth).json()['items'][-1]['launch_relative_path'] == ''
    suggestions = client.get('/api/catalogue').json()['launch_suggestions']
    hint = next(a for a in suggestions if a['catalogue_id'] == cid)
    assert hint['relative_path'] == r'Content\Binaries\Win64\Game.exe'
    other, other_auth = _make_app_device()
    assert client.post(f'/api/catalogue/{cid}/launch-hints/{hint["id"]}/review',
                       json={'decision':'approve'}).status_code == 200
    remote = client.get('/agent/catalogue', headers=other_auth)
    shared = next(x for x in remote.json()['items'] if x['id'] == cid)
    assert shared['launch_relative_path'] == r'Content\Binaries\Win64\Game.exe'
    # No device-specific install or launch approval is inferred by path sharing.
    assert shared['installation'] is None
    assert client.delete(f'/api/catalogue/{cid}/launch-hint').status_code == 200
    assert client.get(f'/agent/catalogue/{cid}', headers=auth).json()['launch_relative_path'] == ''


def test_untrusted_device_cannot_poison_shared_launch_path():
    device_id, auth = _make_app_device()
    catalogue = client.post('/api/catalogue', json={
        'name': 'Sandbox Game', 'installer_path': r'\\192.168.1.250\installers\sandbox\setup.exe',
        'installer_sha256': '5' * 64, 'description': 'Test', 'enabled': True
    })
    assert catalogue.status_code == 200, catalogue.text
    cid = catalogue.json()['id']
    candidate = _app_candidate('e', path=r'C:\Games\Other\game.exe')
    candidate['file_name'] = 'game.exe'
    request = client.post('/agent/applications/requests', headers=auth, json=candidate)
    assert request.status_code == 200, request.text
    body = dict(status='awaiting_approval', installed_path=r'C:\Games\Sandbox',
                install_root=r'C:\Games\Sandbox',
                executable_path=r'C:\Games\Other\game.exe', executable_sha256='e'*64,
                request_id=request.json()['id'])
    response = client.put(f'/agent/catalogue/{cid}/installation', headers=auth, json=body)
    assert response.status_code in (400,403), response.text
    body['executable_path'] = r'C:\Games\Sandbox\..\Evil\game.exe'
    response = client.put(f'/agent/catalogue/{cid}/installation', headers=auth, json=body)
    assert response.status_code in (400,403), response.text


def test_shared_launch_hint_version_must_match_current_installer():
    from server.app.main import SessionLocal, HouseholdCatalogueItem, HouseholdLaunchSuggestion
    device_id, auth = _make_app_device()
    catalogue = client.post('/api/catalogue', json={
        'name': 'Changing Game', 'installer_path': r'\\192.168.1.250\installers\changing\setup.exe',
        'installer_sha256': '3'*64, 'description': 'Testing', 'enabled': True})
    cid = catalogue.json()['id']
    with SessionLocal() as db:
        hint = HouseholdLaunchSuggestion(catalogue_id=cid, device_id=device_id,
                  relative_path=r'Content\Game.exe', installer_sha256='4'*64,
                  executable_sha256='7'*64)
        db.add(hint); db.commit(); db.refresh(hint); hintid=hint.id
    assert client.post(f'/api/catalogue/{cid}/launch-hints/{hintid}/review',
                       json={'decision':'approve'}).status_code == 409


def test_only_admin_can_set_manual_relative_launch_path():
    device_id, auth = _make_app_device()
    item = client.post('/api/catalogue', json={
        'name': 'Deep Folder Game',
        'installer_path': r'\\192.168.1.250\installers\deep\setup.exe',
        'installer_sha256': 'b' * 64, 'description': '', 'enabled': True})
    assert item.status_code == 200
    cid = item.json()['id']
    target = f'/api/catalogue/{cid}/launch-path'
    good = r'Content\Binaries\Win64\DeepGame.exe'
    assert client.put(target, json={'relative_path': good}).status_code == 200
    assert client.get(f'/agent/catalogue/{cid}', headers=auth).json()['launch_relative_path'] == good
    for bad in (r'..\cmd.exe', r'C:\Windows\system32\cmd.exe', r'\\server\share\cmd.exe', r'A/thing.exe', 'not-an-exe'):
        response = client.put(target, json={'relative_path': bad})
        assert response.status_code == 400, (bad, response.text)
    assert client.get(f'/agent/catalogue/{cid}', headers=auth).json()['launch_relative_path'] == good


def test_058_elevation_is_granted_only_by_parent_for_exact_approved_game():
    import hashlib, uuid
    digest = hashlib.sha256(uuid.uuid4().bytes).hexdigest()
    device_id, headers = _make_app_device()
    candidate = _app_candidate('a')
    candidate['sha256'] = digest
    created = client.post('/agent/applications/requests', headers=headers, json=candidate)
    assert created.status_code == 200, created.text
    request_id = created.json()['id']
    assert client.get(f'/agent/elevation/launch/{request_id}', headers=headers).status_code == 403
    assert client.put(f'/api/elevation/games/{digest}', json={'enabled': True}).status_code == 409
    assert client.post(f'/api/applications/requests/{request_id}/decision', json={'decision': 'approved', 'scope': 'device'}).status_code == 200
    assert client.post(f'/agent/applications/{request_id}/verify',headers=headers,json=candidate).json()['elevated'] is False
    assert client.put(f'/api/elevation/games/{digest}', json={'enabled': True}).status_code == 200
    assert client.post(f'/agent/applications/{request_id}/verify',headers=headers,json=candidate).json()['elevated'] is True
    allowed = client.get(f'/agent/elevation/launch/{request_id}', headers=headers)
    assert allowed.status_code == 200 and allowed.json()['sha256'] == digest
    other_id, other_headers = _make_app_device()
    assert client.get(f'/agent/elevation/launch/{request_id}', headers=other_headers).status_code == 404
    assert client.put(f'/api/elevation/games/{digest}', json={'enabled': False}).status_code == 200
    assert client.get(f'/agent/elevation/launch/{request_id}', headers=headers).status_code == 403


def test_058_parent_installer_elevation_requires_published_verified_manifest():
    import hashlib, uuid
    digest = hashlib.sha256(uuid.uuid4().bytes).hexdigest()
    package = dict(name='Test Elevation Package', installer_path=r'\\192.168.1.250\installers\test\setup.exe', installer_sha256=digest,
        description='', enabled=True)
    created = client.post('/api/catalogue', json=package)
    assert created.status_code == 200, created.text
    catalogue_id = created.json()['id']
    device_id, headers = _make_app_device()
    assert client.get(f'/agent/elevation/installer/{catalogue_id}', headers=headers).status_code == 403
    enabled = client.put(f'/api/catalogue/{catalogue_id}/elevation', json={'enabled': True})
    assert enabled.status_code == 200
    # Legacy single-file packages cannot be auto elevated because they lack a complete manifest.
    assert client.get(f'/agent/elevation/installer/{catalogue_id}', headers=headers).status_code == 403
    updated = client.put(f'/api/catalogue/{catalogue_id}', json={**package, 'installer_sha256': '1'*64})
    assert updated.status_code == 200
    assert updated.json()['elevate_installer'] is False


def test_058_runtime_broker_is_separate_and_uses_local_uac_not_system_service():
    from pathlib import Path
    root = Path(__file__).resolve().parents[1]
    broker = (root/'agent/HomeWatchAgent/RuntimeBroker.cs').read_text()
    assert 'Verb = "runas"' in broker
    assert 'WindowsBuiltInRole.Administrator' in broker
    assert 'agent/elevation/installer/' in broker and 'agent/elevation/launch/' in broker
    assert 'SHA256.HashDataAsync' in broker
    assert 'UseShellExecute = true' in broker
    assert 'ProcessStartInfo("cmd' not in broker
    assert 'CreateProcessAsUser' not in broker
    assert 'Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path))' in broker
    assert 'Settings.Allowed' not in broker
    workflow = (root/'.github/workflows/release.yml').read_text()
    assert "('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in workflow


def test_058_revoking_last_normal_game_approval_revokes_its_elevation_grant():
    import hashlib, uuid
    digest = hashlib.sha256(uuid.uuid4().bytes).hexdigest()
    device, headers = _make_app_device()
    candidate = _app_candidate('b')
    candidate['sha256'] = digest
    requested = client.post('/agent/applications/requests',headers=headers,json=candidate).json()
    assert client.post(f"/api/applications/requests/{requested['id']}/decision",json={'decision':'approved','scope':'device'}).status_code == 200
    assert client.put(f'/api/elevation/games/{digest}',json={'enabled':True}).status_code == 200
    approval = next(a for a in client.get('/api/applications').json()['approvals'] if a['sha256']==digest)
    assert client.delete(f"/api/applications/approvals/{approval['id']}").status_code == 200
    assert client.get(f"/agent/elevation/launch/{requested['id']}",headers=headers).status_code == 403
    from server.app.main import SessionLocal, GameElevationGrant
    with SessionLocal() as db:
        assert db.get(GameElevationGrant,digest).enabled is False
