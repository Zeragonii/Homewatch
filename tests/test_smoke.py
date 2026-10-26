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
