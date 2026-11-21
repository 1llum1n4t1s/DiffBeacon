"""CC0。標準zipfile/zlib/hashlibだけで暗号化fixtureとGUI成果物を照合。"""
import argparse
import base64
import hashlib
import io
import json
import math
import pathlib
import re
import struct
import zipfile
import zlib

OUTER = b'input306-public-outer'
LEFT = b'input306-public-left'
RIGHT = b'input306-public-right'
LEFT_TEXT = b'left cipher leaf\r\nUTF-8: \xe6\x97\xa5\xe6\x9c\xac\n'
RIGHT_TEXT = b'right cipher leaf\nUTF-8: \xe5\x8f\xb3\r\n'
FIXED_NS = 1704067200123456700


def require(condition, label):
    if not condition:
        raise AssertionError(label)


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def json_read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def no_credentials(value):
    if isinstance(value, dict):
        for key, item in value.items():
            require(not re.search(r'password|credential|secret|authorization', key, re.I), 'credential field prohibited: ' + key)
            no_credentials(item)
    elif isinstance(value, list):
        for item in value:
            no_credentials(item)
    elif isinstance(value, str):
        require(all(secret.decode() not in value for secret in (OUTER, LEFT, RIGHT, b'input306-wrong')), 'public fixture credential value leaked')


def decode(data, correct, wrong):
    contents = []
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for info in archive.infolist():
            require(info.flag_bits == 1 and info.compress_type == zipfile.ZIP_STORED, 'central encrypted flag and stored codec')
            require(struct.unpack_from('<I', data, info.header_offset)[0] == 0x04034B50 and struct.unpack_from('<H', data, info.header_offset + 6)[0] == 1, 'local encrypted flag')
            content = archive.read(info, pwd=correct)
            require(len(content) == info.file_size and zlib.crc32(content) == info.CRC, 'full entry CRC/size')
            for bad in [None, b'input306-wrong', *wrong]:
                try:
                    archive.read(info, pwd=bad)
                except (RuntimeError, zipfile.BadZipFile):
                    pass
                else:
                    raise AssertionError('missing/wrong/cross value accepted: ' + info.filename)
            contents.append((info.filename, content))
    require(len(contents) == 3, 'all three encrypted entries')
    return contents


def rows(entries):
    return sorted(name + ' （' + str(len(data)) + ' bytes）' for name, data in entries)


def fixture_verify(fixture, repo):
    manifest = json_read(fixture / 'manifest.json')
    require(manifest['schema'] == 'input306-fixture-v1', 'fixture schema')
    pin = manifest['source']
    original = (repo / pin['path']).read_bytes()
    require(len(original) == pin['bytes'] and sha(original) == pin['sha256'], 'original CC0 encoder unchanged')
    require('CC0' in original[:160].decode('utf8'), 'original source license declaration')
    root_pin = manifest['root']
    data = (fixture / root_pin['path']).read_bytes()
    require(len(data) == root_pin['bytes'] and sha(data) == root_pin['sha256'], 'raw encrypted root fixed SHA')
    root = decode(data, OUTER, [LEFT, RIGHT])
    require([x[0] for x in root] == ['left.zip', 'right.zip', 'after.txt'] and root[2][1] == b'encrypted outer later sibling\n', 'all outer entry names / later sibling literal')
    left_bytes, right_bytes = root[0][1], root[1][1]
    for which, content in [('leftContainer', left_bytes), ('rightContainer', right_bytes)]:
        require(len(content) == manifest[which]['bytes'] and sha(content) == manifest[which]['sha256'], 'all inner raw SHA')
    left = decode(left_bytes, LEFT, [OUTER, RIGHT])
    right = decode(right_bytes, RIGHT, [OUTER, LEFT])
    require(left == [('leaf.txt', LEFT_TEXT), ('empty.txt', b''), ('tail.txt', b'left later sibling\n')], 'all left literal entries including empty')
    require(right == [('leaf.txt', RIGHT_TEXT), ('empty.txt', b''), ('tail.txt', b'right later sibling\r\n')], 'all right literal entries including empty')
    require(bytes.fromhex(manifest['leftLiteralHex']) == LEFT_TEXT and bytes.fromhex(manifest['rightLiteralHex']) == RIGHT_TEXT, 'hardcoded independent literals')
    pinfile = fixture / 'pins.json'
    require(pinfile.exists(), 'source / reader / license pins required')
    for pin in json_read(pinfile)['files']:
        original = (fixture / pin['path']).read_bytes()
        require(len(original) == pin['bytes'] and sha(original) == pin['sha256'], 'all source/fixture/reader pins ' + pin['path'])
    return manifest, data, root, left, right


