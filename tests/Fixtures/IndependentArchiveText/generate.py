"""Synthetic literal fixture only; never reads product outputs or invokes product APIs."""
import hashlib
import io
import json
import pathlib
import zipfile
import zlib

ROOT = pathlib.Path(__file__).resolve().parent
STAMP = (2020, 1, 2, 3, 4, 6)
ATTR = (0o100644 << 16) | 0x20
SIDES = ('left', 'middle', 'right')
ENCODINGS = {'left': ('utf-8', b'', 'utf-8'),
             'middle': ('utf-16-le', b'\xff\xfe', 'utf-16'),
             'right': ('cp1252', b'', 'windows-1252')}
ORIGINAL = {
    'left': 'Shared café\r\nLEFT original €\nthird left\rtail-left',
    'middle': 'Shared café\r\nMIDDLE original £\nthird middle\rtail-middle',
    'right': 'Shared café\r\nRIGHT original “quote”\nthird right\rtail-right'}
EDITED = {
    'left': 'Shared café\r\nLEFT edited €\ninsert-left\rthird left\rtail-left',
    'middle': 'Shared café\r\nMIDDLE edited £\ninsert-middle\rthird middle\rtail-middle',
    'right': 'Shared café\r\nRIGHT edited “quote”\ninsert-right\rthird right\rtail-right'}
CHAIN = ['outer.zip', 'inner.zip']
LEAF = 'texts/leaf.txt'
SIBLING = 'texts/sibling.txt'

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def encoded(side, text):
    codec, preamble, _ = ENCODINGS[side]
    return preamble + text.encode(codec, errors='strict')

def json_file(name, value):
    write(name, (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))

def write(name, data):
    path = ROOT / name
    path.parent.mkdir(parents=True, exist_ok=True)
    # 再実行は同一byteの確認のみ。既存内容の上書きをしない。
    if path.exists():
        if path.read_bytes() != data:
            raise RuntimeError(f'Existing artifact differs: {path}')
    else:
        with path.open('xb') as stream:
            stream.write(data)

def zip_bytes(entries):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_STORED) as archive:
        archive.comment = b'synthetic-next-archive133'
        for name, data in entries.items():
            info = zipfile.ZipInfo(name, STAMP)
            info.create_system = 3
            info.create_version = 20
            info.extract_version = 20
            info.compress_type = zipfile.ZIP_STORED
            info.external_attr = ATTR
            info.internal_attr = 0
            info.extra = b''
            info.comment = b''
            archive.writestr(info, data)
    return stream.getvalue()

def entry_manifest(entries):
    return [{'name': name, 'size': len(data), 'crc32': f'{zlib.crc32(data):08X}',
             'sha256': sha(data), 'dateTime': list(STAMP), 'compression': 0,
             'externalAttributes': ATTR, 'createSystem': 3,
             'createVersion': 20, 'extractVersion': 20,
             'internalAttributes': 0, 'flags': 0, 'extraHex': '', 'commentHex': ''}
            for name, data in entries.items()]

def snapshot(side, path, data):
    _, bom, name = ENCODINGS[side]
    return {'entryChain': CHAIN, 'leafEntry': LEAF, 'snapshotPath': path,
            'sha256': sha(data), 'encodingName': name, 'hasBom': bool(bom)}

def project(inputs):
    return {'mode': 'Text', 'leftPath': '', 'basePath': '', 'rightPath': '',
            'leftReadOnly': True, 'baseReadOnly': True, 'rightReadOnly': True,
            'textInputs': {'semantics': 'Independent',
                           **{side: {'kind': 'Archive'} for side in SIDES}},
            **{field: inputs[side] for side, field in
               [('left', 'leftArchiveInput'), ('middle', 'baseArchiveInput'), ('right', 'rightArchiveInput')]}}

