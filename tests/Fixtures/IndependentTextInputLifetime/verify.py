"""Independent stdlib lifetime regression reader; baseline gaps remain failures."""
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

TEXTS = ('lifetime left café\r\nleft tail\n', 'lifetime middle £\r\nmiddle tail\n', 'lifetime right €\r\nright tail\n')
RECIPES = ('PPP', 'UUU', 'AAA', 'APU', 'UAP', 'PUA')
CASES = ('compare-close', 'load-close', 'picker-close') + tuple('success-' + recipe for recipe in RECIPES)

def require(value, detail):
    if not value: raise ValueError(detail)

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def fixture_check(fixture):
    manifest = load(fixture / 'manifest.json')
    require(manifest['schema'] == 'independent-text-input-lifetime-fixture-v1', 'fixture schema')
    require(tuple(manifest['texts']) == TEXTS and tuple(manifest['recipes']) == RECIPES, 'independent fixture literals')
    for pin in manifest['files']:
        data = (fixture / pin['path']).read_bytes()
        require(len(data) == pin['size'] and sha(data) == pin['sha256'], 'source pin ' + pin['path'])
    for side, role in enumerate(('left', 'middle', 'right')):
        expected = TEXTS[side].encode('utf-8')
        require((fixture / 'inputs' / (role + '.txt')).read_bytes() == expected, 'physical full literal')
        with zipfile.ZipFile(io.BytesIO((fixture / 'inputs' / (role + '.zip')).read_bytes())) as archive:
            require(archive.namelist() == ['leaf.txt'] and archive.testzip() is None, 'all ZIP entries/CRC')
            data = archive.read('leaf.txt'); entry = archive.infolist()[0]
            require(data == expected and entry.file_size == len(expected) and entry.CRC == binascii.crc32(expected) & 0xffffffff, 'independent leaf bytes/CRC')
    pins = load(fixture / 'provenance.json')
    for name in ('source', 'reader', 'license'):
        pin = pins[name]; require(sha((fixture / pin['path']).read_bytes()) == pin['sha256'], 'provenance ' + name)
    pin = pins['sourceClass']; require(sha((fixture.parents[2] / pin['path']).read_bytes()) == pin['sha256'], 'C# source pin')
    return manifest

def png_check(path):
    data = path.read_bytes(); require(data[:8] == b'\x89PNG\r\n\x1a\n', 'PNG signature')
    offset, compressed, width, height, bpp, final = 8, bytearray(), None, None, None, False
    while offset < len(data):
        count = struct.unpack_from('>I', data, offset)[0]; kind = data[offset + 4:offset + 8]; body = data[offset + 8:offset + 8 + count]
        require(len(body) == count and offset + count + 12 <= len(data), 'bounded PNG chunk')
        require(binascii.crc32(kind + body) & 0xffffffff == struct.unpack_from('>I', data, offset + count + 8)[0], 'PNG chunk CRC')
        if kind == b'IHDR':
            width, height, depth, color, compression, filter_kind, interlace = struct.unpack('>IIBBBBB', body)
            require(depth == 8 and color in (2, 6) and compression == filter_kind == interlace == 0, 'PNG format'); bpp = 3 if color == 2 else 4
        elif kind == b'IDAT': compressed.extend(body)
        elif kind == b'IEND': require(count == 0, 'PNG IEND'); final = True
        offset += count + 12
    require(final and offset == len(data) and width and height and bpp, 'complete PNG')
    decoded = zlib.decompress(bytes(compressed)); stride = width * bpp
    require(len(decoded) == height * (stride + 1), 'all PNG scanlines')
    previous = bytearray(stride)
    for y in range(height):
        start = y * (stride + 1); mode = decoded[start]; row = bytearray(decoded[start + 1:start + stride + 1]); require(mode <= 4, 'PNG filter')
        for x in range(stride):
            a = row[x - bpp] if x >= bpp else 0; b = previous[x]; c = previous[x - bpp] if x >= bpp else 0
            if mode == 1: prediction = a
            elif mode == 2: prediction = b
            elif mode == 3: prediction = (a + b) // 2
            elif mode == 4:
                p = a + b - c; prediction = min(((abs(p - a), 0, a), (abs(p - b), 1, b), (abs(p - c), 2, c)))[2]
            else: prediction = 0
            row[x] = (row[x] + prediction) & 255
        previous = row
    return {'path': str(path), 'width': width, 'height': height, 'sha256': sha(data)}

def body_check(evidence, texts, dirty):
    require(len(evidence['sides']) == 3, 'all body sides')
    for side, value in enumerate(evidence['sides']):
        expected = texts[side].encode('utf-8')
        require(value['text'] == texts[side] and base64.b64decode(value['bytes'], validate=True) == expected and value['sha256'] == sha(expected), 'full body bytes/BOM/newline/SHA')
        require(value['dirty'] == dirty, 'body savepoint')

