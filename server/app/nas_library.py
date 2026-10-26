"""SMB-backed, read-only installer library. No Docker bind mount needed.

Never use client-supplied paths directly with smbclient. All paths here are
relative to the parent-configured share root; reparse points are disallowed.
"""
from __future__ import annotations

import hashlib
import json
import re

try:
    import smbclient
except ImportError:  # local unit tests without production SMB dependency
    smbclient = None

MAX_FILES = 4000
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
    if any(x in ('', '.', '..') or ':' in x or x.endswith((' ', '.')) for x in parts):
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
        if len(entries) > 500:
            raise ValueError('Directory has too many entries (500 maximum)')
    return sorted(entries, key=lambda e: (not e['is_dir'], e['name'].casefold()))


def scan_package(root: str, folder: str, entrypoint: str, username: str, password: str, on_progress=None):
    """Hashes every file. Progress callback(file, bytes_read, total_bytes) can update an admin job."""
    connect(root, username, password)
    folder = validate_relative(folder)
    entrypoint = validate_relative(entrypoint, allow_empty=False)
    if '\\' in entrypoint or not entrypoint.lower().endswith('.exe'):
        raise ValueError('Select the setup EXE in the chosen package folder')
    files = []
    paths = [('', 0)]
    total_bytes = 0
    while paths:
        current, depth = paths.pop()
        if depth > 12:
            raise ValueError('Folder nesting exceeds 12 levels')
        for e in list_directory(root, folder + ('\\' + current if current else ''), username, password):
            local_rel = current + ('\\' if current else '') + e['name']
            if e['is_dir']:
                paths.append((local_rel, depth + 1))
            else:
                total_bytes += e['size']
                if total_bytes > MAX_PACKAGE_BYTES:
                    raise ValueError('Package exceeds 300 GB limit')
                files.append({'path': local_rel, 'size': e['size']})
                if len(files) > MAX_FILES:
                    raise ValueError('Package exceeds 4000 file limit')
    files.sort(key=lambda f: f['path'].casefold())
    if not any(f['path'].lower() == entrypoint.lower() for f in files):
        raise ValueError('The selected setup executable is not in this folder')
    # Calculate after enumeration so progress can show a meaningful total.
    count = 0
    read_bytes = 0
    for f in files:
        source = resolve(root, folder + ('\\' if folder else '') + f['path'])
        h = hashlib.sha256()
        copied_file = 0
        with smbclient.open_file(source, mode='rb') as handle:
            while chunk := handle.read(1024 * 1024):
                h.update(chunk)
                copied_file += len(chunk)
                read_bytes += len(chunk)
                if on_progress:
                    on_progress(f['path'], read_bytes, total_bytes)
        if read_bytes > MAX_PACKAGE_BYTES:
            raise ValueError('Package changed size during hashing')
        if copied_file != f['size']:
            raise ValueError('NAS file size changed during hashing')
        f['sha256'] = h.hexdigest()
        count += 1
        if on_progress:
            on_progress(f['path'], read_bytes, total_bytes)
    manifest = {'entrypoint': entrypoint, 'files': files, 'total_bytes': total_bytes, 'file_count': count}
    manifest['digest'] = hashlib.sha256(json.dumps({'entrypoint': entrypoint, 'files': files}, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    return manifest
