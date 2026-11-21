"""Self-authored fixed Binary bytes, verified independently with Python stdlib.

The three literals below are the oracle. No product comparer or product DTO is
imported. Nested ZIP entry sets, full bytes, CRC, snapshots and relative assets
are read independently; packaging and CLI project copies use the same oracle.
"""
import hashlib
import io
import json
import pathlib
import sys
import zipfile

original = [bytes.fromhex(x) for x in ('00010203FF80', '00090205FF82', '000A0204FF81')]
working = [original[0], bytes.fromhex('61090205FF82'), original[2]]
root = pathlib.Path(sys.argv[1])
paths = list(root.glob('fixtures/*/binary-threeway/facts.json'))
assert len(paths) == 1, paths
facts = json.loads(paths[0].read_text(encoding='utf-8-sig'))
folder = paths[0].parent
states = {s['name']: s for s in facts['snapshots']}
assert [bytes.fromhex(x) for x in states['ordinary-initial']['hex']] == original
assert states['ordinary-initial']['dirty'] == [False] * 3
assert [bytes.fromhex(x) for x in states['source-initial']['hex']] == original
assert states['source-initial']['readOnly'] == [False] * 3
for route in ['Archive', 'Image', 'Folder', 'Table']:
    state = states['candidate-' + route]
    assert [bytes.fromhex(x) for x in state['hex']] == original, route
    assert state['dirty'] == [False, True, False] and state['readOnly'] == [False] * 3, route
assert [bytes.fromhex(x) for x in states['middle-redo']['hex']] == [original[0], b'\x10' + original[1][1:], original[2]]
for name in ['shared-history', 'middle-saved', 'middle-late']:
    expected = [b'\x10' + original[0][1:], (b'\x21' if name == 'middle-late' else b'\x20') + original[1][1:], b'\x30' + original[2][1:]]
    assert [bytes.fromhex(x) for x in states[name]['hex']] == expected, name
assert states['middle-saved']['dirty'] == [True, False, True]
assert states['middle-late']['dirty'] == [True, True, True]
assert len(facts['refusals']) >= 15
for source in range(3):
    for destination in range(3):
        if source == destination:
            continue
        name = f'copy-{source}-{destination}'
        expected = original.copy()
        expected[destination] = original[source]
        assert [bytes.fromhex(x) for x in states[name]['hex']] == expected
        assert (folder / f'{name}.bin').read_bytes() == original[source]
for name, expected in [('main-middle.bin', b'\x20' + original[1][1:]), ('middle.bin', original[1]), ('middle-late.bin', b'\x20' + original[1][1:]), ('middle-pending-save.bin', b'\x21' + original[1][1:]), ('middle-detached.bin', working[1]), ('middle-readonly-copy.bin', working[1]), ('middle-missing.bin', b''), ('guarded.bin', b'\x66'), ('left.bin', original[0]), ('right.bin', original[2])]:
    assert (folder / name).read_bytes() == expected, name

def sha(b):
    return hashlib.sha256(b).hexdigest().upper()

def nested(b, expected):
    with zipfile.ZipFile(io.BytesIO(b)) as outer:
        assert outer.namelist() == ['inner.zip']
        inner = outer.read('inner.zip')  # CRC validated by zipfile
    with zipfile.ZipFile(io.BytesIO(inner)) as z:
        assert set(z.namelist()) == {'leaf.bin', 'empty.bin'}
        assert z.read('leaf.bin') == expected
        assert z.read('empty.bin') == b''

for side, name in enumerate(['left.zip', 'middle.zip', 'right.zip']):
    b = (folder / name).read_bytes()
    assert sha(b) == facts['rootSha256'][side]
    nested(b, original[side])

def workspace(path):
    doc = json.loads(path.read_text(encoding='utf-8-sig'))
    assert doc['formatVersion'] == 5
    project = doc['entries'][0]
    assert project['mode'] == 'Binary'
    assert project['baseDescription'] == 'middle binary'
    for side, field in enumerate(['leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput']):
        inp = project[field]
        assert inp['inheritedReadOnly'] is False
        root_path = pathlib.Path(inp['rootPath'])
        if not root_path.is_absolute():
            root_path = path.parent / root_path
        b = root_path.read_bytes()
        assert sha(b) == inp['rootSha256']
        nested(b, original[side])
        copies = inp.get('workingTexts', [])
        if side == 1:
            assert len(copies) == 1
            saved = copies[0]
            assert saved['kind'] == 'Binary' and saved['entryChain'] == ['inner.zip'] and saved['leafEntry'] == 'leaf.bin'
            asset = pathlib.Path(saved['snapshotPath'])
            assert not asset.is_absolute() and asset.suffix == '.bin' and '..' not in asset.parts
            full = path.parent / asset
            assert full.read_bytes() == working[1] and sha(full.read_bytes()) == saved['sha256']
        else:
            assert not copies
    return doc

workspace(pathlib.Path(facts['workspace']))
def package(path):
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        assert 'project.json' in names and all(not n.endswith(('.html', '.patch')) for n in names)
        project = json.loads(z.read('project.json'))['entries'][0]
        expected_entries = {'project.json'}
        for side, field in enumerate(['leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput']):
            inp = project[field]
            b = z.read(inp['rootPath'])
            nested(b, original[side])
            assert sha(b) == inp['rootSha256']
            expected_entries.add(inp['rootPath'])
            for saved in inp.get('workingTexts', []):
                b = z.read(saved['snapshotPath'])
                assert b == working[1] and sha(b) == saved['sha256']
                expected_entries.add(saved['snapshotPath'])
        # Manifest is generated metadata; verify every declared input hash and byte count.
        extra = set(names) - expected_entries
        assert extra <= {'manifest.json'}, extra
        for n in names:
            assert not n.startswith('/') and '..' not in pathlib.PurePosixPath(n).parts
            z.read(n)  # every entry CRC

package(facts['package'])

for name in ['binary-threeway-initial.png', 'binary-threeway-readonly.png', 'binary-threeway-minimum.png', 'binary-threeway-multitab-editor.png', 'binary-threeway-multitab-save.png']:
    assert (root / name).read_bytes().startswith(b'\x89PNG\r\n\x1a\n'), name
if len(sys.argv) > 2:
    cli = pathlib.Path(sys.argv[2])
    package(cli / 'cli-package.zip')
    for name in ['copied.json', 'reloaded.json']:
        workspace(cli / name)
    workspace(cli / 'extracted' / 'project.json')
print(json.dumps({'accepted': True, 'workspace': facts['workspace'], 'package': facts['package'], 'folder': str(folder), 'snapshots': len(states), 'copyDirections': 6, 'roots': 3}))
