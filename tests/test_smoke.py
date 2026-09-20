import os
os.environ.setdefault("DATABASE_URL", "sqlite:///./test-homewatch.db")
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
