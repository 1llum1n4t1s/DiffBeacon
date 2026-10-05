"""Actual Main local NTFS copy: self-authored literals and independent native oracle."""
import ctypes as c
from ctypes import wintypes as w
import datetime
import hashlib
import json
import os
from pathlib import Path
import struct
import sys

DATA = bytes((i * 17 + 3) % 256 for i in range(5057))
ADS = bytes((i * 19 + 7) % 256 for i in range(431))
OLD_DATA, OLD_ADS = b'old-target-data', b'old-target-stream'
SOURCE_EA = [(b'ALPHA', 128, b'alpha-one'), (b'BETA', 0, b'source-two')]
OLD_EA = [(b'OLD', 0, b'target-only'), (b'BETA', 0, b'stale-value')]
CREATION, WRITE = 126_593_463_670_000_000, 126_594_463_670_000_000
OLD_CREATION, OLD_WRITE = 129_493_463_670_000_000, 129_494_463_670_000_000
ORDINARY = 0x31a7 & ~0x80
CLOSES, FREES = [], []


class FT(c.Structure):
    _fields_ = [('lo', w.DWORD), ('hi', w.DWORD)]


class Info(c.Structure):
    _fields_ = [('attrs', w.DWORD), ('creation', FT), ('access', FT), ('write', FT),
               ('volume', w.DWORD), ('sizehi', w.DWORD), ('sizelo', w.DWORD),
               ('links', w.DWORD), ('idhi', w.DWORD), ('idlo', w.DWORD)]


class IO(c.Structure):
    _fields_ = [('status', c.c_void_p), ('information', c.c_size_t)]


class StreamInfo(c.Structure):
    _fields_ = [('size', c.c_longlong), ('name', w.WCHAR * 296)]


def require(ok, action):
    if not ok:
        raise OSError(c.get_last_error(), action)


def close(handle, label):
    ok = bool(k.CloseHandle(handle))
    CLOSES.append(dict(label=label, ok=ok, error=0 if ok else c.get_last_error()))
    require(ok, 'checked CloseHandle: ' + label)


def opened(path, access=0x20088, directory=False):
    h = k.CreateFileW(str(path), access, 7, None, 3, 0x00200000 | (0x02000000 if directory else 0), None)
    require(h != c.c_void_p(-1).value, 'CreateFileW: ' + str(path))
    return h


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def ticks(value):
    return value.lo + (value.hi << 32)


def sid(raw, offset):
    assert offset >= 20 and offset + 8 <= len(raw) and raw[offset] == 1
    count = raw[offset + 1]
    assert count <= 15 and offset + 8 + count * 4 <= len(raw)
    value = raw[offset:offset + 8 + count * 4]
    text = 'S-1-' + str(int.from_bytes(value[2:8], 'big'))
    text += ''.join('-' + str(x) for x in struct.unpack_from('<' + 'I' * count, value, 8))
    return value, text


def security(raw):
    revision, reserved, control, owner, group, sacl, dacl = struct.unpack_from('<BBHIIII', raw)
    assert revision == 1 and control & 0x8000 and sacl == 0
    ob, ot = sid(raw, owner)
    gb, gt = sid(raw, group)
    aces, acl_revision = [], 0
    kind = 0 if not control & 4 else 1 if not dacl else 2
    if dacl:
        acl_revision, _, size, count, _ = struct.unpack_from('<BBHHH', raw, dacl)
        assert acl_revision in (2, 4) and dacl + size <= len(raw)
        offset = dacl + 8
        for _ in range(count):
            typ, flags, length = struct.unpack_from('<BBH', raw, offset)
            assert length >= 4 and length % 4 == 0 and offset + length <= dacl + size
            aces.append(raw[offset:offset + length])
            offset += length
    canonical = struct.pack('<BBHIIBBHI', 1, 0, control & 0x150f, len(ob), len(gb), kind, acl_revision, 0, len(aces))
    canonical += ob + gb + b''.join(struct.pack('<I', len(v)) + v for v in aces)
    return dict(owner=ot, group=gt, control=control & 0x150f,
                aces=[v.hex().upper() for v in aces], canonical=canonical.hex().upper(), sha256=sha(canonical))


def ea_canonical(entries):
    entries = sorted(entries)
    assert len({v[0] for v in entries}) == len(entries)
    return struct.pack('<I', len(entries)) + b''.join(struct.pack('<BBH', flags, len(name), len(value)) + name + value
                                                   for name, flags, value in entries)


