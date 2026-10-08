"""Synthetic literal fixtures. Never derives expectations from product output."""
import base64, copy, hashlib, io, json, pathlib, zipfile

ROOT = pathlib.Path(__file__).resolve().parent
ROLES = ('left', 'middle', 'right')
TEXTS = ('shared café\r\nLEFT €\nend-left\r', 'shared café\r\nMIDDLE £\nend-middle\r', 'shared café\r\nRIGHT “quote”\nend-right\r')
WORKING = tuple(t.replace('end-', 'saved-') for t in TEXTS)
ENCODINGS = ('utf-8', 'utf-16-le', 'utf-8')
BOMS = (b'\xef\xbb\xbf', b'\xff\xfe', b'')

def encoded(text, side):
    return BOMS[side] + text.encode(ENCODINGS[side])

def zip_bytes(entries):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, data in entries.items():
            item = zipfile.ZipInfo(name, (2024, 1, 1, 0, 0, 0))
            item.external_attr = 0o100644 << 16
            archive.writestr(item, data)
    return stream.getvalue()

def main():
    inputs = ROOT / 'inputs'
    inputs.mkdir(exist_ok=True)
    files, sides = [], []
    for side, role in enumerate(ROLES):
        raw = encoded(TEXTS[side], side)
        working = encoded(WORKING[side], side)
        data = zip_bytes({'docs/leaf.txt': raw, 'docs/alternate.txt': encoded(TEXTS[side].replace('end-', 'new-'), side)})
        for name, value in ((role + '.txt', raw), (role + '.zip', data), (role + '-working.text', working)):
            path = inputs / name
            if path.exists():
                raise FileExistsError(path)
            path.write_bytes(value)
            files.append({'path': 'inputs/' + name, 'size': len(value), 'sha256': hashlib.sha256(value).hexdigest().upper()})
        sides.append({'role': role, 'text': TEXTS[side], 'workingText': WORKING[side], 'bytes': base64.b64encode(raw).decode(), 'workingBytes': base64.b64encode(working).decode(), 'encoding': ENCODINGS[side], 'bom': bool(BOMS[side])})
    alternate = zip_bytes({'docs/leaf.txt': encoded(TEXTS[0].replace('LEFT', 'SWAP'), 0), 'docs/alternate.txt': encoded(TEXTS[0].replace('end-', 'new-'), 0)})
    assert len(alternate) == (inputs / 'left.zip').stat().st_size
    (inputs / 'left-replacement.zip').write_bytes(alternate)
    files.append({'path': 'inputs/left-replacement.zip', 'size': len(alternate), 'sha256': hashlib.sha256(alternate).hexdigest().upper()})
    project = {'mode': 'Text', 'leftPath': '', 'basePath': '', 'rightPath': '', 'leftReadOnly': True, 'baseReadOnly': True, 'rightReadOnly': True, 'textComparisonPair': 'LeftMiddle', 'textInputs': {'semantics': 'Independent', **{role: {'kind': 'Archive'} for role in ROLES}}}
    for role, key in zip(ROLES, ('leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput')):
        root = 'inputs/' + role + '.zip'
        project[key] = {'rootPath': root, 'rootSha256': next(f['sha256'] for f in files if f['path'] == root), 'entryChain': [], 'leafEntry': 'docs/leaf.txt', 'inheritedReadOnly': False}
    for mode in ('false', 'true', 'null', 'omitted'):
        item = copy.deepcopy(project)
        for key in ('leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput'):
            if mode == 'omitted':
                del item[key]['inheritedReadOnly']
            else:
                item[key]['inheritedReadOnly'] = {'false': False, 'true': True, 'null': None}[mode]
        path = ROOT / ('metadata-' + mode + '.json')
        path.write_text(json.dumps({'formatVersion': 6, 'activeEntryIndex': 0, 'entries': [item]}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        data = path.read_bytes()
        files.append({'path': path.name, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest().upper()})
    (ROOT / 'manifest.json').write_text(json.dumps({'schema': 'input-selection-critical-fixture-v1', 'source': 'generate.py independent synthetic literals; not WinMerge golden', 'license': 'MIT', 'files': files, 'sides': sides}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

if __name__ == '__main__':
    main()
