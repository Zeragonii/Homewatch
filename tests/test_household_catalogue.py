"""Household catalogue is a user-level installer distribution feature, not elevation."""
import os
import tempfile
import uuid

file = tempfile.NamedTemporaryFile(prefix='hw-catalogue-', suffix='.db', delete=False)
file.close()
os.environ.setdefault('DATABASE_URL', 'sqlite:///' + file.name)
os.environ.setdefault('ADMIN_USERNAME', 'admin')
os.environ.setdefault('ADMIN_PASSWORD', 'testpass')
os.environ.setdefault('SESSION_SECRET', 'test-secret')

from fastapi.testclient import TestClient
from server.app.main import app, SessionLocal, Device, sha256_text

c = TestClient(app)


def agent():
    child_id = c.post('/api/children', json={'name': 'Library child ' + uuid.uuid4().hex[:8]}).json()['id']
    device_id = str(uuid.uuid4())
    token = 'library-token-' + uuid.uuid4().hex
    with SessionLocal() as db:
        db.add(Device(id=device_id, child_id=child_id, name='Laptop', hostname='LAPTOP',
                      installation_id=str(uuid.uuid4()), token_hash=sha256_text(token),
                      os_version='Windows 11', agent_version='0.4.5'))
        db.commit()
    return device_id, {'X-Device-ID': device_id, 'Authorization': 'Bearer ' + token}


def package(**overrides):
    return dict(name='Little Kitty, Big City',
                installer_path='192.168.1.250/installers/game1/game1install.exe',
                installer_sha256='a' * 64, description='Cute cat game!', enabled=True,
                **overrides)


def test_publish_visible_to_all_clients_then_unpublish():
    c.post('/api/login', json={'username': 'admin', 'password': 'testpass'})
    first_id, first_headers = agent()
    second_id, second_headers = agent()
    before = c.get('/agent/catalogue', headers=first_headers)
    assert before.status_code == 200
    before_count = len(before.json()['items'])
    created = c.post('/api/catalogue', json=package())
    assert created.status_code == 200, created.text
    item = created.json()
    assert item['installer_path'] == r'\\192.168.1.250\installers\game1\game1install.exe'
    assert item['name'] == 'Little Kitty, Big City'
    assert c.get(f"/agent/catalogue/{item['id']}", headers=second_headers).status_code == 200
    for headers in (first_headers, second_headers):
        entries = c.get('/agent/catalogue', headers=headers).json()['items']
        assert len(entries) == before_count + 1
        assert any(x['id'] == item['id'] for x in entries)
    audit = c.post(f"/agent/catalogue/{item['id']}/attempts", headers=second_headers,
                   json={'status': 'started', 'detail': 'Installer opened as standard user'})
    assert audit.status_code == 200
    assert any(x['device_id'] == second_id for x in c.get('/api/catalogue').json()['attempts'])
    removed = c.delete(f"/api/catalogue/{item['id']}")
    assert removed.status_code == 200
    assert c.get(f"/agent/catalogue/{item['id']}", headers=first_headers).status_code == 404
    assert all(x['id'] != item['id'] for x in c.get('/agent/catalogue', headers=first_headers).json()['items'])
    assert c.get('/api/catalogue').json()['attempts']


def test_edit_updates_presentation_and_artwork_editor_authorization():
    c.post('/api/login', json={'username': 'admin', 'password': 'testpass'})
    p = dict(name='Game A', installer_path=r'\\192.168.1.250\installers\Game A\setup.exe',
             installer_sha256='b'*64, description='', enabled=True)
    created = c.post('/api/catalogue', json=p)
    assert created.status_code == 200, created.text
    item = created.json()
    renamed = c.put(f"/api/catalogue/{item['id']}", json={**p,'name': 'Game B'})
    assert renamed.status_code == 200
    assert renamed.json()['name'] == 'Game B'
    changed = c.put('/api/applications/' + 'b'*64 + '/presentation',
                    json={'display_name': 'My favourite game'})
    assert changed.status_code == 200
    assert next(x for x in c.get('/api/catalogue').json()['items'] if x['id']==item['id'])['name'] == 'My favourite game'


def test_bad_package_metadata_rejected():
    c.post('/api/login', json={'username': 'admin', 'password': 'testpass'})
    good = package()
    invalid = [
        {**good, 'installer_path': r'C:\Windows\System32\cmd.exe'},
        {**good, 'installer_path': r'\\192.168.1.250\C$\admin.exe'},
        {**good, 'installer_path': r'\\192.168.1.250\installers\..\evil.exe'},
        {**good, 'installer_path': 'ftp://example.com/malware.exe'},
        {**good, 'installer_sha256': 'not-a-hash'},
        {**good, 'installer_path': 'https://example.com/setup.exe?token=secret'},
        {**good, 'installer_path': 'https://example.com/setup.bat'},
    ]
    for candidate in invalid:
        response = c.post('/api/catalogue', json=candidate)
        assert response.status_code == 400, response.text
    assert c.get('/agent/catalogue').status_code in (401, 422)


def test_published_catalogue_artwork_available_to_all_enrolled_devices(tmp_path, monkeypatch):
    from server.app import main
    from server.app.main import ApplicationPresentation
    c.post('/api/login', json={'username': 'admin', 'password': 'testpass'})
    _, headers_one = agent()
    _, headers_two = agent()
    digest = 'e' * 64
    created = c.post('/api/catalogue', json=dict(name='Shared Game',
        installer_path=r'\\192.168.1.250\installers\shared\setup.exe',
        installer_sha256=digest, description='', enabled=True))
    assert created.status_code == 200
    monkeypatch.setattr(main, 'ARTWORK_DIR', tmp_path)
    # Test file dispatch only: a tiny JPEG signature is enough to exercise
    # server route authorization without involving SteamGridDB network calls.
    filename = digest + '-1.jpg'
    (tmp_path / filename).write_bytes(b'\xff\xd8\xff\xd9')
    with SessionLocal() as db:
        presentation = db.get(ApplicationPresentation, digest)
        presentation.artwork_revision = 1
        presentation.artwork_file = filename
        db.commit()
    for headers in (headers_one, headers_two):
        response = c.get(f'/agent/applications/artwork/{digest}/1', headers=headers)
        assert response.status_code == 200
    c.delete('/api/catalogue/' + str(created.json()['id']))
    assert c.get(f'/agent/applications/artwork/{digest}/1', headers=headers_two).status_code == 404


def test_catalogue_cannot_trigger_privileged_command():
    _, headers = agent()
    created = c.post('/api/catalogue', json=dict(name='Normal Installer',
        installer_path=r'\\192.168.1.250\installers\software\setup.exe',
        installer_sha256='f'*64, description='', enabled=True))
    assert created.status_code == 200
    item_id = created.json()['id']
    rejected = c.post(f'/agent/catalogue/{item_id}/attempts', headers=headers,
        json={'status':'run_as_system', 'detail':'elevate'})
    assert rejected.status_code == 400
    # The catalogue API only returns metadata; no server-side/privileged launch.
    assert 'installer_path' in c.get(f'/agent/catalogue/{item_id}', headers=headers).json()