def ea_parse(raw):
    entries, offset = [], 0
    while raw:
        nxt, flags, nlen, vlen = struct.unpack_from('<IBBH', raw, offset)
        end = offset + 9 + nlen + vlen
        assert flags in (0, 128) and nlen and end <= len(raw) and raw[offset + 8 + nlen] == 0
        entries.append((raw[offset + 8:offset + 8 + nlen].upper(), flags, raw[offset + 9 + nlen:end]))
        if not nxt:
            assert end == len(raw)
            break
        assert nxt % 4 == 0 and nxt >= end - offset and offset + nxt + 8 <= len(raw)
        offset += nxt
    return sorted(entries)


def streams(path):
    info = StreamInfo()
    h = k.FindFirstStreamW(str(path), 0, c.byref(info), 0)
    require(h != c.c_void_p(-1).value, 'FindFirstStreamW')
    result = {}
    try:
        while True:
            name = info.name
            assert name not in result and len(result) < 8 and 0 <= info.size <= 65536
            data = (path if name == '::$DATA' else Path(str(path) + name)).read_bytes()
            assert len(data) == info.size
            result[name] = data.hex().upper()
            if not k.FindNextStreamW(h, c.byref(info)):
                assert c.get_last_error() == 38, 'stream enumeration must reach EOF'
                break
        return result
    finally:
        ok = bool(k.FindClose(h))
        CLOSES.append(dict(label='FindClose: ' + str(path), ok=ok, error=0 if ok else c.get_last_error()))
        require(ok, 'checked FindClose')


def facts(path):
    h = opened(path)
    try:
        info = Info()
        require(k.GetFileInformationByHandle(h, c.byref(info)), 'GetFileInformationByHandle')
        assert not info.attrs & (0x400 | 0x10), 'regular no-reparse leaf required'
        buffer, needed = c.create_string_buffer(65536), w.DWORD()
        require(a.GetKernelObjectSecurity(h, 7, buffer, len(buffer), c.byref(needed)), 'OwnerGroupDacl query')
        assert 20 <= needed.value <= len(buffer)
        sd = security(buffer.raw[:needed.value])
        io = IO()
        status = nt.NtQueryEaFile(h, c.byref(io), buffer, len(buffer), 0, None, 0, None, 1) & 0xffffffff
        if status in (0xc0000052, 0x80000012):
            assert io.information == 0
            entries = []
        else:
            assert status == 0 and io.information <= len(buffer), hex(status)
            entries = ea_parse(buffer.raw[:io.information])
        return dict(creation=str(ticks(info.creation)), write=str(ticks(info.write)), attrs=info.attrs,
                    size=info.sizelo + (info.sizehi << 32), fileIndex=str(info.idlo + (info.idhi << 32)),
                    volumeSerial=info.volume, security=sd,
                    ea=[dict(name=n.hex().upper(), flags=f, value=v.hex().upper()) for n, f, v in entries],
                    eaSha256=sha(ea_canonical(entries)), dataHex=path.read_bytes().hex().upper(),
                    adsHex=Path(str(path) + ':tag').read_bytes().hex().upper(), streams=streams(path))
    finally:
        close(h, str(path))


def set_security(path, sddl, directory=False):
    sd, length = c.c_void_p(), w.DWORD()
    require(a.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, c.byref(sd), c.byref(length)), 'SDDL parse')
    try:
        assert length.value <= 65536
        h = opened(path, 0x60080, directory)
        try:
            status = nt.NtSetSecurityObject(h, 0x80000004, sd) & 0xffffffff
            assert status == 0, ('set protected DACL', hex(status))
        finally:
            close(h, 'set DACL: ' + str(path))
    finally:
        ok = k.LocalFree(sd) is None
        FREES.append(dict(label='SDDL', ok=ok))
        require(ok, 'checked LocalFree')


