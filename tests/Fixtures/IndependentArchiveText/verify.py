"""Independent ZIP reader and literal byte oracle; standard library only."""
import hashlib
import io
import json
import pathlib
import zipfile
import zlib

ROOT = pathlib.Path(__file__).resolve().parent

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def load(name):
    return json.loads((ROOT / name).read_text(encoding='utf-8'))

def main():
    manifest = load('expected-manifest.json')
    # generatorからimportしない独立literal。manifestのtextをそのまま金値にしない。
    originals = {'left': 'Shared café\r\nLEFT original €\nthird left\rtail-left',
                 'middle': 'Shared café\r\nMIDDLE original £\nthird middle\rtail-middle',
                 'right': 'Shared café\r\nRIGHT original “quote”\nthird right\rtail-right'}
    edits = {'left': 'Shared café\r\nLEFT edited €\ninsert-left\rthird left\rtail-left',
             'middle': 'Shared café\r\nMIDDLE edited £\ninsert-middle\rthird middle\rtail-middle',
             'right': 'Shared café\r\nRIGHT edited “quote”\ninsert-right\rthird right\rtail-right'}
    codecs = {'left': ('utf-8', b''), 'middle': ('utf-16-le', b'\xff\xfe'), 'right': ('cp1252', b'')}
    records = []
    for fixture in manifest['fixtures']:
        side = fixture['side']
        codec, preamble = codecs[side]
        original_root = (ROOT / fixture['rootPath']).read_bytes()
        assert sha(original_root) == fixture['sha256'] and len(original_root) == fixture['size']
        payload = original_root
        for layer in fixture['layers']:
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                assert archive.testzip() is None
                assert archive.comment == b'synthetic-next-archive133'
                assert archive.namelist() == [entry['name'] for entry in layer['entries']]
                assert len(archive.namelist()) == len(set(archive.namelist()))
                values = {}
                for expected in layer['entries']:
                    name = expected['name']
                    assert not pathlib.PurePosixPath(name).is_absolute() and '..' not in pathlib.PurePosixPath(name).parts
                    info = archive.getinfo(name)
                    data = archive.read(name)
                    assert len(data) == expected['size'] and sha(data) == expected['sha256']
                    assert f'{zlib.crc32(data):08X}' == expected['crc32'] == f'{info.CRC:08X}'
                    assert list(info.date_time) == expected['dateTime']
                    assert info.compress_type == expected['compression']
                    assert info.external_attr == expected['externalAttributes']
                    assert info.create_system == expected['createSystem']
                    assert info.create_version == expected['createVersion']
                    assert info.extract_version == expected['extractVersion']
                    assert info.internal_attr == expected['internalAttributes'] and info.flag_bits == expected['flags']
                    assert info.extra.hex() == expected['extraHex'] and info.comment.hex() == expected['commentHex']
                    values[name] = data
                    records.append({'side': side, 'entryChain': layer['entryChain'], 'entry': name,
                                    'size': len(data), 'sha256': sha(data), 'crc32': f'{info.CRC:08X}', 'passed': True})
                if len(layer['entryChain']) < 2:
                    payload = values['outer.zip' if not layer['entryChain'] else 'inner.zip']
                else:
                    assert values['texts/leaf.txt'] == preamble + originals[side].encode(codec)
                    assert values['texts/sibling.txt'] == preamble + f'{side} sibling café\r\nunchanged\n'.encode(codec)
                    assert values['metadata.txt'] == b'synthetic inner metadata\n'
                if not layer['entryChain']:
                    assert values['empty.txt'] == b'' and values['sibling.txt'] == b'root same-root sibling\n'
                elif len(layer['entryChain']) == 1:
                    assert values['sibling.txt'] == b'outer same-root sibling\r\n'
        for state, text in [('original', originals[side]), ('edited', edits[side])]:
            assert (ROOT / f'expected/{side}-{state}.text').read_bytes() == preamble + text.encode(codec)
    cases = load('copy-cases.json')
    assert {(case['source'], case['destination']) for case in cases} == {
        (source, target) for source in codecs for target in codecs if source != target}
    for case in cases:
        codec, preamble = codecs[case['destination']]
        expected = preamble + edits[case['source']].encode(codec, errors='strict')
        assert (ROOT / case['expectedPath']).read_bytes() == expected
        assert case['sha256'] == sha(expected) and case['size'] == len(expected)
    workspace = load('workspace-saved.json')
    assert workspace['formatVersion'] == 6 and workspace['activeEntryIndex'] == 0
    entry, = workspace['entries']
    assert entry['textInputs'] == {'semantics': 'Independent', **{side: {'kind': 'Archive'} for side in codecs}}
    for side, field in [('left', 'leftArchiveInput'), ('middle', 'baseArchiveInput'), ('right', 'rightArchiveInput')]:
        source = entry[field]
        snapshot, = source['workingTexts']
        assert snapshot['entryChain'] == ['outer.zip', 'inner.zip'] and snapshot['leafEntry'] == 'texts/leaf.txt'
        assert set(snapshot) == {'entryChain', 'leafEntry', 'snapshotPath', 'sha256', 'encodingName', 'hasBom'}
        asset = (ROOT / snapshot['snapshotPath']).resolve()
        assert asset.is_relative_to(ROOT) and asset.parent.name == 'workspace-saved.json.assets'
        codec, preamble = codecs[side]
        data = preamble + edits[side].encode(codec)
        assert asset.read_bytes() == data and snapshot['sha256'] == sha(data)
        assert snapshot['hasBom'] == bool(preamble)
        assert snapshot['encodingName'] == {'left': 'utf-8', 'middle': 'utf-16', 'right': 'windows-1252'}[side]
        assert source['rootSha256'] == sha((ROOT / source['rootPath']).read_bytes())
    assert len(load('readonly-cases.json')) == 9
    total = sum(path.stat().st_size for path in ROOT.rglob('*') if path.is_file())
    assert total < 6 * 1024 * 1024
    evidence = {'status': 'passed', 'scope': 'synthetic fixture and independent oracle only; product not run',
                'allZipEntries': records, 'zipEntryCount': len(records), 'rootCount': 3, 'nestedDepth': 2,
                'copyCaseCount': 6, 'expectedSaveByteCases': 6, 'workspaceSavedSides': 3,
                'readonlyFixturesGeneratedOnly': 9, 'bytesBeforeEvidence': total}
    data = (json.dumps(evidence, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    evidence_path = ROOT / 'verification.json'
    if evidence_path.exists():
        old = json.loads(evidence_path.read_text(encoding='utf-8'))
        assert old['allZipEntries'] == records and old['status'] == 'passed'
    else:
        evidence_path.write_bytes(data)
    print(json.dumps({'status': 'passed', 'ZIPEntries': len(records), 'copies': 6, 'workspaceSavedSides': 3}))

if __name__ == '__main__':
    main()