def run_check(fixture, run, expected_pid):
    manifest = fixture_check(fixture); observation = load(run / 'observations.json')
    require('synthetic-lifetime-marker' not in json.dumps(observation, ensure_ascii=False), 'public marker must not flow to JSON')
    require(observation['schema'] == 'independent-text-input-lifetime-observations-v1' and observation['scope'] == 'original-task-close-drain-regression-only', 'observation schema/scope')
    require(observation['pid'] == expected_pid > 0 and observation['processCreationUtc'] and observation['stopwatchFrequency'] > 0, 'actual application PID provenance')
    require(observation['completedCaseCount'] == observation['expectedCaseCount'] == 9 and tuple(case['id'] for case in observation['cases']) == CASES, 'all nine actual cases')
    # 現行baselineのfalseをpassへ置換しない。
    require(all(assertion['passed'] for assertion in observation['assertions']), 'actual regression assertions failed')
    pngs = []
    for case in observation['cases']:
        name = case['id']; directory = run / name; success = name.startswith('success-')
        for pin in manifest['files']:
            require((directory / pathlib.PurePosixPath(pin['path']).name).read_bytes() == (fixture / pin['path']).read_bytes(), 'all input originals preserved')
        require((directory / 'existing.out').read_bytes() == b'lifetime-existing-output\r\n', 'protected existing output')
        require(case['accepted'] == success and case['tabsAfter'] == case['tabsBefore'] + int(success), 'only expected adoption')
        require(case['storeBefore'] == case['storeAfter'] == 0 and case['before'] == case['after'], 'parent/store display kept')
        body_check(case['before'], tuple(text + 'parent-dirty\n' for text in TEXTS), True)
        require(case['fieldsClearedAfterDrain'], 'UI field clear after actual task drain')
        events = case['events']; require(len({event['sequence'] for event in events}) == len(events) and all(event['timestamp'] > 0 for event in events), 'unique actual completion captures')
        by_name = {event['event']: event for event in events}; require(len(by_name) == len(events), 'each event captured once')
        require(load(directory / 'partial-events.json') == events, 'case finally retains exact raw completion captures')
        opening = by_name['opening-complete']; require(opening['openingComplete'] and opening['nonNullSlots'] == 0, 'opener complete after array clear')
        if name in ('load-close', 'picker-close'):
            require(opening['browserComplete'], 'opener before original browser completion')
            require(by_name['browser-complete']['browserComplete'], 'original browser terminal capture')
        else:
            require(opening['submissionComplete'], 'opener before original submission completion')
            require(by_name['submission-complete']['submissionComplete'], 'original submission terminal capture')
            require(by_name['submission-state-observed']['arraysObserved'] >= 8 and by_name['submission-state-observed']['nonNullSlots'] >= 6, 'real non-null submission local/selection outer arrays observed')
        if not success:
            reached = by_name['gate-reached']; closed = by_name['closed-before-release']; released = by_name['gate-release']
            require(reached['sequence'] < closed['sequence'] < released['sequence'] < opening['sequence'], 'actual Close/release/open completion order')
            require(not closed['openingComplete'] and not closed['gateReleased'], 'opener remains pending while gate closed')
            require(not (closed['browserComplete'] if name in ('load-close', 'picker-close') else closed['submissionComplete']), 'original operation remains pending')
            require(opening['gateReleased'], 'completion follows actual release')
            require('cancel-close' in case['clicks'], 'real Cancel closes modal')
        if name == 'compare-close': require(case['nonEmptyFieldsBeforeClose'] == 3 and case['nonNullSlotsBeforeClose'] >= 12, 'three nonempty actual credential input/clone arrays')
        if name == 'load-close': require(case['nonEmptyFieldsBeforeClose'] == 1 and case['nonNullSlotsBeforeClose'] == 1 and opening['arraysObserved'] >= 1, 'nonempty actual pending reader local array')
        if name == 'picker-close': require(opening['arraysObserved'] == 0, 'picker has no fabricated credential array')
        require(case['clicks'][0] == 'entry', 'real entry Button')
        pngs.append(png_check(directory / 'dialog.png'))
        if success:
            recipe = name[8:]; require(case['recipe'] == recipe and 'compare' in case['clicks'] and case['candidateCount'] == 1, 'success real Compare/candidate')
            body_check(case['adopted'], tuple('' if recipe[side] == 'U' else TEXTS[side] for side in range(3)), False)
            pngs.append(png_check(directory / 'adopted.png'))
    return {'status': 'original-task-close-drain-regression-pass', 'cases': 9, 'actualAppPid': expected_pid, 'pngs': pngs, 'unverified': ['native OS picker cancellation', 'event subscription identity directly inspected', 'candidate reader private clone arrays beyond observed local/selection arrays', 'macOS/AOT until separately executed']}

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--run', type=pathlib.Path); parser.add_argument('--expected-app-pid', type=int); parser.add_argument('--output', type=pathlib.Path); args = parser.parse_args()
    fixture = pathlib.Path(__file__).resolve().parent
    if args.run:
        require(args.expected_app_pid is not None, 'external actual application PID required')
        result = run_check(fixture, args.run.resolve(), args.expected_app_pid)
    else: result = {'status': 'fixture-only-pass-product-not-executed', 'files': len(fixture_check(fixture)['files'])}
    if args.output:
        require(not args.output.exists(), 'new result path required'); args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False))
if __name__ == '__main__': main()
