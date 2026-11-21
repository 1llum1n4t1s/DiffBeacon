"""CC0。input301の期待を製品から作らず、標準libraryだけで照合する。"""
import argparse
import base64
import hashlib
import io
import json
import math
import os
import pathlib
import re
import struct
import zipfile
import zlib

OUTER = b'outer-source-fixture'
INNER = b'inner-source-fixture'
LEAF = b'nested archive leaf\r\nUTF-8: \xe6\x97\xa5\xe6\x9c\xac\n'
FIXED_NS = 1704067200123456700


def require(condition, label):
    if not condition:
        raise AssertionError(label)


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def no_credentials(value):
    if isinstance(value, dict):
        for key, item in value.items():
            require(not re.search(r'password|credential|secret|authorization', key, re.I), 'no credential field: ' + key)
            no_credentials(item)
    elif isinstance(value, list):
        for item in value:
            no_credentials(item)
    elif isinstance(value, str):
        require(OUTER.decode() not in value and INNER.decode() not in value and 'wrong-fixture' not in value,
                'no synthetic secret value')


def entries(data, pwd=None):
    result = []
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for info in archive.infolist():
            content = archive.read(info, pwd=pwd)
            require(len(content) == info.file_size and zlib.crc32(content) == info.CRC, 'independent full entry CRC/size')
            result.append((info.filename.rstrip('/'), info.is_dir(), content))
    return result


def rows(items):
    return sorted(name + (' （フォルダー）' if directory else ' （' + str(len(data)) + ' bytes）') for name, directory, data in items)


def fixture_verify(fixture, repo):
    manifest = read_json(fixture / 'manifest.json')
    require(manifest['schema'] == 'input301-fixture-v1', 'fixture schema')
    require(bytes.fromhex(manifest['literalLeafHex']) == LEAF and manifest['literalLeafText'] == LEAF.decode('utf8'), 'hardcoded leaf literal')
    roots = {}
    for pin in manifest['reusedPins']:
        data = (repo / pin['path']).read_bytes()
        require(len(data) == pin['bytes'] and sha(data) == pin['sha256'], 'all reused original pins ' + pin['path'])
        if pin['path'].endswith('.zip'):
            roots[pathlib.Path(pin['path']).name] = data
    for pin in manifest['generated']:
        data = (fixture / pin['path']).read_bytes()
        require(len(data) == pin['bytes'] and sha(data) == pin['sha256'], 'generated root pin ' + pin['path'])
        roots[pathlib.Path(pin['path']).name] = data
    pin_path = fixture / 'pins.json'
    require(pin_path.exists(), 'input301 source/license/reader pins required')
    for pin in read_json(pin_path)['files']:
        data = (fixture / pin['path']).read_bytes()
        require(len(data) == pin['bytes'] and sha(data) == pin['sha256'], 'all input301 source/license/reader pins ' + pin['path'])
    expected_leaf = [('leaf.txt', False, LEAF), ('empty.txt', False, b''), ('folder', True, b'')]
    require(sorted(entries(roots['leaf.zip'])) == sorted(expected_leaf), 'all literal entries including empty and directory')
    outer = entries(roots['two-passwords.zip'], OUTER)
    require(len(outer) == 1 and outer[0][:2] == ('inner.zip', False), 'encrypted outer all-entry set')
    encrypted_inner = outer[0][2]
    require(entries(encrypted_inner, INNER) == [('leaf.txt', False, LEAF)], 'encrypted inner all literal entries')
    for data, good in [(roots['two-passwords.zip'], OUTER), (encrypted_inner, INNER)]:
        for bad in [None, b'wrong-fixture']:
            try:
                entries(data, bad)
            except (RuntimeError, zipfile.BadZipFile):
                pass
            else:
                raise AssertionError('independent reader accepted absent/wrong synthetic secret')
        entries(data, good)
    require(entries(roots['plain-encrypted.zip']) == [('inner.zip', False, encrypted_inner)], 'reused encrypted inner bytes')
    sibling_entries = entries(roots['siblings.zip'])
    require(sibling_entries == [('left.zip', False, encrypted_inner), ('right.zip', False, encrypted_inner), ('after.txt', False, b'sibling tail\n')], 'siblings all literal entries / fixed encrypted bytes')
    require(entries(roots['dangerous.zip']) == [('../leaf.txt', False, b'unsafe leaf\n'), ('after.txt', False, b'tail checked\n')], 'dangerous ZIP is complete with exact unsafe name')
    require(entries(roots['root-a.zip']) == [('same.txt', False, b'first root bytes A\n')], 'stale alternate literal')
    depth_rows = {}
    for count in [8, 9]:
        current = roots['depth-' + str(count) + '.zip']
        for depth in range(count):
            items = entries(current)
            require(len(items) == 1 and items[0][:2] == ('inner.zip', False), 'depth exact container set')
            depth_rows[(count, depth)] = rows(items)
            current = items[0][2]
        require(sorted(entries(current)) == sorted(expected_leaf), 'depth terminal all literal entries')
        depth_rows[(count, count)] = rows(expected_leaf)
    with zipfile.ZipFile(io.BytesIO(roots['late-bad-sibling.zip'])) as archive:
        infos = archive.infolist()
        require([x.filename for x in infos] == ['inner.zip', 'after.txt'], 'late-bad expected all-entry names')
        require(archive.read(infos[0]) == roots['leaf.zip'], 'first sibling complete before broken later sibling')
        try:
            archive.read(infos[1])
        except zipfile.BadZipFile:
            pass
        else:
            raise AssertionError('late sibling bad CRC accepted independently')
    return manifest, roots, rows(expected_leaf), rows(sibling_entries), depth_rows, rows(outer)


