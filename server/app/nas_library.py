"""SMB-backed, read-only installer library. No Docker bind mount needed.

Never use client-supplied paths directly with smbclient. All paths here are
relative to the parent-configured share root; reparse points are disallowed.
"""
from __future__ import annotations

import hashlib
import json
import re
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from threading import Lock

try:
    import smbclient
except ImportError:  # local unit tests without production SMB dependency
    smbclient = None

# The privileged installer broker deliberately accepts at most 4,000 files.
# Ready-to-play games are copied without elevation and often have many more.
MAX_INSTALLER_FILES = 4000
MAX_PORTABLE_FILES = 100_000
MAX_DIRECTORY_ENTRIES = 120_000
HASH_WORKERS = 4
HASH_CHUNK_BYTES = 4 * 1024 * 1024
MAX_PACKAGE_BYTES = 300 * 1024**3
MAX_RELATIVE_LEN = 700


def validate_root(value: str) -> str:
    root = value.strip().replace('/', '\\').rstrip('\\')
    if not root.startswith('\\\\'):
        raise ValueError('Enter a UNC share such as \\\\192.168.1.250\\installers')
    parts = root[2:].split('\\')
    if len(parts) < 2 or any(not p or p in ('.', '..') or ':' in p for p in parts):
        raise ValueError('Invalid UNC share')
    if not re.fullmatch(r'[\w.\-]+', parts[0]) or parts[1].endswith('$'):
        raise ValueError('Invalid or administrative share')
    return root


def validate_relative(value: str, *, allow_empty: bool = True) -> str:
    s = value.replace('/', '\\').strip('\\')
    if not s:
        if allow_empty: return ''
        raise ValueError('Choose a folder or file')
    if len(s) > MAX_RELATIVE_LEN or any(ord(c) < 32 for c in s):
        raise ValueError('Invalid relative path')
    parts = s.split('\\')
    if any(x in ('', '.', '..') or x.endswith((' ', '.')) or
           any(c in x for c in '<>:"|?*') for x in parts):
        raise ValueError('Path traversal or invalid relative path')
    return '\\'.join(parts)


def resolve(root: str, relative: str) -> str:
    return validate_root(root) + ('\\' + validate_relative(relative) if relative else '')


def connect(root: str, username: str, password: str):
    if smbclient is None: raise RuntimeError("smbprotocol must be installed for SMB browsing")
    host = validate_root(root)[2:].split('\\')[0]
    if username:
        smbclient.register_session(host, username=username, password=password, connection_timeout=15)
    # Guest sessions are supported where the NAS permits them; no global credential defaults.


def list_directory(root: str, relative: str, username: str, password: str):
    connect(root, username, password)
    relative = validate_relative(relative)
    entries = []
    for entry in smbclient.scandir(resolve(root, relative)):
        if entry.name in ('.', '..'):
            continue
        st = entry.stat(follow_symlinks=False)
        # Windows reparse points/symlinks can escape the configured share root.
        if getattr(st, 'st_file_attributes', 0) & 0x400 or entry.is_symlink():
            continue
        is_dir = entry.is_dir(follow_symlinks=False)
        if not is_dir and not entry.is_file(follow_symlinks=False):
            continue
        child = relative + ('\\' if relative else '') + entry.name
        validate_relative(child)
        entries.append({'name': entry.name, 'path': child, 'is_dir': is_dir, 'size': 0 if is_dir else st.st_size})
        if len(entries) > MAX_DIRECTORY_ENTRIES:
            raise ValueError('Directory contains more than 120,000 entries; split the source folder')
    return sorted(entries, key=lambda e: (not e['is_dir'], e['name'].casefold()))


def list_directory_page(root: str, relative: str, username: str, password: str,
                        offset: int = 0, page_size: int = 250):
    """Page only the admin browser; never page the actual package scan."""
    if offset < 0 or offset > MAX_DIRECTORY_ENTRIES or not 1 <= page_size <= 500:
        raise ValueError('Invalid NAS browser page')
    entries = list_directory(root, relative, username, password)
    return {'entries': entries[offset:offset + page_size],
            'total': len(entries), 'offset': offset, 'page_size': page_size,
            'has_more': offset + page_size < len(entries)}


