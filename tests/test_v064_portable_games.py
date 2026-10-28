"""Portable game deployment v0.6.4; baseline is recovered 0.6.3.2."""
import hashlib
import io
import os
import tempfile
import uuid
from pathlib import Path

os.environ.setdefault("DATABASE_URL", "sqlite:///" + tempfile.mktemp(prefix="hw064-", suffix=".db"))
os.environ.setdefault("ADMIN_USERNAME", "admin")
os.environ.setdefault("ADMIN_PASSWORD", "testpass")
os.environ.setdefault("SESSION_SECRET", "test-secret")

from fastapi.testclient import TestClient
from server.app import nas_library
from server.app.main import (
    app, nas_jobs, nas_job_lock, SessionLocal, Device, Child, sha256_text,
)

ROOT = Path(__file__).resolve().parents[1]
client = TestClient(app)
assert client.post('/api/login', json={'username':'admin','password':'testpass'}).status_code == 200


def make_device():
    token = uuid.uuid4().hex
    did = str(uuid.uuid4())
    with SessionLocal() as db:
        child = Child(name='Portable child '+uuid.uuid4().hex[:7]); db.add(child); db.flush()
        db.add(Device(id=did, child_id=child.id, name='Portable test PC',
                      installation_id=str(uuid.uuid4()), token_hash=sha256_text(token),
                      hostname='TEST', os_version='Windows', agent_version='0.6.4'))
        db.commit()
    return {'X-Device-ID':did, 'Authorization':'Bearer '+token}


def make_job(exe='Engine\\Binaries\\Win64\\Stray-Win64-Shipping.exe', digest=None):
    digest = digest or uuid.uuid4().hex * 2
    jobid = uuid.uuid4().hex
    manifest={'entrypoint':exe, 'files':[
        {'path':exe,'sha256':'a'*64,'size':12},
        {'path':'Content\\pak0.pak','sha256':'b'*64,'size':44}],
        'file_count':2, 'total_bytes':56, 'deployment_type':'portable','digest':digest}
    with nas_job_lock:
        nas_jobs[jobid] = dict(status='ready', deployment_type='portable', manifest=manifest,
                               share=r'\\192.168.1.250\games', folder=r'Portable\Stray',
                               entrypoint=exe)
    return jobid, digest


def test_portable_publication_and_device_manifest_revision():
    job,digest=make_job()
    bad=client.post('/api/catalogue',json={'name':'Stray','manifest_job_id':job,
        'deployment_type':'installer'})
    assert bad.status_code==400
    publish=client.post('/api/catalogue',json={'name':'Stray','manifest_job_id':job,
        'deployment_type':'portable'})
    assert publish.status_code==200,publish.text
    item=publish.json();cid=item['id']
    assert item['deployment_type']=='portable' and item['launch_relative_path']==item['package_entrypoint']
    assert item['installer_sha256']==digest and item['package_root'].endswith(r'Portable\Stray')
    assert item['elevate_installer'] is False
    assert client.put(f'/api/catalogue/{cid}/elevation',json={'enabled':True}).status_code==400
    assert client.get(f'/agent/elevation/installer/{cid}',headers=make_device()).status_code==403
    headers=make_device()
    catalogue=client.get(f'/agent/catalogue/{cid}',headers=headers)
    assert catalogue.status_code==200 and catalogue.json()['manifest']['entrypoint'].startswith('Engine\\')
    initial=client.put(f'/agent/catalogue/{cid}/installation',headers=headers,
        json={'status':'awaiting_approval','installed_path':r'D:\Games\Stray [HW-1]',
        'install_root':r'D:\Games\Stray [HW-1]', 'executable_path':r'D:\Games\Stray [HW-1]\Engine\Binaries\Win64\Stray-Win64-Shipping.exe',
        'executable_sha256':'a'*64,'installed_manifest_sha256':digest})
    assert initial.status_code==200,initial.text
    from server.app.main import ApplicationPresentation
    with SessionLocal() as db:
        presentation = db.get(ApplicationPresentation, digest)
        presentation.artwork_file = 'test-artwork.jpg'
        presentation.artwork_revision = 2
        presentation.artwork_credit = 'Family selection'
        db.commit()
    job2,digest2=make_job(digest='d'*64)
    edit=client.put(f'/api/catalogue/{cid}',json={'name':'Stray','manifest_job_id':job2,
        'deployment_type':'portable'})
    assert edit.status_code==200,edit.text
    assert edit.json()['installer_sha256']==digest2
    assert edit.json()['launch_relative_path']==edit.json()['package_entrypoint']
    with SessionLocal() as db:
        copied = db.get(ApplicationPresentation, digest2)
        assert copied is not None and copied.artwork_file == 'test-artwork.jpg'
        assert copied.artwork_revision == 2
    current=client.get(f'/agent/catalogue/{cid}',headers=headers).json()
    assert current['installation']['update_available'] is True
    assert current['installation']['installed_manifest_sha256']==digest
    # A status-only update must NOT destroy the existing game or approval state.
    pending=client.put(f'/agent/catalogue/{cid}/installation',headers=headers,
        json={'status':'updating'})
    assert pending.status_code==200,pending.text
    current=client.get(f'/agent/catalogue/{cid}',headers=headers).json()
    assert current['installation']['executable_sha256']=='a'*64
    assert current['installation']['install_root']==r'D:\Games\Stray [HW-1]'
    outdated=client.put(f'/agent/catalogue/{cid}/installation',headers=headers,
        json={'status':'awaiting_approval','installed_manifest_sha256':digest})
    assert outdated.status_code==409
    updated=client.put(f'/agent/catalogue/{cid}/installation',headers=headers,
        json={'status':'awaiting_approval','installed_manifest_sha256':digest2})
    assert updated.status_code==200,updated.text
    assert client.get(f'/agent/catalogue/{cid}',headers=headers).json()['installation']['update_available'] is False