def png_verify(path, width, height):
    raw = path.read_bytes()
    require(raw[:8] == b'\x89PNG\r\n\x1a\n', 'PNG signature')
    offset, compressed, dims, terminal = 8, bytearray(), None, False
    while offset < len(raw):
        require(offset + 12 <= len(raw), 'PNG full chunk header')
        length = struct.unpack_from('>I', raw, offset)[0]
        kind = raw[offset + 4:offset + 8]
        content = raw[offset + 8:offset + 8 + length]
        require(offset + length + 12 <= len(raw), 'PNG chunk extent')
        require(zlib.crc32(kind + content) == struct.unpack_from('>I', raw, offset + 8 + length)[0], 'all PNG chunk CRC')
        if kind == b'IHDR':
            dims = struct.unpack('>IIBBBBB', content)
        elif kind == b'IDAT':
            compressed.extend(content)
        elif kind == b'IEND':
            require(length == 0 and offset + 12 == len(raw), 'PNG terminal IEND')
            terminal = True
        require(kind not in (b'tEXt', b'zTXt', b'iTXt'), 'PNG has no textual credential metadata')
        offset += 12 + length
    require(terminal and dims, 'complete PNG')
    w, h, depth, color, compression, filtering, interlace = dims
    require((w, h) == (math.ceil(width), math.ceil(height)), 'PNG dimensions match bounds')
    require(depth == 8 and color in (2, 6) and compression == filtering == interlace == 0, 'standard headless PNG')
    decoded = zlib.decompress(compressed)
    stride = 1 + w * (3 if color == 2 else 4)
    require(len(decoded) == stride * h and all(decoded[y * stride] <= 4 for y in range(h)), 'independent full PNG scanline decode')
    require(len(set(decoded)) > 4, 'nonempty rendered frame')


