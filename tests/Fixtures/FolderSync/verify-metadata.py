"""Windows NTFSの拒否後保持を、固定literalと実handleで独立照合する。"""
import ctypes as c
from ctypes import wintypes as w
import datetime, hashlib, json, os, pathlib, sys


def main():
    assert os.name == 'nt', 'Windows NTFS専用reader'
    source_path = pathlib.Path(sys.argv[1]).resolve()
    assert source_path.name == 'folder-windows-metadata-observations.json'
    report = json.loads(source_path.read_text(encoding='utf-8'))
    expected_ids = ['metadata-retained-zero', 'metadata-query-zero', 'metadata-native-zero',
                    'metadata-source-creation-change', 'metadata-target-creation-change']
    assert report['complete'] and [r['id'] for r in report['cases']] == expected_ids
    diagnostics_path = source_path.with_name('folder-windows-metadata-diagnostic-observations.json')
    diagnostics = json.loads(diagnostics_path.read_text(encoding='utf-8'))
    diagnostic_ids = ['metadata-execute-cleanup-diagnostic', 'metadata-prepare-cancel-close-diagnostic']
    assert diagnostics['complete'] and [r['id'] for r in diagnostics['cases']] == diagnostic_ids
    all_rows = report['cases'] + diagnostics['cases']

    class FT(c.Structure):
        _fields_ = [('lo', w.DWORD), ('hi', w.DWORD)]

    class Info(c.Structure):
        _fields_ = [('attrs', w.DWORD), ('creation', FT), ('access', FT), ('write', FT),
                    ('volume', w.DWORD), ('sizehi', w.DWORD), ('sizelo', w.DWORD),
                    ('links', w.DWORD), ('idhi', w.DWORD), ('idlo', w.DWORD)]

    k = c.WinDLL('kernel32', use_last_error=True)
    k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, c.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
    k.CreateFileW.restype = w.HANDLE
    k.GetFileInformationByHandle.argtypes = [w.HANDLE, c.POINTER(Info)]
    k.GetFileInformationByHandle.restype = w.BOOL
    k.CloseHandle.argtypes = [w.HANDLE]
    k.CloseHandle.restype = w.BOOL
    closes = []

    def facts(path):
        handle = k.CreateFileW(str(path), 0x80, 7, None, 3, 0x02200000, None)
        assert handle != c.c_void_p(-1).value, ('open', str(path), c.get_last_error())
        try:
            info = Info()
            assert k.GetFileInformationByHandle(handle, c.byref(info)), ('query', str(path), c.get_last_error())
            return dict(creation=(info.creation.hi << 32) | info.creation.lo,
                        write=(info.write.hi << 32) | info.write.lo, attrs=info.attrs)
        finally:
            ok = bool(k.CloseHandle(handle))
            closes.append(dict(path=str(path), ok=ok, error=0 if ok else c.get_last_error()))
            assert ok, closes[-1]

    base_time = datetime.datetime(1601, 1, 1, tzinfo=datetime.timezone.utc)
    fixed = datetime.datetime(2002, 3, 4, 5, 6, 7, tzinfo=datetime.timezone.utc)
    delta = fixed - base_time
    fixed_ticks = (delta.days * 86400 + delta.seconds) * 10_000_000
    rows = []
    for index, row in enumerate(all_rows):
        changed = row.get('changedSide', -1)
        assert not row['mutation'] and row['published'] == 0
        if index < 5:
            assert changed == (-1 if index < 3 else index - 3)
            assert not row['succeeded']
            assert row['confirmed'] == (index >= 3) and row['planPresent'] == (index >= 3)
            assert row['resultPresent'] == (index >= 3) and row['usesWindowsMetadata'] == (index >= 3)
        else:
            marker = '検証: 未公開一時ファイルの清掃失敗' if index == 5 else '検証: metadataハンドル終了失敗'
            assert row['injectedDiagnostic'] and row['marker'] == marker and marker in row['status']
            assert row['confirmed'] == row['resultPresent'] == (index == 5)
            assert marker in row['summary'] if index == 5 else '中止' in row['status']
        inspected = []
        for side, key in [(0, 'Source'), (1, 'Destination')]:
            leaf = pathlib.Path(row['source' if side == 0 else 'destination'])
            root = leaf.parent
            assert root.parent.parent == source_path.parent and root.parent.name == row['id']
            original = {r['path']: r for r in row['before' + key]}
            observed = {r['path']: r for r in row['after' + key]}
            assert original == observed
            literals = {'a.bin': bytes([0x11 if side == 0 else 0x22]),
                        'b.bin': bytes([0x33 if side == 0 else 0x44]), 'same.bin': b'\x00',
                        'tree/leaf.bin': bytes([0x55 if side == 0 else 0x66])}
            entries = {p.relative_to(root).as_posix(): p for p in root.rglob('*')}
            assert set(entries) == set(literals) | {'tree', 'empty'}
            for name, path in entries.items():
                assert not path.is_symlink() and not (path.lstat().st_file_attributes & 0x400)
                actual = facts(path)
                snapshot = observed[name]
                assert actual['write'] == fixed_ticks == snapshot['mtime']
                assert actual['attrs'] == snapshot['attributes']
                if name in literals:
                    data = path.read_bytes()
                    assert data == literals[name]
                    assert not snapshot['directory'] and len(data) == snapshot['size']
                    assert hashlib.sha256(data).hexdigest().upper() == snapshot['sha256']
                else:
                    assert path.is_dir() and snapshot['directory'] and snapshot['size'] == 0
            actual = facts(leaf)
            prefix = 'source' if side == 0 else 'target'
            expected_creation = fixed_ticks + 10_000_000 if changed == side else int(row[prefix + 'CreationBefore'])
            assert actual['creation'] == expected_creation == int(row[prefix + 'CreationAfter'])
            inspected.append(dict(side=side, path=str(leaf), creation=str(actual['creation']), entries=len(entries)))
        rows.append(dict(id=row['id'], allPassed=True, sides=inspected))
    assert len(closes) == 98 and all(item['ok'] for item in closes)
    proof = dict(pid=os.getpid(), utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                 input=str(source_path), inputSha256=hashlib.sha256(source_path.read_bytes()).hexdigest().upper(),
                 diagnosticsInput=str(diagnostics_path), diagnosticsInputSha256=hashlib.sha256(diagnostics_path.read_bytes()).hexdigest().upper(),
                 readerSha256=hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest().upper(),
                 cases=rows, closes=closes, allPassed=True)
    pathlib.Path(sys.argv[2]).write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(pid=proof['pid'], allPassed=True, cases=len(rows), closes=len(closes))))
    return 0


if __name__ == '__main__':
    sys.exit(main())
