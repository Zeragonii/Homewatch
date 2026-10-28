"""Large prepared-game folders: no arbitrary 500-entry scan cap."""
import hashlib
import io

import pytest
from fastapi.testclient import TestClient
from server.app import nas_library
from server.app.main import app


def make_fake_smb(count):
    data = {'game.exe': b'portable-executable'}
    data.update({f'asset_{i:05d}.bin': (i % 251).to_bytes(1, 'little') for i in range(count)})

    class Stat:
        st_file_attributes = 0
        def __init__(self, length): self.st_size = length

    class Entry:
        def __init__(self, name): self.name = name
        def stat(self, follow_symlinks=False): return Stat(len(data[self.name]))
        def is_symlink(self): return False
        def is_dir(self, follow_symlinks=False): return False
        def is_file(self, follow_symlinks=False): return True

    class Smb:
        def register_session(self, *a, **kw): pass
        def scandir(self, path): return (Entry(key) for key in data)
        def open_file(self, path, mode='rb'):
            assert mode == 'rb'
            return io.BytesIO(data[path.rsplit('\\', 1)[-1]])

    return Smb(), data


def test_browser_pages_a_folder_with_more_than_500_entries(monkeypatch):
    smb, data = make_fake_smb(649)
    monkeypatch.setattr(nas_library, 'smbclient', smb)
    page1 = nas_library.list_directory_page(r'\\nas\games', 'Game', '', '')
    assert page1['total'] == 650 and len(page1['entries']) == 250
    assert page1['offset'] == 0 and page1['has_more']
    page2 = nas_library.list_directory_page(r'\\nas\games', 'Game', '', '', offset=250)
    page3 = nas_library.list_directory_page(r'\\nas\games', 'Game', '', '', offset=500)
    assert len(page2['entries']) == 250 and len(page3['entries']) == 150
    assert not page3['has_more']
    names = [e['name'] for page in (page1,page2,page3) for e in page['entries']]
    assert set(names) == set(data) and len(names) == len(set(names))
    for bad in (-1, 120001):
        with pytest.raises(ValueError):
            nas_library.list_directory_page(r'\\nas\games', 'Game', '', '', offset=bad)


def test_portable_can_hash_more_than_4000_files_in_one_folder(monkeypatch):
    smb, data = make_fake_smb(4400)
    monkeypatch.setattr(nas_library, 'smbclient', smb)
    progress = []
    manifest = nas_library.scan_package(r'\\nas\games', 'Game', 'game.exe', '', '',
        lambda name, done, total: progress.append((done,total)), deployment_type='portable')
    assert manifest['file_count'] == 4401
    assert manifest['total_bytes'] == sum(map(len, data.values()))
    assert len(manifest['files']) == 4401
    assert all(file['sha256'] == hashlib.sha256(data[file['path']]).hexdigest()
               for file in manifest['files'])
    assert progress[-1] == (manifest['total_bytes'],manifest['total_bytes'])
    with pytest.raises(ValueError, match='4,000 file limit'):
        nas_library.scan_package(r'\\nas\games', 'Game', 'game.exe', '', '',
                                 deployment_type='installer')


def test_admin_nas_browser_pagination_api(monkeypatch):
    smb, _ = make_fake_smb(600)
    monkeypatch.setattr(nas_library, 'smbclient', smb)
    client = TestClient(app)
    login = client.post('/api/login', json={'username':'admin','password':'testpass'})
    assert login.status_code == 200
    client.put('/api/nas/config', json={'root_unc':r'\\nas\games', 'username':'', 'password':''})
    first = client.get('/api/nas/browse?path=Game')
    assert first.status_code == 200, first.text
    assert first.json()['total'] == 601 and len(first.json()['entries']) == 250
    later = client.get('/api/nas/browse?path=Game&offset=500')
    assert later.status_code == 200, later.text
    assert len(later.json()['entries']) == 101 and later.json()['has_more'] is False
    assert client.get('/api/nas/browse?path=Game&offset=-5').status_code == 400


def test_portable_manifest_limit_matches_windows_deployer():
    from pathlib import Path
    source = (Path(__file__).parents[1] / 'agent/HomeWatchAgent/PortableGameDeployment.cs').read_text()
    assert 'MaxPortableFiles = 100_000' in source
    assert 'manifest.Files.Count is < 1 or > MaxPortableFiles' in source
    broker = (Path(__file__).parents[1] / 'agent/HomeWatchInstallerBroker/Program.cs').read_text()
    assert 'files.Count > 4000' in broker