def png_verify(path, width, height):
    data = path.read_bytes()
    require(data[:8] == b'\x89PNG\r\n\x1a\n', 'PNG signature')
    offset, compressed, dimensions, terminal = 8, bytearray(), None, False
    while offset < len(data):
        require(offset + 12 <= len(data), 'PNG chunk header')
        size = struct.unpack_from('>I', data, offset)[0]
        kind, content = data[offset + 4:offset + 8], data[offset + 8:offset + 8 + size]
        require(offset + size + 12 <= len(data), 'PNG chunk range')
        require(zlib.crc32(kind + content) == struct.unpack_from('>I', data, offset + 8 + size)[0], 'all PNG chunk CRC')
        if kind == b'IHDR':
            dimensions = struct.unpack('>IIBBBBB', content)
        elif kind == b'IDAT':
            compressed.extend(content)
        elif kind == b'IEND':
            require(size == 0 and offset + 12 == len(data), 'complete PNG IEND')
            terminal = True
        require(kind not in (b'tEXt', b'zTXt', b'iTXt'), 'no PNG textual credential metadata')
        offset += size + 12
    require(terminal and dimensions, 'PNG complete')
    w, h, depth, color, compression, filtering, interlace = dimensions
    require((w, h) == (math.ceil(width), math.ceil(height)) and depth == 8 and color in (2, 6) and compression == filtering == interlace == 0, 'headless PNG format/dimensions')
    raster = zlib.decompress(compressed)
    stride = 1 + w * (3 if color == 2 else 4)
    require(len(raster) == stride * h and all(raster[y * stride] <= 4 for y in range(h)) and len(set(raster)) > 4, 'full PNG scanline decode / nonempty render')