def main():
    manifest = {'provenance': 'synthetic Python literal text + explicit codec/preamble; no product outputs',
                'schema': 'artifact oracle 1 (not product version)', 'fixtures': [], 'expectedBytes': []}
    inputs, saved_inputs = {}, {}
    for side in SIDES:
        original = encoded(side, ORIGINAL[side])
        sibling = encoded(side, f'{side} sibling café\r\nunchanged\n')
        inner_entries = {LEAF: original, SIBLING: sibling, 'metadata.txt': b'synthetic inner metadata\n'}
        inner = zip_bytes(inner_entries)
        outer_entries = {'inner.zip': inner, 'sibling.txt': b'outer same-root sibling\r\n'}
        outer = zip_bytes(outer_entries)
        root_entries = {'outer.zip': outer, 'sibling.txt': b'root same-root sibling\n',
                        'empty.txt': b'', 'provenance.txt': b'synthetic, not WinMerge golden\n'}
        root = zip_bytes(root_entries)
        root_path = f'inputs/{side}.zip'
        write(root_path, root)
        codec, bom, encoding_name = ENCODINGS[side]
        manifest['fixtures'].append({'side': side, 'rootPath': root_path, 'size': len(root), 'sha256': sha(root),
            'entryChain': CHAIN, 'leafEntry': LEAF, 'siblingLeafEntry': SIBLING,
            'pythonCodec': codec, 'preambleHex': bom.hex(), 'encodingName': encoding_name,
            'hasBom': bool(bom), 'originalText': ORIGINAL[side], 'editedText': EDITED[side],
            'layers': [{'entryChain': [], 'entries': entry_manifest(root_entries)},
                       {'entryChain': ['outer.zip'], 'entries': entry_manifest(outer_entries)},
                       {'entryChain': CHAIN, 'entries': entry_manifest(inner_entries)}]})
        inputs[side] = {'rootPath': root_path, 'entryChain': CHAIN, 'leafEntry': LEAF,
                        'rootSha256': sha(root), 'inheritedReadOnly': False}
        for state, text in [('original', ORIGINAL[side]), ('edited', EDITED[side])]:
            data = encoded(side, text)
            path = f'expected/{side}-{state}.text'
            write(path, data)
            manifest['expectedBytes'].append({'path': path, 'side': side, 'state': state,
                                             'text': text, 'size': len(data), 'sha256': sha(data)})
        saved = encoded(side, EDITED[side])
        asset_path = f'workspace-saved.json.assets/{sha(saved)}.text'
        write(asset_path, saved)
        saved_inputs[side] = {**inputs[side], 'workingTexts': [snapshot(side, asset_path, saved)]}
    json_file('workspace-original.json', {'formatVersion': 6, 'activeEntryIndex': 0, 'entries': [project(inputs)]})
    json_file('workspace-saved.json', {'formatVersion': 6, 'activeEntryIndex': 0, 'entries': [project(saved_inputs)]})
    readonly = []
    for side in SIDES:
        for value in (True, None, 'omitted'):
            changed = {key: dict(item) for key, item in inputs.items()}
            if value == 'omitted':
                changed[side].pop('inheritedReadOnly')
            else:
                changed[side]['inheritedReadOnly'] = value
            path = f'readonly/{side}-{str(value).lower()}.json'
            json_file(path, {'formatVersion': 6, 'activeEntryIndex': 0, 'entries': [project(changed)]})
            readonly.append({'path': path, 'blockedDestination': side, 'inheritedReadOnly': value,
                             'expected': 'reject edit/copy-to/working-save, preserve all bytes/history/savepoints'})
    copies = []
    for source in SIDES:
        for target in SIDES:
            if source == target:
                continue
            data = encoded(target, EDITED[source])
            path = f'expected/copy-{source}-to-{target}.text'
            write(path, data)
            copies.append({'source': source, 'destination': target, 'sourceState': 'edited',
                'operation': 'whole-text replacement (apply every source hunk)',
                'expectedPath': path, 'expectedText': EDITED[source], 'size': len(data), 'sha256': sha(data),
                'encodingName': ENCODINGS[target][2], 'hasBom': bool(ENCODINGS[target][1]),
                'sourceUnchanged': f'expected/{source}-edited.text',
                'thirdSideUnchanged': [f'expected/{side}-edited.text' for side in SIDES if side not in (source, target)]})
    json_file('copy-cases.json', copies)
    json_file('readonly-cases.json', readonly)
    json_file('expected-manifest.json', manifest)
    json_file('save-report-package-contract.json', {
        'provenance': 'synthetic', 'status': 'product verification pending',
        'saveBytes': {side: f'expected/{side}-edited.text' for side in SIDES},
        'workingSave': 'keep original ZIP bytes; use workspace-saved.json snapshot metadata/assets',
        'externalSave': 'same bytes as working save; change only saved side Archive->Physical; protect original roots',
        'workspace': 'v6 Independent, middle descriptor + baseArchiveInput payload, relative roots/assets',
        'htmlText': EDITED,
        'htmlChecks': ['reconstruct all 3 sides incl mixed endings from report structure',
                       'Independent labels, no ancestor/merge/patch', 'escape text, mark dirty when unsaved'],
        'packageChecks': ['all entries CRC/SHA, safe relative paths', 'original 3 root ZIPs byte exact',
                          '3 saved asset bytes exact', 'v6 Independent descriptor/chain/leaf/SHA preserved',
                          'extract and independently read every nested ZIP, then product reload separately'],
        'notPredicted': 'product package entry names, asset copy layout and HTML markup depend on implementation'})
    total = sum(path.stat().st_size for path in ROOT.rglob('*') if path.is_file())
    assert total <= 6 * 1024 * 1024, total
    print(json.dumps({'status': 'generated', 'bytes': total, 'copyCases': len(copies), 'readonlyCases': len(readonly)}))

if __name__ == '__main__':
    main()
