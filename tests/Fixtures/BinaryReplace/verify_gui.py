"""Independent full-byte reader for HeadlessBinaryReplaceChecks (stdlib only)."""
import argparse
import base64
import hashlib
import io
import json
import pathlib
import struct
import zipfile
import zlib


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def decode(value):
    return base64.b64decode(value, validate=True)


def png(path, expected_size=None):
    data = path.read_bytes()
    require(data.startswith(b'\x89PNG\r\n\x1a\n'), str(path) + '/PNG signature')
    offset, chunks, dimensions, ended = 8, [], None, False
    while offset < len(data):
        require(offset + 12 <= len(data), 'truncated PNG chunk')
        count = struct.unpack('>I', data[offset:offset + 4])[0]
        require(offset + count + 12 <= len(data), 'truncated PNG content')
        kind, content = data[offset + 4:offset + 8], data[offset + 8:offset + 8 + count]
        crc = struct.unpack('>I', data[offset + 8 + count:offset + 12 + count])[0]
        require(zlib.crc32(kind + content) & 0xffffffff == crc, 'PNG every chunk CRC')
        offset += count + 12
        if kind == b'IHDR':
            dimensions = struct.unpack('>IIBBBBB', content)
            require(dimensions[2] == 8 and dimensions[3] in (2, 6) and dimensions[4:] == (0, 0, 0), 'PNG supported complete format')
        elif kind == b'IDAT':
            chunks.append(content)
        elif kind == b'IEND':
            require(not content and offset == len(data), 'PNG trailing bytes')
            ended = True
            break
    require(dimensions and chunks and ended, 'PNG complete structure')
    width, height, _, color, *_ = dimensions
    if expected_size:
        require((width, height) == expected_size, 'PNG dimensions match bounds')
    stride = width * (4 if color == 6 else 3)
    decoder = zlib.decompressobj()
    raw = decoder.decompress(b''.join(chunks), (stride + 1) * height + 1)
    require(decoder.eof and not decoder.unused_data and len(raw) == (stride + 1) * height, 'PNG independently decoded complete pixels')
    require(all(raw[index * (stride + 1)] <= 4 for index in range(height)), 'PNG row filters')
    require(len(set(raw)) > 4, 'PNG contains rendered controls')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--gui', required=True, type=pathlib.Path, help='self-test output directory')
    parser.add_argument('--receipt', type=pathlib.Path, help='independent reader JSON receipt')
    args = parser.parse_args()
    candidates = list(args.gui.rglob('binary-replace/proof.json'))
    if args.gui.name == 'binary-replace' and (args.gui / 'proof.json').is_file():
        candidates.append(args.gui / 'proof.json')
    require(len(candidates) == 1, 'Exactly one binary replacement GUI run is required')
    proof_path = candidates[0]
    run = proof_path.parent.resolve()
    proof = json.loads(proof_path.read_text(encoding='utf-8-sig'))
    fixed = pathlib.Path(__file__).resolve().parent
    manifest = json.loads((fixed / 'manifest.json').read_text(encoding='utf-8-sig'))
    fixtures = {entry['caseId']: entry for entry in manifest['cases']}
    require(len(fixtures) == 51, 'fixed case count')
    golden = {}
    sources = {}
    for ident, entry in fixtures.items():
        source = (fixed / entry['inputFile']).read_bytes()
        expected = (fixed / entry['expectedFile']).read_bytes()
        require(sha(source).lower() == entry['inputSha256'] and len(source) == entry['inputLength'], ident + '/fixed input')
        require(sha(expected).lower() == entry['outputSha256'] and len(expected) == entry['outputLength'], ident + '/fixed golden')
        golden[ident], sources[ident] = expected, source

    def run_file(value):
        path = (run / value).resolve()
        require(path.is_relative_to(run), 'proof path outside run: ' + str(path))
        return path

    def state_bytes(side):
        data = decode(side['bytesBase64']) if side['bytesBase64'] is not None else run_file(side['bytesFile']).read_bytes()
        require(sha(data) == side['sha256'], 'raw state SHA: ' + str(side['side']))
        return data

    def side(state, index):
        return next(value for value in state['sides'] if value['side'] == index)

    def all_bytes(state):
        return {value['side']: state_bytes(value) for value in state['sides']}

    def atomic(before, after, ident):
        require(all_bytes(before) == all_bytes(after), ident + '/full bytes')
        require(before['canUndo'] == after['canUndo'] and before['canRedo'] == after['canRedo'], ident + '/history flags')
        for first, last in zip(before['sides'], after['sides']):
            for field in ('revision', 'dirty', 'pending', 'readOnly', 'selectionStart', 'selectionLength', 'caret', 'hex'):
                require(first[field] == last[field], ident + '/' + field)

    def history(row, expected, original):
        ident, target = row['id'], row['side']
        before, after, clean = row['before'], row['after'], row['clean']
        require(state_bytes(side(before, target)) == original, ident + '/original bytes')
        require(state_bytes(side(after, target)) == expected, ident + '/replacement bytes')
        require(state_bytes(side(clean, target)) == expected and not side(clean, target)['dirty'], ident + '/saved point')
        changed = expected != original
        require(after['canUndo'] == changed, ident + '/one shared history action')
        for other in before['sides']:
            if other['side'] != target:
                require(state_bytes(side(after, other['side'])) == state_bytes(other), ident + '/other side preserved')
        if changed:
            undo, redo = row['undo'], row['redo']
            require(state_bytes(side(undo, target)) == original and side(undo, target)['dirty'], ident + '/Undo saved point')
            require(state_bytes(side(redo, target)) == expected and not side(redo, target)['dirty'], ident + '/Redo saved point')
            require(undo['canRedo'] and not undo['canUndo'], ident + '/exactly one action undone')
        else:
            require(row['undo'] is None and row['redo'] is None, ident + '/noop no history')

    require(proof['schemaVersion'] == 1 and not proof['failures'], 'producer failure')
    require(proof['clipboardReads'] == 0 and proof['clipboardWrites'] == 0, 'no OS clipboard')
    require(proof['ownedWindows'] == 0, 'replacement modal closed')
    require(all(task['completed'] for task in proof['tasks']), 'all owned operations completed')
    rows = {row['id']: row for row in proof['cases']}
    require(len(rows) == len(proof['cases']), 'duplicate case IDs')
    product = [json.loads(line) for line in (run / 'reference-product.ndjson').read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    actual = {row['caseId']: row for row in product}
    require(len(actual) == len(product) and set(actual) == set(fixtures), 'all 51 real UI reference cases')
    for ident, entry in fixtures.items():
        result = actual[ident]
        require(decode(result['bytesBase64']) == golden[ident], ident + '/product all bytes')
        require([result['replacementCount'], result['selectionStart'], result['selectionLength'], result['caret']] ==
                [entry['successfulCount'], entry['expectedSelectionStart'], entry['expectedSelectionLength'], entry['expectedCaret']], ident + '/old reference count/position')
        row = rows['reference/' + ident]
        history(row, golden[ident], sources[ident])
        actual_side = side(row['after'], row['side'])
        require(actual_side['caret'] == entry['expectedCaret'] and actual_side['selectionLength'] == entry['expectedSelectionLength'], ident + '/actual UI caret/count')
        if entry['expectedSelectionLength']:
            require(actual_side['selectionStart'] == entry['expectedSelectionStart'], ident + '/actual selected UI position')
        if ident in ('selection-no-selection', 'empty-source'):
            require(row['route'] == 'disabled-control-and-injected-refusal' and row['status'], ident + '/disabled refusal route')
        else:
            require(row['route'] == 'modal-pointer', ident + '/actual modal route')

    original, edited = b'xABABABz', b'xXYZABABz'
    for ident in ('two/0', 'two/2', 'three/0', 'three/1', 'three/2'):
        history(rows[ident], edited, original)
    # Archive edits deliberately share one store/history across the three sides.
    for target in range(3):
        row = rows['archive/' + str(target)]
        require(state_bytes(side(row['before'], target)) == original, row['id'] + '/original')
        for state in ('after', 'clean', 'redo'):
            require(state_bytes(side(row[state], target)) == edited, row['id'] + '/' + state)
        require(not side(row['clean'], target)['dirty'] and not side(row['redo'], target)['dirty'], row['id'] + '/saved points')
        require(state_bytes(side(row['undo'], target)) == original and side(row['undo'], target)['dirty'], row['id'] + '/Undo bytes/dirty')
        for other in range(target):
            require(state_bytes(side(row['undo'], other)) == edited and not side(row['undo'], other)['dirty'], row['id'] + '/shared other saved point')
    require(all(data == edited for data in all_bytes(rows['archive/reloaded']['after']).values()), 'archive workspace reload all bytes')

    navigation = rows['navigation']
    for field, offset in (('first', 1), ('next', 3), ('previous', 1), ('noWrap', 1)):
        item = side(navigation[field], 0)
        require(item['selectionStart'] == offset and item['selectionLength'] == 2, 'navigation/' + field)
        require(state_bytes(item) == original and not item['dirty'], 'navigation immutable/' + field)
    literal = rows['invalid-correction-literal']
    require(literal['emptyDisabled'] and literal['invalidStatus'] == '数値バイトコードは49文字までです。', 'empty query and exact malformed bytecode status')
    atomic(literal['invalidBefore'], literal['invalid'], 'invalid bytecode preserves original state')
    require(state_bytes(side(literal['invalid'], 0)) == original and not literal['invalid']['canUndo'], 'invalid input atomic')
    require(state_bytes(side(literal['after'], 0)) == b'x<bh:ff>ABABz', 'literal replacement bypasses bytecode')
    for ident, expected in (('endian/big', bytes([0, 81, 52, 18, 255])), ('endian/little', bytes([0, 18, 52, 81, 255])), ('oem/raw-bytecode', bytes([0, 129, 255]))):
        require(state_bytes(side(rows[ident]['after'], rows[ident]['side'])) == expected, ident + '/all bytes')
    raw = b'A' * 40000 + b'\0' + b'A' * 40000
    for ident in ('large-selection-raw', 'large-selection-cache'):
        row = rows[ident]; item = side(row['after'], 0)
        require(state_bytes(item) == raw and item['selectionStart'] == 40001 and item['selectionLength'] == 40000, ident + '/raw cache beyond text limit')
        require(not row['after']['canUndo'], ident + '/no history')
    require(rows['large-selection-raw']['useRaw'] and rows['large-selection-cache']['cached'], 'saved raw backing selected')

    stale_routes = ('selection', 'page', 'readonly', 'other-revision', 'undo', 'pending-hex', 'comparison', 'tab', 'tab-roundtrip', 'cancel', 'observer-throw', 'observer-reentry')
    for route in stale_routes:
        row = rows['stale/' + route]
        require(row['hookReached'] and row['duplicateSameTask'] and row['restored'], row['id'] + '/real candidate and usable controls')
        require(row['eventState'] is not None, row['id'] + '/event snapshot')
        atomic(row['eventState'], row['after'], row['id'] + '/stale rejection')
        require(state_bytes(side(row['after'], 0)) == original + b'\0' * (8194 - len(original)), row['id'] + '/target bytes')
        require(bool(row['observationFailure']) == (route == 'observer-throw'), row['id'] + '/observer failures visible')
        if route in ('tab', 'tab-roundtrip'):
            require(row['tabGenerationAfterHook'] is not None, row['id'] + '/selection notification captured')
            minimum = 2 if route == 'tab-roundtrip' else 1
            require(row['tabGenerationAfterHook'] - row['tabGenerationBefore'] >= minimum, row['id'] + '/real root selection epoch increments')
    for prefix in ('readonly/', 'pending/', 'empty/', 'inherited-readonly/'):
        for target in range(3):
            row = rows[prefix + str(target)]
            require(row['disabled'] and row['error'], row['id'] + '/disabled and injected refusal')
            atomic(row['before'], row['after'], row['id'])
    for ident in ('limit/16MiB', 'limit/256history', 'limit/64MiB-redo', 'canceled-token-redo', 'limit/work'):
        row = rows[ident]
        require(row['error'], ident + '/refusal observed')
        atomic(row['before'], row['after'], ident)
    require(len(state_bytes(side(rows['limit/16MiB']['before'], 0))) == 16 * 1024 * 1024, '16MiB exact input boundary')
    require(rows['limit/64MiB-redo']['before']['canRedo'], '64MiB rejection starts with retained Redo')
    for ident in ('limit/256history', 'limit/64MiB-redo'):
        row = rows[ident]
        require(row['error'] == 'InvalidDataException: バイナリ履歴は共有64 MiB、256操作までです。履歴と本文は変更していません。', ident + '/correct history refusal type/reason')
        require(row['readyStamp'] == row['beforeStamp'] == row['afterStamp'], ident + '/settled snapshot reaches adoption without delayed state changes')
        require(row['tabGenerationBefore'] == row['tabGenerationReady'] == row['tabGenerationAfter'], ident + '/stable root selection context')
        require(row['feedbackAdopted'] is None and row['feedbackMessage'] is None and row['observationFailure'] is None, ident + '/real history exception rather than false feedback or hook failure')
    require(rows['canceled-token-redo']['before']['canRedo'], 'cancellation retains existing Redo')
    work = rows['limit/work']
    require(work['kind'] == 'atomic' and work['error'] == 'InvalidDataException: バイナリ置換の作業量上限を超えました。', 'work refusal exact type/reason (not output capacity)')
    require('作業量' in work['error'], 'work limit specific message')
    require(state_bytes(side(work['before'], 0)) == b'a' * (16 * 1024 * 1024), 'work input fixed full bytes')
    require(side(work['before'], 0)['caret'] == 16 * 1024 * 1024 and side(work['before'], 0)['selectionLength'] == 0, 'work backward starts at exclusive EOF without selection')
    require(work['before']['canRedo'] and not work['before']['canUndo'] and not any(item['dirty'] for item in work['before']['sides']), 'work rejection starts at clean saved point with existing Redo')
    for ident, expected in (('save-guard/input', original), ('save-guard/readonly', b'kept')):
        row = rows[ident]
        require(row['error'] and decode(row['originalBase64']) == expected, ident + '/observed refusal')
        require(run_file(row['path']).read_bytes() == expected, ident + '/output preserved full bytes')
        atomic(row['before'], row['after'], ident)
    noop = rows['same-selection-redo']
    require(all_bytes(noop['before']) == all_bytes(noop['after']), 'same selection full bytes')
    require(noop['before']['canRedo'] and noop['after']['canRedo'] and not noop['after']['canUndo'], 'same selection retains Redo and no history')
    require(not side(noop['after'], 0)['dirty'] and side(noop['after'], 0)['revision'] == side(noop['before'], 0)['revision'], 'same selection saved point/revision')

    expected_saved = {ident: golden[ident] for ident in fixtures}
    expected_saved.update({ident: edited for ident in ('two/0', 'two/2', 'three/0', 'three/1', 'three/2')})
    require({item['id'] for item in proof['saved']} == set(expected_saved), 'saved evidence set')
    for item in proof['saved']:
        data = run_file(item['path']).read_bytes()
        require(data == expected_saved[item['id']] and sha(data) == item['sha256'], item['id'] + '/saved full bytes and SHA')
    for archive in proof['archives']:
        require(archive['leaf'] == 'leaf.bin' and decode(archive['originalBase64']) == original, 'fixed archive expectation')
        for path, digest in zip(archive['paths'], archive['hashes']):
            data = run_file(path).read_bytes(); require(sha(data) == digest, 'archive root unchanged SHA')
            with zipfile.ZipFile(io.BytesIO(data)) as container:
                require(container.testzip() is None and container.namelist() == ['leaf.bin'] and container.read('leaf.bin') == original, 'root ZIP every entry/CRC/full bytes')
    for target in range(3):
        row = rows['archive-root-guard/' + str(target)]
        require(row['error'], row['id'] + '/explicit root refusal')
        require(run_file(row['path']).read_bytes() == decode(row['originalBase64']), row['id'] + '/raw root preserved')
        atomic(row['before'], row['after'], row['id'])

    def inspect_project(document, read, ident):
        require(len(document['entries']) == 1, ident + '/one project')
        entry = document['entries'][0]
        if ident == 'ordinary':
            field = 'leftPath' if next(value for value in proof['cliProjects'] if value['id'] == ident)['side'] == 0 else 'rightPath'
            require(read(entry[field]) == golden['forward-grow'], ident + '/ordinary saved reference bytes')
        elif ident == 'archive':
            for field in ('leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput'):
                source = entry[field]
                with zipfile.ZipFile(io.BytesIO(read(source['rootPath']))) as container:
                    require(container.testzip() is None and container.namelist() == ['leaf.bin'] and container.read('leaf.bin') == original, ident + '/packaged root CRC and bytes')
                require(len(source['workingTexts']) == 1, ident + '/one binary asset per side')
                asset = source['workingTexts'][0]; data = read(asset['snapshotPath'])
                require(asset['kind'] == 'Binary' and data == edited and sha(data) == asset['sha256'].upper(), ident + '/working asset all bytes and SHA')
        else:
            raise AssertionError('unknown project expectation: ' + ident)

    def workspace(path, ident):
        document = json.loads(path.read_text(encoding='utf-8-sig'))
        inspect_project(document, lambda name: (path.parent / name).read_bytes(), ident)

    def package(path, ident):
        with zipfile.ZipFile(path) as container:
            require(container.testzip() is None, ident + '/all ZIP CRC')
            document = json.loads(container.read('project.json'))
            inspect_project(document, container.read, ident)

    require({project['id'] for project in proof['cliProjects']} == {'ordinary', 'archive'}, 'CLI reusable project set')
    for project in proof['cliProjects']:
        workspace(run_file(project['projectFile']), project['id'])
        row = rows['package/' + project['id']]
        package(run_file(row['package']), project['id'])
    require(len(proof['layouts']) == 39, 'normal/minimum/many-tabs plus four modal controls')
    for layout in proof['layouts']:
        bounds = layout['bounds']
        require(bounds['visible'] and bounds['width'] > 0 and bounds['height'] > 0, layout['id'] + '/reachable control')
        require(0 <= bounds['x'] < layout['windowWidth'] and 0 <= bounds['y'] < layout['windowHeight'], layout['id'] + '/viewport bounds')
        require(bounds['x'] + bounds['width'] / 2 < layout['windowWidth'] and bounds['y'] + bounds['height'] / 2 < layout['windowHeight'], layout['id'] + '/pointer center in viewport')
    for name in ('normal', 'minimum', 'many-tabs'):
        layout = next(item for item in proof['layouts'] if item['id'].startswith(name + '/modal/'))
        png(run / ('dialog-' + name + '.png'), (round(layout['windowWidth']), round(layout['windowHeight'])))
        screenshots = list(args.gui.rglob('binary-replace-' + name + '.png'))
        require(len(screenshots) == 1, name + '/Exactly one main PNG is required under --gui; a proof leaf without the screenshot root is unsupported')
        screenshot = screenshots[0].resolve()
        require(screenshot.is_relative_to(args.gui.resolve()), name + '/main PNG resolved path stays within --gui')
        png(screenshot, (1280, 850) if name == 'normal' else (850, 550))
    receipt = {'success': True, 'referenceCases': len(fixtures), 'savedFiles': len(proof['saved']),
               'uiCases': len(rows), 'fullBytes': True, 'zipCrc': True, 'proofFile': str(proof_path),
               'unverified': ['real OS clipboard/pointer', 'native save dialog', 'CLI wrapper (parent-owned)']}
    if args.receipt:
        args.receipt.write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
