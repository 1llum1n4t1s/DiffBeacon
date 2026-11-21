"""Independent stdlib fixture and actual critical-GUI evidence reader."""
import argparse
import base64
import binascii
import hashlib
import io
import json
import pathlib
import struct
import zipfile
import zlib

FIXED_NS = 1704067200123456700
ROLES = ('left', 'middle', 'right')
TEXTS = ('shared café\r\nLEFT €\nend-left\r', 'shared café\r\nMIDDLE £\nend-middle\r', 'shared café\r\nRIGHT “quote”\nend-right\r')
WORKING = tuple(t.replace('end-', 'saved-') for t in TEXTS)
ENCODINGS = ('utf-8', 'utf-16-le', 'utf-8')
BOMS = (b'\xef\xbb\xbf', b'\xff\xfe', b'')
CASES = ('working-cache', 'metadata-false', 'metadata-true', 'metadata-null', 'metadata-omitted', 'default-archive', 'physical-readonly', 'parent-body', 'parent-role', 'parent-pair', 'parent-readonly', 'pending-hex', 'store-save', 'parent-close', 'window-close', 'sha-before-first-read')

def require(value, detail):
    if not value:
        raise ValueError(detail)

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def raw(text, side):
    return BOMS[side] + text.encode(ENCODINGS[side])

def zip_check(data, side, replacement=False):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        require(archive.testzip() is None, 'full ZIP entry CRC')
        require(archive.namelist() == ['docs/leaf.txt', 'docs/alternate.txt'], 'full ZIP entry names')
        original = TEXTS[side].replace('LEFT', 'SWAP') if replacement else TEXTS[side]
        expected = {'docs/leaf.txt': raw(original, side), 'docs/alternate.txt': raw(TEXTS[side].replace('end-', 'new-'), side)}
        for entry in archive.infolist():
            data = archive.read(entry.filename)
            require(data == expected[entry.filename], 'ZIP literal bytes ' + entry.filename)
            require(entry.file_size == len(data) and entry.CRC == binascii.crc32(data) & 0xffffffff, 'ZIP independent size/CRC')

def png_check(path):
    data = path.read_bytes()
    require(data[:8] == b'\x89PNG\r\n\x1a\n', 'PNG signature')
    offset, compressed, width, height, bpp, final = 8, bytearray(), None, None, None, False
    while offset < len(data):
        count = struct.unpack_from('>I', data, offset)[0]
        kind = data[offset + 4:offset + 8]
        body = data[offset + 8:offset + 8 + count]
        require(len(body) == count and offset + count + 12 <= len(data), 'PNG bounded chunk')
        crc = struct.unpack_from('>I', data, offset + count + 8)[0]
        require(binascii.crc32(kind + body) & 0xffffffff == crc, 'PNG chunk CRC')
        if kind == b'IHDR':
            width, height, depth, color, compression, filter_kind, interlace = struct.unpack('>IIBBBBB', body)
            require(depth == 8 and color in (2, 6) and compression == filter_kind == interlace == 0, 'PNG expected format')
            bpp = 3 if color == 2 else 4
        elif kind == b'IDAT':
            compressed.extend(body)
        elif kind == b'IEND':
            require(count == 0, 'PNG IEND')
            final = True
        offset += count + 12
    require(final and offset == len(data) and width and height and bpp, 'PNG complete stream')
    stream = zlib.decompress(bytes(compressed))
    stride = width * bpp
    require(len(stream) == height * (stride + 1), 'PNG all scanlines')
    prior = bytearray(stride)
    for y in range(height):
        start = y * (stride + 1)
        mode = stream[start]
        require(mode <= 4, 'PNG row filter')
        row = bytearray(stream[start + 1:start + stride + 1])
        for x in range(stride):
            a, b = (row[x - bpp] if x >= bpp else 0), prior[x]
            c = prior[x - bpp] if x >= bpp else 0
            if mode == 1: predictor = a
            elif mode == 2: predictor = b
            elif mode == 3: predictor = (a + b) // 2
            elif mode == 4:
                p = a + b - c
                predictor = min(((abs(p - a), 0, a), (abs(p - b), 1, b), (abs(p - c), 2, c)))[2]
            else: predictor = 0
            row[x] = (row[x] + predictor) & 255
        prior = row
    return {'path': path.name, 'width': width, 'height': height, 'sha256': sha(data)}

