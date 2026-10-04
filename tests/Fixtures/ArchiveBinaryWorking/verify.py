"""Actual Binary GUI/CLI outputs; independent Python stdlib full-byte/ZIP reader."""
import hashlib
import io
import json
import pathlib
import struct
import sys
import zipfile
import zlib

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def zipped(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        assert archive.testzip() is None
        names = archive.namelist()
        assert len(names) == len(set(names))
        assert all(not pathlib.PurePosixPath(name).is_absolute() and '..' not in pathlib.PurePosixPath(name).parts for name in names)
        return {name: archive.read(name) for name in names if not name.endswith('/')}

def workspace(path, expected):
    path = pathlib.Path(path)
    doc = json.loads(path.read_text(encoding='utf-8-sig'))
    assert doc['formatVersion'] == 5
    for project in doc['entries']:
        for side in ('left', 'base', 'right'):
            source = project.get(side + 'ArchiveInput')
            if source is None:
                continue
            root = path.parent / source['rootPath']
            original = root.read_bytes()
            assert sha(original) == source['rootSha256']
            entries = zipped(original)
            for chain in source['entryChain']:
                entries = zipped(entries[chain])
            assert source['leafEntry'] in entries
            for copy in source.get('workingTexts') or []:
                assert copy['kind'] == 'Binary' and copy['encodingName'] == '' and not copy['hasBom']
                assert not pathlib.Path(copy['snapshotPath']).is_absolute()
                data = (path.parent / copy['snapshotPath']).read_bytes()
                assert len(data) <= 16 * 1024 * 1024 and sha(data) == copy['sha256']
                assert data == expected
    assert 'password' not in json.dumps(doc).lower()
    return doc

gui = pathlib.Path(sys.argv[1])
facts_path = list(gui.glob('fixtures/*/binary-working/binary-working-facts.json'))
assert len(facts_path) == 1
facts = json.loads(facts_path[0].read_text())
a, b = (bytes.fromhex(facts[name]) for name in ('originalLeftHex', 'originalRightHex'))
assert a == bytes.fromhex('00 01 02 03 FF 80')
assert b == bytes.fromhex('00 09 02 04 FF 81')
assert bytes.fromhex(facts['lateHex']) == bytes.fromhex('10 01 02 03 FF 80')
assert bytes.fromhex(facts['adoptionHex']) == bytes.fromhex('30 01 02 03 FF 80')
for index, root in enumerate(facts['roots']):
    original = pathlib.Path(root).read_bytes()
    assert sha(original) == facts['rootSha256'][index]
    entries = zipped(zipped(original)['inner.zip'])
    assert entries == {'leaf.bin': (a, b)[index], 'empty.bin': b'', 'ascii.txt': b'original text\n'}
assert pathlib.Path(facts['asset']).read_bytes() == a
assert pathlib.Path(facts['external']).read_bytes() == a
folder = facts_path[0].parent
assert (folder / 'same-bytes-different-root.zip').read_bytes() == pathlib.Path(facts['roots'][0]).read_bytes()
for name, count in (('budget-documents.zip', 257), ('budget-bytes.zip', 9)):
    path = folder / name
    if 'ordinaryLeftDirty' in facts:
        assert path.is_file(), name
    if path.exists():
        assert zipped(path.read_bytes()) == {str(index) + '.bin': b'' for index in range(count)}
outputs = {'ordinary-left.bin': a, 'ordinary-right.bin': b, 'ordinary-saved.bin': bytes([0x10])+a[1:],
           'pending-refused.bin': b'\x55', 'guarded.bin': b'\x66', 'physical-readonly.bin': b'\x44',
           'output-link.bin': b'\x44', 'missing-zero.bin': b'', 'readonly-export.bin': bytes([0x60])+a[1:]}
outputs.update({'external-left.bin': a, 'late-edit.bin': bytes.fromhex(facts['lateHex']), 'adoption-failed.bin': bytes.fromhex(facts['adoptionHex'])})
if 'ordinaryLeftDirty' in facts:
    assert {path.name for path in folder.glob('*.bin')} == set(outputs)
    assert {path.name for path in folder.glob('*.zip')} == {'left.zip', 'right.zip', 'same-bytes-different-root.zip', 'budget-documents.zip', 'budget-bytes.zip', 'working-package.zip'}
for name, expected in outputs.items():
    path = folder / name
    if path.exists():
        assert path.read_bytes() == expected, name
for path, hex_key in (('late', 'lateHex'), ('adoption', 'adoptionHex')):
    assert pathlib.Path(facts[path]).read_bytes() == bytes.fromhex(facts[hex_key])
if 'ordinaryLeftDirty' in facts:
    assert facts['ordinaryLeftDirty'] and facts['ordinaryRightDirty'] and facts['ordinaryCanUndo']
    assert facts['ordinaryLeftPath'] == facts['late']
    assert bytes.fromhex(facts['ordinaryDisplayHex'])[0] == 0x30 and pathlib.Path(facts['late']).read_bytes()[0] == 0x10
    assert any('64 MiB' in reason and '256' in reason for reason in facts['refusals'])
    assert any('128 MiB' in reason for reason in facts['refusals'])
workspace(facts['workspace'], a)
package = zipped(pathlib.Path(facts['package']).read_bytes())
packed = json.loads(package['project.json'])
assert packed['formatVersion'] == 5
for project in packed['entries']:
    for side in ('left', 'right'):
        source = project[side + 'ArchiveInput']
        assert sha(package[source['rootPath']]) == source['rootSha256']
        for copy in source.get('workingTexts') or []:
            assert package[copy['snapshotPath']] == a and sha(a) == copy['sha256']
assert not any(name.endswith('.html') or name.endswith('.patch') for name in package)
pngs = list(gui.glob('binary-working-*.png'))
assert len(pngs) == 2
for path in pngs:
    data = path.read_bytes()
    assert data[:8] == b'\x89PNG\r\n\x1a\n'
    offset, image_data = 8, b''
    while offset < len(data):
        size = struct.unpack('>I', data[offset:offset+4])[0]
        kind, payload = data[offset+4:offset+8], data[offset+8:offset+8+size]
        assert zlib.crc32(kind+payload) & 0xffffffff == struct.unpack('>I', data[offset+8+size:offset+12+size])[0]
        if kind == b'IHDR':
            width, height, depth, color = struct.unpack('>IIBB', payload[:10]); assert depth == 8 and color in (2, 6)
        if kind == b'IDAT': image_data += payload
        offset += size+12
    assert len(zlib.decompress(image_data)) == height * (1 + width * (4 if color == 6 else 3))
if len(sys.argv) > 2:
    cli = pathlib.Path(sys.argv[2])
    for name in ('copied.json', 'extracted/project.json', 'reloaded.json'):
        workspace(cli / name, a)
    extracted = cli / 'extracted'
    assert {path.relative_to(extracted).as_posix(): path.read_bytes() for path in extracted.rglob('*') if path.is_file()} == package
    assert zipped((cli / 'cli-package.zip').read_bytes()) == package
print(json.dumps({'facts': str(facts_path[0]), 'workspace': facts['workspace'], 'package': facts['package'], 'pngCount': len(pngs),
                  'binaryOutputs': {name: sha(expected) for name, expected in outputs.items() if (folder / name).exists()},
                  'budgetZipSha256': {path.name: sha(path.read_bytes()) for path in folder.glob('budget-*.zip')}, 'verified': True}))
