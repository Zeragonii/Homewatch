import io
import os
import tempfile
from pathlib import Path
from unittest.mock import patch

from server.app import nas_library
from server.app.main import app, nas_jobs, nas_job_lock
from fastapi.testclient import TestClient

client = TestClient(app)
client.post('/api/login', json={'username': 'admin', 'password': 'testpass'})


def test_relative_path_cannot_escape_share():
    assert nas_library.validate_root(r'\\192.168.1.250\installers') == r'\\192.168.1.250\installers'
    assert nas_library.resolve(r'\\192.168.1.250\installers', r'game\setup.exe') == r'\\192.168.1.250\installers\game\setup.exe'
    for p in (r'..\windows', r'game\..\secret', r'C:\Windows', r'game\\file.exe', r'game\bad:thing'):
        try: nas_library.validate_relative(p)
        except ValueError: pass
        else: assert False, f'Expected refusal for {p}'


def test_settings_round_trip_never_echoes_password():
    r = client.put('/api/nas/config', json={
        'root_unc': r'\\192.168.1.250\installers', 'username': 'reader', 'password': 'private-credential'})
    assert r.status_code == 200, r.text
    assert 'private-credential' not in r.text
    got = client.get('/api/nas/config')
    assert got.status_code == 200 and got.json()['has_password']
    assert 'private-credential' not in got.text


def test_browser_is_admin_only_and_validates_input():
    anonymous = TestClient(app)
    assert anonymous.get('/api/nas/config').status_code == 401
    assert client.get('/api/nas/browse?path=..%5C..%5C').status_code == 400
    assert client.post('/api/nas/scan', json={'folder':'x', 'entrypoint':'..\\setup.exe'}).status_code == 400


def test_manifest_publish_and_device_specific_installation():
    import uuid
    from server.app.main import SessionLocal, Device, sha256_text
    payload = {'entrypoint': 'setup.exe', 'files': [
        {'path':'setup.exe', 'size': 10, 'sha256':'a'*64},
        {'path':'setup-1.bin', 'size': 20, 'sha256':'b'*64}
    ], 'total_bytes':30,'file_count':2,'digest':'c'*64}
    with nas_job_lock:
        nas_jobs['test-job']={'status':'ready', 'manifest':payload,'share':r'\\192.168.1.250\installers', 'folder':'games\\cat', 'entrypoint':'setup.exe'}
    row = client.post('/api/catalogue', json={'name':'Cat Game', 'manifest_job_id':'test-job', 'enabled':True})
    assert row.status_code == 200, row.text
    item = row.json()
    assert item['file_count']==2 and item['manifest']['files'][1]['path']=='setup-1.bin'
    assert item['package_root']==r'\\192.168.1.250\installers\games\cat'
    device_id = str(uuid.uuid4()); token = 'nas-test-' + uuid.uuid4().hex
    from server.app.main import Child
    with SessionLocal() as db:
        child = Child(name='NAS Kid ' + uuid.uuid4().hex[:6]);db.add(child);db.flush()
        db.add(Device(id=device_id, child_id=child.id, name='Test PC', installation_id=str(uuid.uuid4()),
                      token_hash=sha256_text(token),hostname='PC',os_version='win',agent_version='0.5.0'))
        db.commit()
    headers={'X-Device-ID':device_id,'Authorization':'Bearer '+token}
    res=client.get('/agent/catalogue',headers=headers)
    assert res.status_code==200
    assert any(x['id']==item['id'] and x['manifest']['file_count']==2 for x in res.json()['items'])
    assert 'private-credential' not in res.text
    save=client.put(f"/agent/catalogue/{item['id']}/installation",headers=headers,json={'status':'detecting'})
    assert save.status_code==200,save.text
    show=client.get(f"/agent/catalogue/{item['id']}",headers=headers)
    assert show.json()['installation']['status']=='detecting'


def test_readonly_package_scanner_hashes_every_file(monkeypatch):
    import hashlib
    class FakeStat:
        st_file_attributes=0
        def __init__(self,size): self.st_size=size
    data={'setup.exe':b'exe', 'setup-1.bin':b'bin contents'}
    class FakeEntry:
        def __init__(self,name): self.name=name
        def stat(self, follow_symlinks=False): return FakeStat(len(data[self.name]))
        def is_symlink(self): return False
        def is_dir(self,follow_symlinks=False): return False
        def is_file(self,follow_symlinks=False): return True
    class FakeSmb:
        def register_session(self, *args,**kwargs): pass
        def scandir(self,*args): return [FakeEntry(k) for k in data]
        def open_file(self, name, mode='rb'): return io.BytesIO(data[name.split('\\')[-1]])
    monkeypatch.setattr(nas_library,'smbclient',FakeSmb())
    updates=[]
    m=nas_library.scan_package(r'\\192.168.1.250\installers','games','setup.exe','reader','secret',
        lambda name,done,total: updates.append((name,done,total)))
    assert m['file_count']==2 and m['total_bytes']==len(data['setup.exe'])+len(data['setup-1.bin'])
    assert m['files'][0]['sha256']==hashlib.sha256(data['setup-1.bin']).hexdigest()
    assert updates[-1][1]==updates[-1][2]