def observation_verify(fixture, repo, report_path):
    manifest, roots, leaf_rows, sibling_rows, depth_rows, outer_rows = fixture_verify(fixture, repo)
    report = read_json(report_path)
    require(report['scope'] in ('independent-text-input-archives-only', 'all'), 'new distinct GUI scope')
    require(report['assertions'] and all(x['passed'] for x in report['assertions']), 'product final assertions all pass')
    run = pathlib.Path(report['fixtures']) / 'independent-text-input-selection-archives'
    require(read_json(run / 'manifest.json') == manifest, 'exact copied manifest')
    no_credentials(read_json(run / 'manifest.json'))
    for name, data in roots.items():
        path = run / name
        require(path.read_bytes() == data, 'all run root SHA/full bytes unchanged')
        require(path.stat().st_mtime_ns == FIXED_NS, 'fresh input independent filesystem mtime ns')
    require((run / 'parent-left.txt').read_bytes() == b'input301 parent left\r\n' and (run / 'parent-right.txt').read_bytes() == b'input301 parent right\r\n', 'parent input literals preserved')
    observations = read_json(run / 'observations.json')
    no_credentials(observations)
    require(observations['schema'] == 'input301-observations-v1' and observations['status'] == 'completed', 'final completed observations')
    observed = {x['id']: x for x in observations['cases']}
    require(len(observed) == len(manifest['cases']) == len(observations['cases']), 'all cases exactly once')
    for spec in manifest['cases']:
        case = observed[spec['id']]
        require(case['accepted'] == spec['accepted'], 'expected acceptance ' + spec['id'])
        require(case['candidateCount'] == int(spec['accepted']), 'one or zero candidate')
        require(case['tabsAfter'] == case['tabsBefore'] + int(spec['accepted']), 'exact tab count')
        require(case['before'] == case['after'], 'complete old parent state preserved')
        require(case['checks'] and all(case['checks'].values()), 'all in-process checks pass ' + spec['id'])
        require(case['checks']['closed-current-fields-cleared'] and case['checks']['opener-terminal'], 'close task / retained fields clear')
        require(case['actions'][0] == 'entry' and 'pick' in case['actions'], 'real opener / picker button')
        if spec['accepted']:
            project = read_json(run / case['projectFile'])
            no_credentials(project)
            source = project['leftArchiveInput']
            require(pathlib.Path(source['rootPath']).name == spec['root'] and source['rootSha256'] == sha(roots[spec['root']]), 'typed route root and raw SHA')
            require(source['entryChain'] == spec['chain'] and source['leafEntry'] == spec['leaf'], 'exact typed chain and leaf')
            require(project['textInputs']['semantics'] == 'Independent' and project['textComparisonPair'] == 'LeftMiddle', 'normal independent pair')
            require(source['inheritedReadOnly'] and project['leftReadOnly'], 'archive default inherited readonly')
            payload = case['payload']['sides']
            require(base64.b64decode(payload[0]['bytes']) == LEAF and payload[0]['text'] == LEAF.decode('utf8'), 'adopted exact original leaf literal')
            require(payload[0]['sha256'] == sha(LEAF), 'adopted leaf SHA')
            require(all(base64.b64decode(x['bytes']) == b'' and x['text'] == '' for x in payload[1:]), 'other sides untitled literal')
            require(case['actions'][-1] == 'compare-accept', 'real Compare accept')
    def step(case, name):
        matches = [s for s in observed[case]['steps'] if s['name'] == name]
        require(len(matches) == 1, 'required step exactly once ' + case + '/' + name)
        return matches[0]
    for name in ['root-no-secret', 'root-wrong']:
        require(step('password-retry', name)['rows'] == [], 'failed root has no manifest')
    require(sorted(step('password-retry', 'root-correct')['rows']) == outer_rows, 'encrypted root all manifest entries')
    for name in ['inner-no-secret', 'inner-wrong']:
        item = step('password-retry', name)
        require(sorted(item['rows']) == outer_rows and '（未検証）' in item['summary'], 'inner fail retains previous confirmed rows')
    require(step('password-retry', 'inner-correct')['rows'] == ['leaf.txt （35 bytes）'], 'encrypted leaf full manifest')
    require(observed['password-retry']['checks']['outer-retained'], 'nonempty authenticated outer retained on inner route')
    require(sorted(step('siblings', 'siblings-root')['rows']) == sibling_rows and sorted(step('siblings', 'siblings-back')['rows']) == sibling_rows, 'Back / sibling complete root rows')
    require(step('siblings', 'right-no-secret')['fieldLengths'] == [len(OUTER), 0], 'common optional outer only and new inner empty')
    require(observed['siblings']['checks']['removed-inner-cleared'] and observed['siblings']['checks']['common-outer-only'], 'cleared old inner / nonempty optional outer retained')
    require(step('root-clear', 'root-changed')['fieldLengths'] == [0], 'root change all fields clear')
    for count in [8, 9]:
        require(sorted(step('depth' + str(count), 'depth-root')['rows']) == depth_rows[count, 0], 'depth root all entries')
        for depth in range(1, 9):
            item = step('depth' + str(count), 'depth-' + str(depth))
            require(sorted(item['rows']) == depth_rows[count, depth] and item['fieldCount'] == depth + 1, 'all intermediate container entries/depth')
    require(step('depth9', 'depth-nine-refused')['summary'] == step('depth9', 'depth-8')['summary'].replace('葉未選択', 'inner.zip'), 'ninth requested route stays at eighth container')
    require(observed['depth9']['checks']['ninth-route-refused'], 'ninth actual Open rejected')
    for case in ['late-crc', 'dangerous', 'root-budget', 'work-budget', 'link']:
        old, failed = step(case, 'confirmed-old'), step(case, 'failed-new-request')
        require(sorted(old['rows']) == leaf_rows and old['rows'] == failed['rows'], 'old all rows held ' + case)
        require('（未検証）' in failed['summary'], 'new request not confirmed ' + case)
        require(observed[case]['checks']['old-list-retained'], 'product old-list retention ' + case)
    require(observed['link']['linkCreated'] and (run / 'input301-root-link.zip').is_symlink(), 'actual OS link created')
    require(os.path.realpath(run / 'input301-root-link.zip') == str((run / 'leaf.zip').resolve()), 'link exact run target')
    require(step('password-limit', 'limit-4096')['fieldLengths'] == [4096] and '（検証済み）' in step('password-limit', 'limit-4096')['summary'], '4096 actual field/load accepts')
    over = step('password-limit', 'limit-over')
    require(over['fieldLengths'][0] <= 4096 or '（未検証）' in over['summary'], '4097 setter rejected or guarded')
    require(sorted(step('directory', 'directory-selected')['rows']) == leaf_rows and not step('directory', 'directory-selected')['openEnabled'], 'directory manifest/refused open')
    require('葉未選択' in step('missing', 'missing-selection')['summary'], 'absent selection cannot capture leaf')
    require(step('stale-root', 'confirmed-old')['rows'] == step('stale-root', 'stale-completed')['rows'] and 'root-a.zip' in step('stale-root', 'stale-completed')['summary'] and '（未検証）' in step('stale-root', 'stale-completed')['summary'], 'stale read refuses mixed root/list')
    require(observed['stale-root']['gateCancelled'] and observed['close-pending']['gateCancelled'], 'gates deliberately ignore cancellation then original task ends')
    require(len(observations['layouts']) == 8, 'two window sizes / two fields + load + list')
    for item in observations['layouts']:
        require((item['width'], item['height']) in [(1000, 680), (850, 550)], 'normal / minimum window')
        rect, clip = item['bounds'], item['clip']
        require(item['visible'] and rect['width'] > 0 and rect['height'] > 0, 'control visible geometry')
        require(rect['x'] >= clip['x'] - .01 and rect['y'] >= clip['y'] - .01 and rect['x'] + rect['width'] <= clip['x'] + clip['width'] + .01 and rect['y'] + rect['height'] <= clip['y'] + clip['height'] + .01, 'control reachable after scroll')
        require(item['control'] != 'list' or rect['height'] >= 100, 'list minimum 100DIP')
        png_verify(run / item['png'], item['width'], item['height'])
    return {'cases': len(observed), 'allOriginalPins': True, 'allZipEntryCrcAndLiteral': True, 'allRoutesAndParentState': True,
            'allPngCrcAndReach': True, 'noCredentialPersistence': True, 'unverified': ['encrypted outer with multiple sibling containers', 'real pointer/native picker', 'default 1GiB oversized root (small injected budget used)']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parent)
    parser.add_argument('--repo', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[3])
    parser.add_argument('--gui-report', type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path)
    options = parser.parse_args()
    if options.gui_report:
        proof = observation_verify(options.fixture, options.repo, options.gui_report)
    else:
        manifest, roots, *_ = fixture_verify(options.fixture, options.repo)
        proof = {'fixtureOnly': True, 'casesPlanned': len(manifest['cases']), 'allOriginalPins': True, 'allZipEntryCrcAndLiteral': True, 'rootCount': len(roots), 'productRun': 'not-run'}
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        with options.output.open('x', encoding='utf8') as stream:
            json.dump(proof, stream, ensure_ascii=False, indent=2)
            stream.write('\n')
    print(json.dumps(proof, ensure_ascii=False))
