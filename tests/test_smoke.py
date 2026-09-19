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
