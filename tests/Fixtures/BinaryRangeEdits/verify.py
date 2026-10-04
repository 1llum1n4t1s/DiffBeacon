"""Self-authored fixed byte literals; independent stdlib full assets and ZIP CRC."""
import hashlib, json, pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
paths = list(root.glob('fixtures/*/binary-range-edits/facts.json'))
assert len(paths) == 1, paths
folder = paths[0].parent
facts = json.loads(paths[0].read_text(encoding='utf-8-sig'))
original = [bytes.fromhex(x) for x in ('0001FF', '0A0B0C0D80', '1415161718191A')]
edited = [bytes.fromhex(x) for x in ('00CC01FF', '0ACC0B0C0D80', '14CC15161718191A')]
assert len(facts['snapshots']) == 5
for state in facts['snapshots']:
    three = state['name'].startswith('three'); side = int(state['name'].split('-')[1])
    expected = original.copy() if three else [original[0], original[2]]
    target = side if three else (0 if side == 0 else 1)
    expected[target] = edited[side]
    assert [bytes.fromhex(x) for x in state['hex']] == expected, state['name']
    assert (folder / (state['name'] + '.bin')).read_bytes() == edited[side]
assert (folder / 'late.bin').read_bytes() == bytes.fromhex('0AAA0B0C0D80')
assert (folder / 'stale-save.bin').read_bytes() == original[1]
mib = 1024 * 1024
assert (folder / 'history-redo-original.bin').read_bytes() == b'\x00' * (16 * mib)
assert (folder / 'history-redo-empty.bin').read_bytes() == b''
assert (folder / 'history-redo-before.bin').read_bytes() == b'\x22' * (12 * mib) + b'\x00' * (4 * mib)
assert (folder / 'history-redo-after.bin').read_bytes() == b'\x33' * (8 * mib) + b'\x22' * (4 * mib) + b'\x00' * (4 * mib)
for side, name in enumerate(['left.bin', 'middle.bin', 'right.bin']):
    assert (folder / name).read_bytes() == original[side], name
for side, name in enumerate(['left.zip', 'middle.zip', 'right.zip']):
    assert hashlib.sha256((folder / name).read_bytes()).hexdigest().upper() == facts['rootSha256'][side]
    with zipfile.ZipFile(folder / name) as archive:
        assert archive.testzip() is None
        assert archive.namelist() == ['leaf.bin']
        assert archive.read('leaf.bin') == original[side]
def workspace(path):
    doc = json.loads(path.read_text(encoding='utf-8-sig')); entry = doc['entries'][0]
    snapshot = entry['baseArchiveInput']['workingTexts'][0]
    data = (path.parent / snapshot['snapshotPath']).read_bytes()
    assert data == edited[1]
    assert snapshot['kind'] == 'Binary'
    assert hashlib.sha256(data).hexdigest().upper() == snapshot['sha256']
    for asset in path.parent.glob(path.name + '.assets/**/*'):
        if asset.is_file():
            assert asset.read_bytes() == edited[1], asset
workspace(pathlib.Path(facts['workspace']))
with zipfile.ZipFile(facts['package']) as package:
    assert package.testzip() is None
    doc = json.loads(package.read('project.json')); entry = doc['entries'][0]
    snapshot = entry['baseArchiveInput']['workingTexts'][0]
    assert package.read(snapshot['snapshotPath']) == edited[1]
    for field, side in [('leftArchiveInput', 0), ('baseArchiveInput', 1), ('rightArchiveInput', 2)]:
        import io
        data = package.read(entry[field]['rootPath'])
        assert hashlib.sha256(data).hexdigest().upper() == facts['rootSha256'][side]
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            assert archive.testzip() is None and archive.read('leaf.bin') == original[side]
if len(sys.argv) > 2:
    work = pathlib.Path(sys.argv[2]); workspace(work / 'copied.json'); workspace(work / 'reloaded.json')
    with zipfile.ZipFile(work / 'cli-package.zip') as archive:
        assert archive.testzip() is None
        doc = json.loads(archive.read('project.json'))
        assert archive.read(doc['entries'][0]['baseArchiveInput']['workingTexts'][0]['snapshotPath']) == edited[1]
print(json.dumps({'snapshots': 5, 'fixedSides': 5, 'workspace': facts['workspace'], 'package': facts['package'], 'fullBytes': True, 'zipCrc': True}))
