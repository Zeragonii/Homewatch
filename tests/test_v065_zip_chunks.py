"""Fast portable transfer: chunk packaging on NAS, SHA-256 at publication,
and client ZIP streaming. No privileged installer-broker changes.
"""
import hashlib
import json
import os
from pathlib import Path
import zipfile

from server.app import nas_library


def test_grouping_maintains_file_order_and_allows_oversized_files():
    files = [{'path': f'{i}.bin', 'size': n} for i, n in enumerate([3, 4, 9, 1, 17, 2])]
    groups = list(nas_library._chunk_groups(files, max_bytes=10))
    assert [start for start, _ in groups] == [0, 2, 4, 5]
    assert [[f['path'] for f in batch] for _, batch in groups] == [
        ['0.bin', '1.bin'], ['2.bin', '3.bin'], ['4.bin'], ['5.bin']]


class LocalSMB:
    """Only a test stand-in for smbclient's file, directory, stat and rename APIs."""
    def __init__(self, base): self.base = base
    def path(self, raw):
        parts = raw.lstrip('\\').split('\\')
        assert parts[:2] == ['nas', 'share'], parts
        return self.base.joinpath(*parts[2:])
    def open_file(self, path, mode='rb'):
        physical = self.path(path)
        if 'w' in mode: physical.parent.mkdir(parents=True, exist_ok=True)
        return open(physical, mode)
    def stat(self, path): return os.stat(self.path(path))
    def makedirs(self, path, exist_ok=False): self.path(path).mkdir(parents=True, exist_ok=exist_ok)
    def rename(self, source, dest): self.path(source).rename(self.path(dest))


def test_builds_uncompressed_chunks_and_reuses_immutable_cache(tmp_path, monkeypatch):
    data = {
        'Game.exe': b'MZ-game-bin',
        'Engine\\Data\\part1.bin': b'abcdef',
        'Engine\\Data\\part2.bin': b'0123456789',
        'empty.dat': b'',
    }
    for name, contents in data.items():
        target = tmp_path / 'FamilyGame' / Path(name.replace('\\', '/'))
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(contents)
    files = [
        {'path': key, 'size': len(content), 'sha256': hashlib.sha256(content).hexdigest()}
        for key, content in sorted(data.items())
    ]
    manifest = {'deployment_type': 'portable', 'digest': 'b'*64,
                'files': files, 'total_bytes': sum(f['size'] for f in files)}
    monkeypatch.setattr(nas_library, 'smbclient', LocalSMB(tmp_path))
    monkeypatch.setattr(nas_library, 'connect', lambda *args: None)
    progress = []
    transport = nas_library.build_portable_chunks(r'\\nas\share', 'FamilyGame', '', '',
                                                   manifest, lambda *x: progress.append(x),
                                                   chunk_bytes=12)
    assert transport['transport'] == 'zip-chunks-v1'
    assert len(transport['chunks']) >= 2
    assert transport['archive_root'].endswith('b'*64)
    assert progress[-1][1] == manifest['total_bytes']
    recovered = {}
    for chunk in transport['chunks']:
        zip_path = tmp_path / '.homewatch-packages' / ('b'*64) / chunk['name']
        assert zip_path.is_file()
        with zipfile.ZipFile(zip_path) as archive:
            assert all(info.compress_type == zipfile.ZIP_STORED for info in archive.infolist())
            recovered.update({name.replace('/', '\\'): archive.read(name)
                              for name in archive.namelist()})
    assert recovered == data
    ready_file = tmp_path / '.homewatch-packages' / ('b'*64) / 'ready.json'
    assert json.loads(ready_file.read_text())['digest'] == 'b'*64
    # Cached rescans reuse the same ZIPs. Changing the source file afterward
    # does not mutate a published package revision.
    (tmp_path/'FamilyGame'/'Game.exe').write_bytes(b'changed')
    assert nas_library.build_portable_chunks(r'\\nas\share', 'FamilyGame', '', '', manifest,
                                             chunk_bytes=12) == transport


def test_rejects_source_changed_between_scan_and_packaging(tmp_path, monkeypatch):
    (tmp_path/'Game').mkdir()
    (tmp_path/'Game'/'game.exe').write_bytes(b'changed')
    f = {'path': 'game.exe', 'size': 7, 'sha256': hashlib.sha256(b'initial').hexdigest()}
    m = {'deployment_type': 'portable', 'digest': 'c'*64, 'files': [f]}
    monkeypatch.setattr(nas_library, 'smbclient', LocalSMB(tmp_path))
    monkeypatch.setattr(nas_library, 'connect', lambda *args: None)
    try:
        nas_library.build_portable_chunks(r'\\nas\share', 'Game', '', '', m)
    except ValueError as error:
        assert 'changed during packaging' in str(error)
    else:
        raise AssertionError('Publisher must detect content changed since scanning')
    assert not (tmp_path/'.homewatch-packages'/('c'*64)).exists()


def test_windows_client_preserves_legacy_and_checks_executable():
    root = Path(__file__).resolve().parents[1]
    code = (root/'agent/HomeWatchAgent/PortableGameDeployment.cs').read_text()
    models = (root/'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
    assert 'ZipArchive(remote, ZipArchiveMode.Read' in code
    assert 'entry.CompressedLength != entry.Length' in code
    assert 'await input.ReadAsync(buffer, token)' in code
    assert 'Executable SHA-256 mismatch in ZIP' in code
    assert 'item.PackageRoot.TrimEnd' in code  # legacy compatibility
    assert 'DeployChunksAsync' in code
    assert 'archive_root' in models and 'file_count' in models
    assert 'HomeWatchInstallerBroker' not in code


def test_scan_job_generates_chunks_before_becoming_publishable(monkeypatch):
    from server.app.main import run_nas_scan, nas_jobs, nas_job_lock
    import uuid
    jid = uuid.uuid4().hex
    with nas_job_lock:
        nas_jobs[jid] = {'status': 'hashing', 'phase': 'enumerating',
                         'bytes_hashed': 0, 'total_bytes': 0, 'current_file': ''}
    seen = []
    def fake_scan(root, folder, entrypoint, user, password, progress, deployment_type):
        seen.append(('scan', deployment_type))
        progress('game.exe', 5, 5)
        return {'deployment_type': 'portable', 'digest': 'd'*64,
                'entrypoint': 'game.exe', 'files': [
                    {'path': 'game.exe', 'size': 5, 'sha256': 'e'*64}],
                'total_bytes': 5, 'file_count': 1}
    def fake_bundle(root, folder, user, password, manifest, progress):
        seen.append(('bundle', manifest['digest']))
        progress('Packaging game.exe', 5, 5)
        return {'transport': 'zip-chunks-v1',
                'archive_root': root + '\\.homewatch-packages\\' + manifest['digest'],
                'chunks': [{'name': 'part-0001.zip', 'file_start': 0,
                            'file_count': 1, 'uncompressed_bytes': 5}]}
    monkeypatch.setattr(nas_library, 'scan_package', fake_scan)
    monkeypatch.setattr(nas_library, 'build_portable_chunks', fake_bundle)
    run_nas_scan(jid, r'\\nas\share', 'Game', 'game.exe', '', '', deployment_type='portable')
    with nas_job_lock:
        job = nas_jobs.pop(jid)
    assert job['status'] == 'ready', job
    assert job['manifest']['transport'] == 'zip-chunks-v1'
    assert seen == [('scan', 'portable'), ('bundle', 'd'*64)]