def scan_package(root: str, folder: str, entrypoint: str, username: str, password: str, on_progress=None, deployment_type='installer'):
    """Hashes every file. Progress callback(file, bytes_read, total_bytes) can update an admin job."""
    connect(root, username, password)
    folder = validate_relative(folder)
    entrypoint = validate_relative(entrypoint, allow_empty=False)
    if deployment_type not in ('installer', 'portable'):
        raise ValueError('Unknown deployment type')
    if not entrypoint.lower().endswith('.exe') or (deployment_type == 'installer' and '\\' in entrypoint):
        raise ValueError('Choose the setup EXE for an installer or the game EXE within the portable folder')
    files = []
    paths = [('', 0)]
    total_bytes = 0
    max_files = MAX_PORTABLE_FILES if deployment_type == 'portable' else MAX_INSTALLER_FILES
    visited = 0
    while paths:
        current, depth = paths.pop()
        if depth > 12:
            raise ValueError('Folder nesting exceeds 12 levels')
        for e in list_directory(root, folder + ('\\' + current if current else ''), username, password):
            visited += 1
            if visited > MAX_DIRECTORY_ENTRIES:
                raise ValueError('Package contains too many files or directories (120,000 maximum)')
            local_rel = current + ('\\' if current else '') + e['name']
            if e['is_dir']:
                paths.append((local_rel, depth + 1))
            else:
                total_bytes += e['size']
                if total_bytes > MAX_PACKAGE_BYTES:
                    raise ValueError('Package exceeds 300 GB limit')
                files.append({'path': local_rel, 'size': e['size']})
                if len(files) > max_files:
                    raise ValueError(f'Package exceeds {max_files:,} file limit for {deployment_type} deployments')
    files.sort(key=lambda f: f['path'].casefold())
    if len({x['path'].casefold() for x in files}) != len(files):
        raise ValueError('Game files collide on case-insensitive Windows filesystems')
    if not any(f['path'].lower() == entrypoint.lower() for f in files):
        raise ValueError('The selected setup executable is not in this folder')
    # Enumerate first for an exact byte total, then hash each file ONCE over
    # SMB. Four bounded workers help especially with the many tiny files common
    # in games, without creating tens of thousands of queued futures.
    bytes_hashed = 0
    progress_lock = Lock()

    def hash_file(f):
        nonlocal bytes_hashed
        source = resolve(root, folder + ('\\' if folder else '') + f['path'])
        h = hashlib.sha256()
        copied_file = 0
        with smbclient.open_file(source, mode='rb') as handle:
            while chunk := handle.read(HASH_CHUNK_BYTES):
                copied_file += len(chunk)
                if copied_file > f['size']:
                    raise ValueError('NAS file changed size during hashing: ' + f['path'])
                h.update(chunk)
                with progress_lock:
                    bytes_hashed += len(chunk)
                    if on_progress:
                        on_progress(f['path'], bytes_hashed, total_bytes)
        if copied_file != f['size']:
            raise ValueError('NAS file changed size during hashing: ' + f['path'])
        with progress_lock:
            if on_progress:
                on_progress(f['path'], bytes_hashed, total_bytes)
        return h.hexdigest()

    # Keep at most twice the worker count queued; executor.map would enqueue
    # every file and use excessive memory for very large portable manifests.
    with ThreadPoolExecutor(max_workers=HASH_WORKERS, thread_name_prefix='nas-hash') as pool:
        files_iter = iter(enumerate(files))
        pending = {}
        for _ in range(HASH_WORKERS * 2):
            item = next(files_iter, None)
            if item is None:
                break
            index, f = item
            pending[pool.submit(hash_file, f)] = index
        while pending:
            completed, _ = wait(pending, return_when=FIRST_COMPLETED)
            for future in completed:
                index = pending.pop(future)
                files[index]['sha256'] = future.result()
                item = next(files_iter, None)
                if item is not None:
                    next_index, next_file = item
                    pending[pool.submit(hash_file, next_file)] = next_index
    count = len(files)
    manifest = {'entrypoint': entrypoint, 'files': files, 'total_bytes': total_bytes, 'file_count': count}
    fingerprint_input = {'entrypoint': entrypoint, 'files': files}
    if deployment_type == 'portable':
        manifest['deployment_type'] = 'portable'
        fingerprint_input['deployment_type'] = 'portable'
    manifest['digest'] = hashlib.sha256(json.dumps(fingerprint_input, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return manifest