def seed(path, old=False, entries=(), attrs=128):
    path.write_bytes(OLD_DATA if old else DATA)
    Path(str(path) + ':tag').write_bytes(OLD_ADS if old else ADS)
    if old:
        Path(str(path) + ':obsolete').write_bytes(b'old-target-only-stream')
    h = opened(path, 0x198)
    try:
        if entries:
            parts = []
            for name, flags, value in entries:
                part = struct.pack('<IBBH', 0, flags, len(name), len(value)) + name + b'\0' + value
                parts.append(part)
            raw = b''
            for i, part in enumerate(parts):
                if i != len(parts) - 1:
                    size = (len(part) + 3) & ~3
                    part = struct.pack('<I', size) + part[4:] + bytes(size - len(part))
                raw += part
            assert len(raw) <= 65536
            buffer, io = c.create_string_buffer(raw), IO()
            status = nt.NtSetEaFile(h, c.byref(io), buffer, len(raw)) & 0xffffffff
            assert status == 0, ('NtSetEaFile', hex(status))
        creation = OLD_CREATION if old else CREATION
        write = OLD_WRITE if old else WRITE
        ct, wt = FT(creation & 0xffffffff, creation >> 32), FT(write & 0xffffffff, write >> 32)
        require(k.SetFileTime(h, c.byref(ct), None, c.byref(wt)), 'SetFileTime')
    finally:
        close(h, 'seed: ' + str(path))
    require(k.SetFileAttributesW(str(path), attrs), 'final seed attributes')


def initialize():
    global k, a, nt
    k, a, nt = c.WinDLL('kernel32', use_last_error=True), c.WinDLL('advapi32', use_last_error=True), c.WinDLL('ntdll')
    signatures = [(k.CreateFileW, [w.LPCWSTR, w.DWORD, w.DWORD, c.c_void_p, w.DWORD, w.DWORD, w.HANDLE], w.HANDLE),
                  (k.CloseHandle, [w.HANDLE], w.BOOL), (k.LocalFree, [c.c_void_p], c.c_void_p),
                  (k.GetFileInformationByHandle, [w.HANDLE, c.POINTER(Info)], w.BOOL),
                  (k.SetFileTime, [w.HANDLE, c.POINTER(FT), c.POINTER(FT), c.POINTER(FT)], w.BOOL),
                  (k.SetFileAttributesW, [w.LPCWSTR, w.DWORD], w.BOOL),
                  (a.GetKernelObjectSecurity, [w.HANDLE, w.DWORD, c.c_void_p, w.DWORD, c.POINTER(w.DWORD)], w.BOOL),
                  (a.ConvertStringSecurityDescriptorToSecurityDescriptorW, [w.LPCWSTR, w.DWORD, c.POINTER(c.c_void_p), c.POINTER(w.DWORD)], w.BOOL),
                  (nt.NtSetSecurityObject, [w.HANDLE, w.DWORD, c.c_void_p], c.c_long),
                  (nt.NtSetEaFile, [w.HANDLE, c.POINTER(IO), c.c_void_p, w.DWORD], c.c_long),
                  (nt.NtQueryEaFile, [w.HANDLE, c.POINTER(IO), c.c_void_p, w.DWORD, c.c_ubyte, c.c_void_p, w.DWORD, c.c_void_p, c.c_ubyte], c.c_long),
                  (k.DeviceIoControl, [w.HANDLE, w.DWORD, c.c_void_p, w.DWORD, c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.c_void_p], w.BOOL)]
    signatures += [(k.FindFirstStreamW, [w.LPCWSTR, w.DWORD, c.POINTER(StreamInfo), w.DWORD], w.HANDLE),
                   (k.FindNextStreamW, [w.HANDLE, c.POINTER(StreamInfo)], w.BOOL), (k.FindClose, [w.HANDLE], w.BOOL),
                   (k.GetVolumeInformationW, [w.LPCWSTR, w.LPWSTR, w.DWORD, c.POINTER(w.DWORD), c.POINTER(w.DWORD), c.POINTER(w.DWORD), w.LPWSTR, w.DWORD], w.BOOL),
                   (k.GetCurrentProcess, [], w.HANDLE),
                   (k.GetProcessTimes, [w.HANDLE, c.POINTER(FT), c.POINTER(FT), c.POINTER(FT), c.POINTER(FT)], w.BOOL)]
    for function, args, result in signatures:
        function.argtypes, function.restype = args, result


def safe_root(root):
    assert root.name == 'windows-metadata-main' and root.is_dir()
    assert root.drive and not str(root).startswith('\\\\'), 'local drive required'
    filesystem = c.create_unicode_buffer(64)
    require(k.GetVolumeInformationW(root.anchor, None, 0, None, None, None, filesystem, len(filesystem)), 'volume filesystem')
    assert filesystem.value.upper() == 'NTFS', 'independently require NTFS'
    for ancestor in [root, *root.parents]:
        assert not ancestor.is_symlink() and not ancestor.lstat().st_file_attributes & 0x400
    for node in root.rglob('*'):
        assert not node.is_symlink() and not node.lstat().st_file_attributes & 0x400