def evidence(value, texts):
    require(len(value['sides']) == 3, 'three evidence sides')
    for side, body in enumerate(value['sides']):
        data = base64.b64decode(body['bytes'], validate=True)
        require(body['text'] == texts[side], 'literal text side ' + str(side))
        require(data == raw(texts[side], side), 'encoding/BOM/newline full bytes side ' + str(side))
        require(body['sha256'] == sha(data), 'literal captured SHA')

def verify_fixture(fixture):
    manifest = load(fixture / 'manifest.json')
    require(manifest['schema'] == 'input-selection-critical-fixture-v1', 'fixture schema')
    provenance = load(fixture / 'provenance.json')
    for name in ('source', 'reader', 'license'):
        pin = provenance[name]
        require(sha((fixture / pin['path']).read_bytes()) == pin['sha256'], 'source/license/reader provenance ' + name)
    source_pin = provenance['sourceClass']
    require(sha((fixture.parents[2] / source_pin['path']).read_bytes()) == source_pin['sha256'], 'C# check source provenance')
    for pin in manifest['files']:
        data = (fixture / pin['path']).read_bytes()
        require(len(data) == pin['size'] and sha(data) == pin['sha256'], 'pinned source ' + pin['path'])
    for side, role in enumerate(ROLES):
        require((fixture / 'inputs' / (role + '.txt')).read_bytes() == raw(TEXTS[side], side), 'physical source literal')
        require((fixture / 'inputs' / (role + '-working.text')).read_bytes() == raw(WORKING[side], side), 'working source literal')
        zip_check((fixture / 'inputs' / (role + '.zip')).read_bytes(), side)
    zip_check((fixture / 'inputs' / 'left-replacement.zip').read_bytes(), 0, True)
    require((fixture / 'inputs' / 'left.zip').stat().st_size == (fixture / 'inputs' / 'left-replacement.zip').stat().st_size, 'same-size independent replacement')
    for mode in ('false', 'true', 'null', 'omitted'):
        project = load(fixture / ('metadata-' + mode + '.json'))['entries'][0]
        for key in ('leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput'):
            require(('inheritedReadOnly' not in project[key]) if mode == 'omitted' else project[key]['inheritedReadOnly'] is {'false': False, 'true': True, 'null': None}.get(mode), 'original metadata ' + mode)
    return manifest