def gui_verify(fixture, repo, report_path):
    manifest, data, root_entries, left_entries, right_entries = fixture_verify(fixture, repo)
    report = json_read(report_path)
    require(report['scope'] in ('independent-text-input-cipher-only', 'all'), 'distinct input306 scope')
    require(report['assertions'] and all(x['passed'] for x in report['assertions']), 'final product assertions all pass')
    run = pathlib.Path(report['fixtures']) / 'independent-text-input-selection-cipher'
    require(json_read(run / 'manifest.json') == manifest, 'copied manifest exact')
    path = run / 'cipher-siblings.zip'
    require(path.read_bytes() == data and path.stat().st_mtime_ns == FIXED_NS, 'full root bytes / independent filesystem mtime ns')
    require((run / 'parent-left.txt').read_bytes() == b'input306 parent left\r\n' and (run / 'parent-right.txt').read_bytes() == b'input306 parent right\r\n', 'parent original input bytes')
    observation = json_read(run / 'observations.json')
    project = json_read(run / 'adopted-project.json')
    no_credentials(observation); no_credentials(project)
    require(observation['schema'] == 'input306-observations-v1' and observation['status'] == 'completed' and observation['id'] == manifest['case']['id'], 'completed input306 receipt')
    require(observation['accepted'] and observation['candidateCount'] == 1 and observation['tabsAfter'] == observation['tabsBefore'] + 1, 'one accepted candidate / one new tab')
    require(observation['before'] == observation['after'], 'complete parent payload / history / save state held')
    checks = observation['checks']
    require(checks and all(checks.values()), 'all in-process facts pass')
    for name in ['left-common-outer', 'left-missing-display-held', 'left-cross-display-held', 'back-outer-retained', 'removed-left-field-cleared', 'right-common-outer-only', 'right-missing-display-held', 'right-cross-display-held', 'closed-fields-clear', 'opener-terminal', 'parent-identity-store-held']:
        require(checks[name], 'required fact ' + name)
    expected_names = ['outer-missing', 'outer-wrong', 'outer-correct', 'left-missing', 'left-cross', 'left-correct', 'outer-back', 'right-missing', 'right-cross', 'right-correct']
    require([s['name'] for s in observation['steps']] == expected_names, 'exact step order')
    steps = {s['name']: s for s in observation['steps']}
    for name in ['outer-missing', 'outer-wrong']:
        require(steps[name]['rows'] == [] and '（未検証）' in steps[name]['summary'], 'outer missing/wrong rejected')
    for name in ['outer-correct', 'outer-back']:
        require(sorted(steps[name]['rows']) == rows(root_entries) and steps[name]['outerRetained'] and '（検証済み）' in steps[name]['summary'], 'outer all encrypted entries confirmed')
    for name in ['left-missing', 'left-cross', 'right-missing', 'right-cross']:
        item = steps[name]
        require(sorted(item['rows']) == rows(root_entries) and item['outerRetained'] and '（未検証）' in item['summary'], 'failed inner keeps old confirmed display ' + name)
    for name in ['left-missing', 'right-missing']:
        require(steps[name]['fieldLengths'] == [len(OUTER), 0], 'new inner empty / real outer retained')
    require(steps['left-cross']['fieldLengths'] == [len(OUTER), len(RIGHT)] and steps['right-cross']['fieldLengths'] == [len(OUTER), len(LEFT)], 'cross values actually submitted')
    require(sorted(steps['left-correct']['rows']) == rows(left_entries) and 'left.zip' in steps['left-correct']['summary'], 'all left entries / actual route')
    require(sorted(steps['right-correct']['rows']) == rows(right_entries) and 'right.zip' in steps['right-correct']['summary'], 'all right entries / actual route')
    source = project['leftArchiveInput']
    require(pathlib.Path(source['rootPath']).name == 'cipher-siblings.zip' and source['rootSha256'] == sha(data), 'typed root and SHA')
    require(source['entryChain'] == ['right.zip'] and source['leafEntry'] == 'leaf.txt' and source['inheritedReadOnly'] and project['leftReadOnly'], 'typed right route / readonly boundary')
    require(project['textInputs']['semantics'] == 'Independent' and project['textComparisonPair'] == 'LeftMiddle', 'independent normal pair')
    payload = observation['payload']['sides']
    require(base64.b64decode(payload[0]['bytes']) == RIGHT_TEXT and payload[0]['text'] == RIGHT_TEXT.decode('utf8') and payload[0]['sha256'] == sha(RIGHT_TEXT), 'adopted independent right literal / byte SHA')
    require(all(base64.b64decode(s['bytes']) == b'' and s['text'] == '' for s in payload[1:]), 'other sides untitled exact')
    actions = observation['actions']
    require(actions[0] == 'entry' and actions[-1] == 'compare-accept' and actions.count('pick') == 1 and actions.count('open') == 2 and actions.count('back') == 1 and actions.count('load') == 7 and actions.count('compare-reject') == 6, 'real button flow counts / retries')
    require(len(observation['layouts']) == 8, 'normal/minimum two masked fields/load/list')
    for item in observation['layouts']:
        require((item['width'], item['height']) in [(1000, 680), (850, 550)], 'normal/minimum geometry')
        rect, clip = item['bounds'], item['clip']
        require(item['visible'] and rect['width'] > 0 and rect['height'] > 0, 'control visible')
        require(rect['x'] >= clip['x'] - .01 and rect['y'] >= clip['y'] - .01 and rect['x'] + rect['width'] <= clip['x'] + clip['width'] + .01 and rect['y'] + rect['height'] <= clip['y'] + clip['height'] + .01, 'control reachable after scroll')
        require(item['control'] != 'list' or rect['height'] >= 100, 'list 100DIP')
        png_verify(run / item['png'], item['width'], item['height'])
    return {'gui': 'verified', 'caseCount': 1, 'encryptedOuterDifferentInner': True, 'allEntryCrcShaLiteral': True, 'retainedOuterClearedInner': True, 'allPngAndBounds': True, 'noCredentialPersistence': True}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parent)
    parser.add_argument('--repo', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[3])
    parser.add_argument('--gui-report', type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path)
    options = parser.parse_args()
    if options.gui_report:
        proof = gui_verify(options.fixture, options.repo, options.gui_report)
    else:
        fixture_verify(options.fixture, options.repo)
        proof = {'fixtureOnly': True, 'caseCountPlanned': 1, 'allEntryCrcShaLiteralCipherFlag': True, 'allMissingWrongCrossRejected': True, 'productRun': 'not-run'}
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        with options.output.open('x', encoding='utf8') as stream:
            json.dump(proof, stream, ensure_ascii=False, indent=2); stream.write('\n')
    print(json.dumps(proof, ensure_ascii=False))