def prepare(root):
    assert not (root / 'inputs.json').exists() and not (root / 'cases').exists()
    rows, skips, capabilities = [], [], []
    for compressed in (False, True):
        for overwrite in (False, True):
            for index, attrs in enumerate((128, 2, 1, 32)):
                id = ('compressed' if compressed else 'plain') + ('-overwrite-' if overwrite else '-fresh-') + str(attrs)
                location = root / 'cases' / id
                left, right = location / 'left', location / 'right'
                left.mkdir(parents=True)
                right.mkdir()
                source, target = left / 'payload.bin', right / 'payload.bin'
                if compressed:
                    h = opened(right, 0xc0000000, True)
                    try:
                        mode, returned = w.WORD(1), w.DWORD()
                        ok = bool(k.DeviceIoControl(h, 0x9c040, c.byref(mode), 2, None, 0, c.byref(returned), None))
                        capabilities.append(dict(id=id, supported=ok, error=0 if ok else c.get_last_error()))
                    finally:
                        close(h, 'compressed parent')
                    if not ok:
                        skips.append(id + ': FSCTL_SET_COMPRESSION error ' + str(capabilities[-1]['error']))
                        continue
                # Owner/group remain the current filesystem defaults; only the DACL is changed.
                seed(source, entries=SOURCE_EA if (index + int(overwrite)) % 2 == 0 else (), attrs=attrs)
                baseline = facts(source)
                owner = baseline['security']['owner']
                set_security(source, 'D:P(A;;FA;;;' + owner + ')(A;;FR;;;SY)')
                # DACL publication itself dirties metadata and can add Archive at close.
                require(k.SetFileAttributesW(str(source), attrs), 'source attrs after security close')
                set_security(right, 'D:P(A;OICI;FA;;;' + owner + ')(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)', True)
                # A separately created child witnesses the parent's real inherited descriptor.
                witness = right / 'inheritance-witness.bin'
                seed(witness)
                inheritance = facts(witness)['security']
                old = None
                if overwrite:
                    seed(target, old=True, entries=OLD_EA)
                    set_security(target, 'D:P(D;;0x100;;;' + owner + ')(A;;FA;;;' + owner + ')(A;;FA;;;SY)(A;;FA;;;BA)')
                    old = facts(target)
                    try:
                        denied = opened(target, 0x100)
                    except OSError as ex:
                        assert ex.errno == 5, ex
                    else:
                        close(denied, 'unexpected WRITE_ATTRIBUTES open')
                        raise AssertionError('WRITE_ATTRIBUTES deny did not take effect')
                source_before = facts(source)
                assert int(source_before['creation']) == CREATION and int(source_before['write']) == WRITE
                assert source_before['attrs'] & ORDINARY == attrs & ORDINARY
                rows.append(dict(id=id, attrs=attrs, overwrite=overwrite, sourceEa=bool((index + int(overwrite)) % 2 == 0),
                                 sourceBefore=source_before, targetBefore=old, inheritedSecurity=inheritance))
    return dict(schema=1, root=str(root), cases=rows, skips=skips, compressionCapabilities=capabilities,
                dataSha256=sha(DATA), adsSha256=sha(ADS), allPassed=True)