def verify_run(fixture, run):
    manifest = verify_fixture(fixture)
    observations = load(run / 'observations.json')
    require(observations['schema'] == 'input-selection-critical-observations-v1' and observations['status'] == 'critical-subset-only', 'actual evidence scope')
    cases = observations['cases']
    require('synthetic-critical-marker' not in json.dumps(observations, ensure_ascii=False), 'public marker must not flow into JSON')
    require(tuple(c['id'] for c in cases) == CASES, 'all critical cases exactly once')
    images = []
    for case in cases:
        mode = case['id']
        directory = run / mode
        for pin in manifest['files']:
            path = directory / pin['path']
            require(path.read_bytes() == (fixture / pin['path']).read_bytes(), 'final input/metadata preserved ' + mode + '/' + pin['path'])
            if pin['path'].startswith('inputs/'):
                require(path.stat().st_mtime_ns == FIXED_NS, 'actual ns final input ' + mode + '/' + pin['path'])
        require((directory / 'existing.out').read_bytes() == b'existing-critical-output\r\n', 'existing output preserved')
        success = mode in CASES[:7]
        require(case['accepted'] == success and all(case['restored']) and case['sameStore'], 'accepted/restored/store ' + mode)
        require(case['tasksCompleted'] and case['fieldsCleared'], 'actual task and field lifetime ' + mode)
        require(case['nonEmptyFieldsBeforeSubmission'] == (0 if mode == 'physical-readonly' else 3), 'nonempty field clear evidence')
        require(case['clicks'][0] == 'entry' and 'compare' in case['clicks'], 'real entry/Compare evidence')
        images.append(png_check(directory / 'dialog.png'))
        if mode != 'pending-hex':
            initial = WORKING if mode == 'working-cache' else TEXTS
            if mode == 'default-archive':
                # Untitled fixture uses UTF-8 without BOM independently of physical side encodings.
                require(all(s['text'] == '\nparent-dirty' and base64.b64decode(s['bytes']) == b'\nparent-dirty' for s in case['before']['sides']), 'Untitled dirty parent literals')
            else:
                dirty = mode not in ('physical-readonly', 'metadata-false', 'metadata-true', 'metadata-null', 'metadata-omitted')
                evidence(case['before'], tuple(t + ('\nparent-dirty' if dirty else '') for t in initial))
        if success:
            evidence(case['adopted'], WORKING if mode == 'working-cache' else TEXTS)
            blocked = mode in ('metadata-true', 'metadata-null', 'metadata-omitted', 'default-archive', 'physical-readonly')
            require(all(s['editorReadOnly'] == blocked and not s['dirty'] for s in case['adopted']['sides']), 'adopted readonly/savepoints')
            require(case['after'] == case['before'], 'parent original dirty/cache state preserved')
            require(case['tabsAfter'] == case['tabsBefore'] + 1, 'only one candidate attached')
            images.append(png_check(directory / 'adopted.png'))
            if blocked:
                require(len(case['diagnostics']) == (6 if mode == 'physical-readonly' else 9), 'actual copy/save readonly diagnostics')
                require(all(str(side) in {c.split(':')[-1] for c in case['clicks'] if c.startswith('readonly-copy:')} for side in range(3)), 'all three copy destinations exercised')
                require(sum(c.startswith('save:') for c in case['clicks']) == (3 if mode == 'physical-readonly' else 6), 'actual save buttons')
        elif mode in ('parent-close', 'window-close'):
            require(case['callbackWaited'] and case['gateReturned'] and case['tokenCancelled'], 'closure awaits real callback finally')
        elif mode == 'sha-before-first-read':
            require(case['after'] == case['before'] and case['tabsBefore'] == case['tabsAfter'], 'initial read rejection holds old state')
            before, after = directory / 'before.raw', directory / 'after.raw'
            require(before.stat().st_mtime_ns == after.stat().st_mtime_ns == FIXED_NS, 'raw actual ns (no retiming)')
            require(before.read_bytes() == (fixture / 'inputs/left.zip').read_bytes(), 'raw backup original SHA')
            require(after.read_bytes() == (fixture / 'inputs/left-replacement.zip').read_bytes(), 'raw alternate SHA')
            require(before.stat().st_size == after.stat().st_size and sha(before.read_bytes()) != sha(after.read_bytes()), 'same size different raw SHA')
            zip_check(before.read_bytes(), 0); zip_check(after.read_bytes(), 0, True)
            require(case['rawTiming'] == 'browser-confirmed-before-first-candidate-read', 'initial-read timing')
        else:
            require(case['callbackWaited'] and case['gateReturned'] and (case['tokenCancelled'] or mode == 'store-save'), 'stale actual task completion/cancellation')
            require(case['afterMutation'] == case['after'] and case['tabsBefore'] == case['tabsAfter'], 'post-mutation old display retained')
        if mode == 'pending-hex':
            require(case['sameBinaryPanel'] and case['pendingHex'] == 'GG', 'same actual Binary panel and pending Hex retained')
            require(len(case['binaryBefore']) == len(case['binaryAfter']) == 3, 'all three Binary sides')
            for side in range(3):
                require(base64.b64decode(case['binaryBefore'][side], validate=True) == base64.b64decode(case['binaryAfter'][side], validate=True) == raw(TEXTS[side], side), 'unchanged applied bytes despite pending Hex')
        if mode == 'working-cache':
            require(case['storeBefore'] == case['storeAfter'] == case['noopGeneration'] == 3, 'same SHA Import and candidate store no-op')
        if mode == 'store-save':
            require(case['storeAfter'] > case['storeBefore'] and 'save:中央の作業版を保存' in case['clicks'], 'real working save changes shared generation')
            require((directory / 'saved-middle.text').read_bytes() == raw(TEXTS[1] + '\nparent-dirty', 1), 'actual shared working bytes')
            require(case['before']['sides'][1]['dirty'] and not case['after']['sides'][1]['dirty'], 'real working savepoint changes')
    return {'status': 'critical-subset-only', 'cases': len(cases), 'pngs': images, 'unverified': ['private credential arrays', 'all six copy/save/package routes', 'encrypted passwords', 'unsafe entry/depth/complete output guards', 'native picker/OS input', 'macOS and AOT until separately executed']}

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--run', type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path)
    args = parser.parse_args()
    fixture = pathlib.Path(__file__).resolve().parent
    result = verify_run(fixture, args.run.resolve()) if args.run else {'status': 'fixture-only-pass-product-not-executed', 'files': len(verify_fixture(fixture)['files'])}
    if args.output:
        require(not args.output.exists(), 'new reader result path required')
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))

if __name__ == '__main__':
    main()