def test_portable_requires_scanned_folder_and_prevents_manual_mode_override():
    bad=client.post('/api/catalogue',json={'name':'Loose files','deployment_type':'portable',
        'installer_path':r'\\192.168.1.250\games\Stray', 'installer_sha256':'1'*64})
    assert bad.status_code==400
    assert client.post('/api/nas/scan',json={'folder':'games','entrypoint':'main.exe',
        'deployment_type':'other'}).status_code==400
    for path in (r'..\main.exe',r'outside:drive.exe'):
        assert client.post('/api/nas/scan',json={'folder':'games','entrypoint':path,
            'deployment_type':'portable'}).status_code==400


def test_scanner_supports_nested_portable_executable(monkeypatch):
    data={'Engine\\Binaries\\game.exe':b'portable game', 'data\\pack.bin':b'assets'}
    dirs={'': [('Engine',True),('data',True)], 'Engine': [('Binaries',True)],
          'Engine\\Binaries':[('game.exe',False)], 'data':[('pack.bin',False)]}
    class Stat:
        st_file_attributes=0
        def __init__(self,size): self.st_size=size
    class Entry:
        def __init__(self,name,dir_,relative): self.name=name;self.dir=dir_;self.relative=relative
        def stat(self,follow_symlinks=False): return Stat(0 if self.dir else len(data[self.relative]))
        def is_symlink(self): return False
        def is_dir(self,follow_symlinks=False): return self.dir
        def is_file(self,follow_symlinks=False): return not self.dir
    class SMB:
        def register_session(self,*a,**k): pass
        def scandir(self,path):
            rel=path.split('\\games\\Folder')[-1].strip('\\')
            return [Entry(n,is_dir,rel+('\\' if rel else '')+n) for n,is_dir in dirs[rel]]
        def open_file(self,path,mode='rb'):
            rel=path.split('\\games\\Folder\\')[-1]
            return io.BytesIO(data[rel])
    monkeypatch.setattr(nas_library,'smbclient',SMB())
    m=nas_library.scan_package(r'\\nas\games','Folder',r'Engine\Binaries\game.exe','','',
                               deployment_type='portable')
    assert m['deployment_type']=='portable' and m['file_count']==2
    assert m['files'][0]['sha256']==hashlib.sha256(b'assets').hexdigest()
    assert m['digest'] != nas_library.scan_package(r'\\nas\games','Folder',r'Engine\Binaries\game.exe','','',
                                                 deployment_type='portable')['files'][0]['sha256']
    try:
        nas_library.scan_package(r'\\nas\games','Folder','game.exe','','',deployment_type='installer')
    except ValueError:
        pass
    else:
        raise AssertionError('Installer cannot select a nested portable EXE')


def test_portable_client_and_signed_release_contract():
    agent=(ROOT/'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
    copy=(ROOT/'agent/HomeWatchAgent/PortableGameDeployment.cs').read_text()
    assert 'if (item.DeploymentType == "portable")' in agent
    assert 'RegisterInstalledGameAsync(item, exe, explicitRoot: root,' in agent
    assert 'HomeWatchInstallerBroker' not in copy
    assert 'Directory.Move(stage, final)' in copy and 'Directory.Move(final, backup)' in copy
    assert 'IncrementalHash.CreateHash' in copy
    assert 'MatchesManifestAsync' in copy
    assert 'UpdateAvailable' in agent
    workflow=(ROOT/'.github/workflows/release.yml').read_text()
    assert "$version = '0.6.4'" in workflow
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in workflow
    assert 'HomeWatchInstallerBroker-win-x64.exe' in workflow
