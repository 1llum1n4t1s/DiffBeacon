"""Self-authored literals are the oracle; Python stdlib checks full bytes independently."""
import hashlib, json, pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
paths = list(root.glob('fixtures/*/binary-copy-all/facts.json'))
assert len(paths) == 1, paths
folder = paths[0].parent
facts = json.loads(paths[0].read_text(encoding='utf-8-sig'))
original = [bytes.fromhex(x) for x in ('0001FF', '0A0B0C0D80', '1415161718191A')]
assert len(facts['snapshots']) == 9
for state in facts['snapshots']:
    source, destination = state['source'], state['destination']
    expected = original.copy() if not state['name'].startswith('two') else [original[0], original[2]]
    s, d = (source, destination) if len(expected) == 3 else (0 if source == 0 else 1, 0 if destination == 0 else 1)
    expected[d] = expected[s] + expected[d][len(expected[s]):]
    assert [bytes.fromhex(x) for x in state['hex']] == expected, state['name']
    if state['name'] != 'archive-2-1':
        assert (folder / (state['name'] + '.bin')).read_bytes() == expected[d]
for side, name in enumerate(['left.bin', 'middle.bin', 'right.bin']):
    assert (folder / name).read_bytes() == original[side], name
for side, name in enumerate(['left.zip', 'middle.zip', 'right.zip']):
    data = (folder / name).read_bytes()
    assert hashlib.sha256(data).hexdigest().upper() == facts['rootSha256'][side]
    with zipfile.ZipFile(folder / name) as archive:
        assert archive.namelist() == ['leaf.bin']
        assert archive.read('leaf.bin') == original[side]
def workspace(path):
    doc = json.loads(path.read_text(encoding='utf-8-sig'))
    entry = doc['entries'][0]
    snapshot = entry['baseArchiveInput']['workingTexts'][0]
    data = (path.parent / snapshot['snapshotPath']).read_bytes()
    assert data == original[2]
    assert snapshot['kind'] == 'Binary'
    assert hashlib.sha256(data).hexdigest().upper() == snapshot['sha256']
workspace(pathlib.Path(facts['workspace']))
with zipfile.ZipFile(facts['package']) as package:
    doc = json.loads(package.read('project.json'))
    snapshot = doc['entries'][0]['baseArchiveInput']['workingTexts'][0]
    assert package.read(snapshot['snapshotPath']) == original[2]
if len(sys.argv) > 2:
    work = pathlib.Path(sys.argv[2])
    workspace(work / 'copied.json')
    workspace(work / 'reloaded.json')
print(json.dumps({'snapshots': len(facts['snapshots']), 'fixedDirections': 8, 'workspace': facts['workspace'], 'package': facts['package'], 'fullBytes': True}))