def verify(root):
    manifest = json.loads((root / 'inputs.json').read_text(encoding='utf-8'))
    assert manifest['schema'] == 1 and manifest['root'] == str(root)
    assert manifest['dataSha256'] == sha(DATA) and manifest['adsSha256'] == sha(ADS)
    ids = {('compressed' if compressed else 'plain') + ('-overwrite-' if overwrite else '-fresh-') + str(attrs)
           for compressed in (False, True) for overwrite in (False, True) for attrs in (128, 2, 1, 32)}
    rows = []
    assert len({row['id'] for row in manifest['cases']}) == len(manifest['cases'])
    assert {row['id'] for row in manifest['cases']} | {s.split(':')[0] for s in manifest['skips']} == ids
    for row in manifest['cases']:
        assert row['attrs'] in (128, 2, 1, 32)
        expected_id = ('compressed' if row['id'].startswith('compressed-') else 'plain') + ('-overwrite-' if row['overwrite'] else '-fresh-') + str(row['attrs'])
        assert row['id'] == expected_id
        assert row['sourceEa'] == (([128, 2, 1, 32].index(row['attrs']) + int(row['overwrite'])) % 2 == 0)
        location = root / 'cases' / row['id']
        source, target = location / 'left' / 'payload.bin', location / 'right' / 'payload.bin'
        source_actual = facts(source)
        assert source_actual == row['sourceBefore'], 'source metadata or DATA/ADS changed'
        assert int(source_actual['creation']) == CREATION and int(source_actual['write']) == WRITE
        assert source_actual['dataHex'] == DATA.hex().upper() and source_actual['adsHex'] == ADS.hex().upper()
        actual = facts(target)
        assert actual['dataHex'] == DATA.hex().upper() and actual['adsHex'] == ADS.hex().upper()
        assert actual['streams'] == source_actual['streams'] == {'::$DATA': DATA.hex().upper(), ':tag:$DATA': ADS.hex().upper()}
        assert actual['attrs'] & ORDINARY == row['attrs'] & ORDINARY, ('post-last-close ordinary attrs', row['id'], actual['attrs'])
        assert int(actual['write']) == WRITE
        if row['overwrite']:
            assert int(actual['creation']) == OLD_CREATION
            expected_security = row['targetBefore']['security']
        else:
            assert int(actual['creation']) != CREATION and int(actual['creation']) != OLD_CREATION
            expected_security = row['inheritedSecurity']
        assert actual['security'] == expected_security and actual['security'] != row['sourceBefore']['security']
        expected_ea = SOURCE_EA if row['sourceEa'] else []
        assert actual['eaSha256'] == sha(ea_canonical(expected_ea))
        assert actual['ea'] == [dict(name=n.hex().upper(), flags=f, value=v.hex().upper()) for n, f, v in sorted(expected_ea)]
        app = json.loads((location / 'app.stdout.json').read_text(encoding='utf-8'))
        # JSON integers are parsed by Python as exact integers; FILETIME/identity must be decimal strings.
        execution = app.get('execution', app)
        assert execution['succeeded'] and not execution['cancelled'] and execution['published'] == 1 and execution['usesWindowsMetadata']
        entries = execution['entries']
        entry = next(value for value in entries if value['path'] == 'payload.bin')
        assert entry['published']
        metadata = entry['windowsMetadata']
        assert metadata['verified'] and metadata['observed'] is not None
        for name in ('expected', 'observed'):
            value = metadata[name]
            for field, actual_field in [('creationFileTime', 'creation'), ('lastWriteFileTime', 'write'), ('fileIndex', 'fileIndex')]:
                assert isinstance(value[field], str) and value[field].isdigit()
                assert int(value[field]) == int(actual[actual_field]), (name, field)
            assert value['size'] == len(DATA) and value['volumeSerial'] == actual['volumeSerial']
            assert value['attributes'] == actual['attrs'], (row['id'], name, 'full post-last-close attributes', value['attributes'], actual['attrs'])
            assert value['securityControl'] == actual['security']['control']
            assert value['securitySha256'] == actual['security']['sha256'] and value['eaSha256'] == actual['eaSha256']
        rows.append(dict(id=row['id'], actual=actual, allPassed=True))
    return dict(cases=rows, skipped=manifest['skips'], allPassed=True)


def main():
    assert os.name == 'nt', 'Windows only'
    action, directory = sys.argv[1:]
    assert action in ('prepare', 'verify')
    root = Path(directory).absolute()
    initialize()
    safe_root(root)
    birth, exit_time, kernel, user = FT(), FT(), FT(), FT()
    require(k.GetProcessTimes(k.GetCurrentProcess(), c.byref(birth), c.byref(exit_time), c.byref(kernel), c.byref(user)), 'reader birth')
    result = prepare(root) if action == 'prepare' else verify(root)
    result.update(pid=os.getpid(), utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  birthFileTime=str(ticks(birth)), readerSha256=sha(Path(__file__).read_bytes()), closes=CLOSES, localFrees=FREES)
    assert all(item['ok'] for item in CLOSES + FREES)
    with (root / ('inputs.json' if action == 'prepare' else 'independent.json')).open('x', encoding='utf-8') as output:
        json.dump(result, output, indent=2)
    print(json.dumps(dict(allPassed=True, cases=len(result['cases']), checkedCloses=len(CLOSES), checkedLocalFrees=len(FREES))))


if __name__ == '__main__':
    main()
