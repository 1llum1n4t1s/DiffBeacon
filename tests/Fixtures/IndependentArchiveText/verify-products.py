"""Read product receipts with Python standard library; expected text is literal."""
import argparse
import base64
import datetime
import hashlib
import html.parser
import io
import json
import os
import pathlib
import time
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent
ROLES = ('left', 'middle', 'right')
FIELDS = ('leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput')
ORIGINAL = ('Shared café\r\nLEFT original €\nthird left\rtail-left',
            'Shared café\r\nMIDDLE original £\nthird middle\rtail-middle',
            'Shared café\r\nRIGHT original “quote”\nthird right\rtail-right')
EDITED = ('Shared café\r\nLEFT edited €\ninsert-left\rthird left\rtail-left',
          'Shared café\r\nMIDDLE edited £\ninsert-middle\rthird middle\rtail-middle',
          'Shared café\r\nRIGHT edited “quote”\ninsert-right\rthird right\rtail-right')
CODECS = ('utf-8', 'utf-16-le', 'cp1252')
BOMS = (b'', b'\xff\xfe', b'')
ENCODINGS = ('utf-8', 'utf-16', 'windows-1252')
CHECKS = []

def check(name, condition):
    if not condition:
        raise ValueError(name)
    CHECKS.append(name)

def load(path):
    return json.loads(path.read_bytes().decode('utf-8-sig'))

def encoded(text, side):
    return BOMS[side] + text.encode(CODECS[side])

class Snapshots(html.parser.HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.role = None
        self.pre = False
        self.values = {}
        self.metadata = {}
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'section' and 'data-input-role' in attrs:
            role = attrs['data-input-role']
            check('unique HTML role ' + role, role in ROLES and role not in self.metadata)
            self.role = role
            self.metadata[role] = attrs
        if tag == 'pre' and attrs.get('class') == 'input-snapshot':
            check('HTML snapshot belongs to role', self.role in ROLES and self.role not in self.values)
            self.values[self.role] = ''
            self.pre = True
    def handle_endtag(self, tag):
        if tag == 'pre': self.pre = False
        if tag == 'section': self.role = None
    def handle_data(self, data):
        if self.pre: self.values[self.role] += data

def html_check(data, texts, name):
    parser = Snapshots()
    parser.feed(data.decode('utf-8-sig'))
    check(name + ' literal three snapshots', parser.values == dict(zip(ROLES, texts)))
    for side, role in enumerate(ROLES):
        check(name + ' encoding/BOM ' + role,
              parser.metadata[role].get('data-encoding') == ENCODINGS[side]
              and parser.metadata[role].get('data-bom') == str(bool(BOMS[side])).lower())

def project_check(project, read, texts, name, physical_side=None):
    check(name + ' v6', project['formatVersion'] == 6)
    entry, = project['entries']
    check(name + ' Independent input roles', entry['textInputs'] == {'semantics': 'Independent', **{role: {'kind': 'Physical' if side == physical_side else 'Archive'} for side, role in enumerate(ROLES)}})
    for side, field in enumerate(FIELDS):
        if side == physical_side:
            check(name + ' detached only selected Archive ' + ROLES[side], entry.get(field) is None)
            data = read(entry[('leftPath', 'basePath', 'rightPath')[side]])
            check(name + ' selected Physical literal bytes ' + ROLES[side], data == encoded(texts[side], side))
            check(name + ' selected Physical writable ' + ROLES[side], entry.get(('leftReadOnly', 'baseReadOnly', 'rightReadOnly')[side], False) is False)
            continue
        source = entry[field]
        check(name + ' Archive root remains fixed readonly ' + ROLES[side], entry[('leftReadOnly', 'baseReadOnly', 'rightReadOnly')[side]] is True)
        check(name + ' route ' + field, source['entryChain'] == ['outer.zip', 'inner.zip'] and source['leafEntry'] == 'texts/leaf.txt' and source['inheritedReadOnly'] is False)
        root = read(source['rootPath'])
        check(name + ' fixed root bytes ' + field, root == (ROOT / 'inputs' / (ROLES[side] + '.zip')).read_bytes())
        check(name + ' root SHA ' + field, source['rootSha256'].upper() == hashlib.sha256(root).hexdigest().upper())
        if texts == EDITED:
            asset, = source['workingTexts']
            data = read(asset['snapshotPath'])
            check(name + ' saved literal bytes ' + field, data == encoded(texts[side], side))
            check(name + ' asset encoding ' + field, asset['encodingName'] == ENCODINGS[side] and asset['hasBom'] is bool(BOMS[side]))
            check(name + ' asset SHA ' + field, asset['sha256'].upper() == hashlib.sha256(data).hexdigest().upper())
    return entry

def package_check(path, texts, physical_side=None):
    with zipfile.ZipFile(path) as archive:
        check(path.name + ' all CRC', archive.testzip() is None)
        names = archive.namelist()
        check(path.name + ' no duplicate entries', len(names) == len(set(names)))
        check(path.name + ' safe paths', all(not pathlib.PurePosixPath(name).is_absolute() and '..' not in pathlib.PurePosixPath(name).parts for name in names))
        files = {name: archive.read(name) for name in names if not name.endswith('/')}
        entry = project_check(json.loads(files['project.json']), files.__getitem__, texts, path.name, physical_side)
        expected = {'project.json', 'report.html', 'report.files/1.html'}
        for side, field in enumerate(FIELDS):
            if side == physical_side:
                expected.add(entry[('leftPath', 'basePath', 'rightPath')[side]])
                continue
            source = entry[field]
            expected.add(source['rootPath'])
            expected.update(asset['snapshotPath'] for asset in source.get('workingTexts', []))
        check(path.name + ' exact full ZIP entry set', set(files) == expected)
        html_check(files['report.files/1.html'], texts, path.name + ' HTML')

def command_oracle(work, gui=None):
    # IndependentArchiveTextScenarios.cs の各Invokeを原典から列挙。
    # 基本18命令 + 全3側の外部保存各6命令/境界拒否各2命令 = 42。旧20は各stateを7と誤算したもの。
    # 製品recordsのlabel/arguments/exitから期待を作らない。
    expected = {}
    def add(label, exit_code, json_scope, *arguments):
        if label in expected:
            raise ValueError('duplicate static command oracle: ' + label)
        expected[label] = {'name': 'independent-archive-text-' + label, 'exit': exit_code,
                           'json': json_scope, 'arguments': [str(argument) for argument in arguments]}
    for state in ('original', 'saved'):
        source = work / ('workspace-' + state + '.json')
        package = work / (state + '-package.zip')
        extracted = work / (state + '-extracted')
        reopened = work / (state + '-reopened.json')
        add(state + '-copy', 0, True, '--project-copy', source, work / (state + '-copy.json'))
        add(state + '-report', 0, True, '--report-project', source, work / (state + '.html'))
        add(state + '-package', 0, True, '--package-project', source, package, '--report')
        add(state + '-extract', 0, True, '--archive-extract', package, extracted)
        add(state + '-reopen', 0, True, '--project-copy', extracted / 'project.json', reopened)
        add(state + '-reopened-report', 0, True, '--report-project', reopened, work / (state + '-reopened.html'))
    for rejection in ('absent', 'missing', 'provider', 'mutable-root', 'old-version'):
        add('reject-' + rejection, 2, False, '--project-copy', work / ('reject-' + rejection + '.json'), work / ('reject-' + rejection + '-output.json'))
    add('gui', 0, False, '--self-test-independent-archive-text', work / 'gui')
    if gui is not None:
        for role in ROLES:
            prefix = 'external-' + role
            source = gui / (prefix + '.json')
            copy = work / (prefix + '-copy.json')
            package = work / (prefix + '-package.zip')
            extracted = work / (prefix + '-extracted')
            reopened = work / (prefix + '-reopened.json')
            add(prefix + '-copy', 0, True, '--project-copy', source, copy)
            add(prefix + '-report', 0, True, '--report-project', copy, work / (prefix + '.html'))
            add(prefix + '-package', 0, True, '--package-project', copy, package, '--report')
            add(prefix + '-extract', 0, True, '--archive-extract', package, extracted)
            add(prefix + '-reopen', 0, True, '--project-copy', extracted / 'project.json', reopened)
            add(prefix + '-reopened-report', 0, True, '--report-project', reopened, work / (prefix + '-reopened.html'))
            for failure in ('tamper', 'corrupt'):
                add('reject-' + failure + '-' + role, 2, False, '--report-project', gui / (failure + '-' + role + '.json'), work / ('reject-' + failure + '-' + role + '.html'))
    return expected

BOUNDARY_EDITS = ('boundary left café\r\nleft €', 'boundary middle £\r\ncentral', 'boundary right “quote”\r\nright')
BOUNDARY_GUARDS = ('physical', 'root', 'workspace', 'filter', 'asset', 'readonly', 'link', 'late-newtabinput', 'late-readonly', 'late-link')
COMMON_GUARD_REASON = '比較入力・アーカイブ原本・フィルター・プロジェクトを上書きできません。'

def boundary_oracle():
    # 196 sourceの2×tabmove、2×parent、3×capacity、3setup、3×10guardを固定する。
    expected = {}
    expected['tab-move-working'] = (1, 'OperationCanceledException', ('保存元の比較が変更されました。',), True, False)
    expected['tab-move-external'] = (1, 'OperationCanceledException', ('保存元の比較または権限が変更されました。',), True, False)
    expected['parent-close-working-success'] = (1, None, (None,), True, True)
    expected['parent-close-external-success'] = (1, None, (None,), True, True)
    for budget in ('bytes', 'documents', 'text-file'):
        reason = 'テキストファイルがサイズ上限を超えています。' if budget == 'text-file' else '作業文書は256件、保存済み本文の合計128 MiBまでです。'
        expected['capacity-' + budget] = (1, 'InvalidDataException', (reason,), False, False)
        expected['capacity-' + budget + '-setup'] = None
    for side in range(3):
        for guard in BOUNDARY_GUARDS:
            kind, reasons = 'InvalidOperationException', (COMMON_GUARD_REASON,)
            if guard in ('readonly', 'late-readonly'):
                kind, reasons = 'UnauthorizedAccessException', ('読み取り専用のファイルは保存できません。',)
            elif guard in ('link', 'late-link'):
                kind, reasons = 'IOException', ('アーカイブ操作のパスにリンクを使用できません。',)
            elif guard == 'asset':
                reasons = (COMMON_GUARD_REASON, '公開または読込み済みの作業snapshotを上書きできません。')
            expected[f'guard-{side}-{guard}'] = (side, kind, reasons, guard.startswith('late-'), False)
    return expected

def repeated_x_sha(length):
    digest = hashlib.sha256()
    chunk = b'x' * (1024 * 1024)
    full, remainder = divmod(length, len(chunk))
    for _ in range(full): digest.update(chunk)
    digest.update(chunk[:remainder])
    return digest.hexdigest().upper()

def boundary_state(state, bodies, snapshots, dirty, name, central_utf8=False, huge=False):
    check(name + ' exact three ordered sides', [side['side'] for side in state['sides']] == [0, 1, 2])
    check(name + ' valid shared generation', isinstance(state['generation'], int) and state['generation'] >= 0)
    for side, value in enumerate(state['sides']):
        codec = 'utf-8' if central_utf8 and side == 1 else CODECS[side]
        preamble = b'' if central_utf8 and side == 1 else BOMS[side]
        encoding = 'utf-8' if central_utf8 and side == 1 else ENCODINGS[side]
        check(name + ' fixed encoding BOM ' + str(side), value['encodingName'] == encoding and value['hasBom'] is bool(preamble))
        check(name + ' dirty and revisions ' + str(side), value['dirty'] is dirty[side] and isinstance(value['textRevision'], int) and value['textRevision'] >= 0
              and isinstance(value['storeRevision'], int) and value['storeRevision'] >= 0)
        if huge and side == 1:
            check(name + ' literal 64MiB plus one x body', value['length'] == 64 * 1024 * 1024 + 1 and value['body'] is None and value['bodyBytesBase64'] is None
                  and value['bodyUtf8Sha256'] == repeated_x_sha(64 * 1024 * 1024 + 1))
        else:
            body = bodies[side]
            check(name + ' literal body UTF8 hash and exact encoded bytes ' + str(side), value['body'] == body and value['length'] == len(body)
                  and value['bodyUtf8Sha256'] == hashlib.sha256(body.encode('utf-8')).hexdigest().upper()
                  and base64.b64decode(value['bodyBytesBase64'], validate=True) == preamble + body.encode(codec))
        snapshot = snapshots[side]
        if snapshot is None:
            check(name + ' absent snapshot ' + str(side), value['snapshotSha256'] is None and value['snapshotBase64'] is None and value['snapshotLength'] == 0)
        else:
            data = preamble + snapshot.encode(codec)
            check(name + ' literal saved snapshot ' + str(side), base64.b64decode(value['snapshotBase64'], validate=True) == data
                  and value['snapshotLength'] == len(data) and value['snapshotSha256'] == hashlib.sha256(data).hexdigest().upper())

def boundary_check(folder):
    receipt = load(folder / 'cases.json')
    expected = boundary_oracle()
    cases = receipt['cases']
    check('boundary schema exact', receipt['schema'] == 'independent-archive-boundaries-1')
    check('boundary source-defined exact unique 37 cases and 3 setups', len(cases) == 40 and [case['name'] for case in cases] == list(expected)
          and len({case['name'] for case in cases}) == 40)
    root_files = list(folder.glob('case-*-root-*.zip'))
    check('boundary source-defined 37 isolated root triplets', len(root_files) == 111
          and {path.name for path in root_files} == {f'case-{case}-root-{side}.zip' for case in range(1, 38) for side in range(3)})
    for path in root_files:
        side = int(path.stem.rsplit('-', 1)[1])
        check('boundary input original bytes retained ' + path.name, path.read_bytes() == (ROOT / 'inputs' / (ROLES[side] + '.zip')).read_bytes())
    by_name = {case['name']: case for case in cases}
    for name, oracle in expected.items():
        case = by_name[name]
        if oracle is None:
            budget = name.removeprefix('capacity-').removesuffix('-setup')
            original = encoded(ORIGINAL[0], 0)
            prior = len(original)
            empty_hash = hashlib.sha256(b'').hexdigest().upper()
            inventory = [{'leaf': 'texts/leaf.txt', 'length': prior, 'sha256': hashlib.sha256(original).hexdigest().upper()}]
            if budget == 'bytes':
                inventory.extend([{'leaf': 'budget-first.txt', 'length': 64 * 1024 * 1024, 'sha256': repeated_x_sha(64 * 1024 * 1024)},
                                  {'leaf': 'budget-second.txt', 'length': 64 * 1024 * 1024 - prior, 'sha256': repeated_x_sha(64 * 1024 * 1024 - prior)}])
            elif budget == 'documents':
                inventory.extend({'leaf': f'budget-{index}.txt', 'length': 0, 'sha256': empty_hash} for index in range(255))
            inventory.sort(key=lambda item: item['leaf'])
            requested = 64 * 1024 * 1024 + 1 if budget == 'text-file' else len(((ORIGINAL[0] if budget == 'bytes' else 'left sibling café\r\nunchanged\n') + 'x').encode('utf-8'))
            check(name + ' source-defined complete inventory and fixed SHA retained', case['inventoryBefore'] == case['inventoryAfter'] == inventory)
            check(name + ' exact capacity and requested bytes', case['bytesAtStart'] == sum(item['length'] for item in inventory)
                  and case['documentsAtStart'] == len(inventory) and case['requestedBodyBytes'] == requested)
            check(name + ' actual Undo Redo scope', case['undoRedoRetained'] is True and case['undoRedoExecuted'] is (budget != 'text-file'))
            continue
        side, exception, reasons, hook, success = oracle
        check(name + ' source-defined outcome reason and hook', case['side'] == side and case['passed'] is True and case['exception'] == exception
              and case['reason'] in reasons and case['hookInvoked'] is hook)
        capacity = name.startswith('capacity-')
        bodies = BOUNDARY_EDITS
        snapshots = (None, ORIGINAL[1], None)
        dirty = (True, True, True)
        if name.startswith('parent-close-'):
            snapshots = (None, None, None)
        if capacity:
            budget = name.removeprefix('capacity-')
            bodies = (ORIGINAL[0], (ORIGINAL[0] if budget == 'bytes' else 'left sibling café\r\nunchanged\n') + 'x', ORIGINAL[2])
            snapshots = (ORIGINAL[0], None if budget == 'documents' else ORIGINAL[0], None)
            dirty = (False, True, False)
        boundary_state(case['before'], bodies, snapshots, dirty, name + ' before', capacity, name == 'capacity-text-file')
        if not success:
            check(name + ' all documents snapshots state and revisions retained', case['stateRetained'] is True and case['documentsRetained'] is True
                  and case['snapshotReferencesRetained'] is True and case['before'] == case['after'])
            boundary_state(case['after'], bodies, snapshots, dirty, name + ' after', capacity, name == 'capacity-text-file')
        else:
            external = name == 'parent-close-external-success'
            after_snapshots = (None, None if external else BOUNDARY_EDITS[1], None)
            boundary_state(case['after'], BOUNDARY_EDITS, after_snapshots, (True, False, True), name + ' after')
            check(name + ' only middle saved point changes', case['stateRetained'] is False and case['documentsRetained'] is False
                  and case['snapshotReferencesRetained'] is external
                  and case['before']['sides'][0] == case['after']['sides'][0] and case['before']['sides'][2] == case['after']['sides'][2]
                  and [value['textRevision'] for value in case['before']['sides']] == [value['textRevision'] for value in case['after']['sides']])
            if external:
                check(name + ' external middle path and exact bytes', case['after']['sides'][1]['documentPath'] == case['target']
                      and base64.b64decode(case['initialBase64'], validate=True) == b'parent-external sentinel'
                      and base64.b64decode(case['finalBase64'], validate=True) == encoded(BOUNDARY_EDITS[1], 1)
                      and pathlib.Path(case['target']).read_bytes() == encoded(BOUNDARY_EDITS[1], 1))
            else:
                check(name + ' working generation and middle revision advance once', case['after']['generation'] == case['before']['generation'] + 1
                      and case['after']['sides'][1]['storeRevision'] > case['before']['sides'][1]['storeRevision'])
        if name == 'tab-move-external':
            sentinel = b'retain tab-move sentinel\r\n'
            check(name + ' independent sentinel bytes', base64.b64decode(case['initialBase64'], validate=True) == sentinel
                  and base64.b64decode(case['finalBase64'], validate=True) == sentinel and pathlib.Path(case['target']).read_bytes() == sentinel)
        if name.startswith('guard-'):
            guard = name.split('-', 2)[2]
            initial = base64.b64decode(case['initialBase64'], validate=True)
            if guard == 'root':
                check(name + ' literal protected Archive root', initial == (ROOT / 'inputs' / 'middle.zip').read_bytes())
            elif guard == 'workspace':
                workspace = json.loads(initial.decode('utf-8-sig'))
                entry, = workspace['entries']
                check(name + ' original empty workspace protected', all(entry.get(key, '') == '' for key in ('leftPath', 'basePath', 'rightPath'))
                      and all(entry.get(key) is None for key in FIELDS) and entry.get('textInputs') is None)
            else:
                check(name + ' literal output sentinel', initial == ('retain ' + name + '\r\n').encode('utf-8'))
            sentinel = pathlib.Path(case['sentinel'])
            check(name + ' actual raw sentinel file unchanged', base64.b64decode(case['finalBase64'], validate=True) == initial and sentinel.read_bytes() == initial)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--work', type=pathlib.Path, required=True)
    parser.add_argument('--receipt', type=pathlib.Path, required=True)
    args = parser.parse_args()
    work = args.work.resolve()
    hashes = load(ROOT / 'fixed-sha256.json')
    for item in hashes['files']:
        data = (ROOT / item['path']).read_bytes()
        check('fixed synthetic source ' + item['path'], len(data) == item['size'] and hashlib.sha256(data).hexdigest().upper() == item['sha256'])
    manifest = load(ROOT / 'expected-manifest.json')
    for fixture in manifest['fixtures']:
        payload = (work / fixture['rootPath']).read_bytes()
        for layer in fixture['layers']:
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                check('nested all CRC ' + fixture['side'] + str(layer['entryChain']), archive.testzip() is None)
                check('nested exact entry set', archive.namelist() == [entry['name'] for entry in layer['entries']])
                for entry in layer['entries']:
                    data = archive.read(entry['name'])
                    info = archive.getinfo(entry['name'])
                    check('nested SHA/size/CRC ' + fixture['side'] + str(layer['entryChain']) + entry['name'], len(data) == entry['size'] and hashlib.sha256(data).hexdigest().upper() == entry['sha256'] and f'{info.CRC:08X}' == entry['crc32'])
                if len(layer['entryChain']) < 2:
                    payload = archive.read('outer.zip' if not layer['entryChain'] else 'inner.zip')
    for state, texts in [('original', ORIGINAL), ('saved', EDITED)]:
        for suffix in ['copy', 'reopened']:
            path = work / (state + '-' + suffix + '.json')
            project_check(load(path), lambda name, parent=path.parent: (parent / name).read_bytes(), texts, path.name)
        for suffix in ['', '-reopened']:
            path = work / (state + suffix + '.html')
            html_check(path.read_bytes(), texts, path.name)
        package_check(work / (state + '-package.zip'), texts)
    report = load(work / 'gui' / 'ui-report.json')
    check('GUI all assertions passed', report['assertions'] and all(item['passed'] for item in report['assertions']))
    gui = pathlib.Path(report['fixtures']) / 'independent-archive-text'
    check('GUI run paths remain within requested run', gui.resolve().is_relative_to((work / 'gui').resolve()))
    boundary_check(gui.parent / 'independent-archive-boundaries')
    project_check(load(gui / 'gui-saved.json'), lambda name: (gui / name).read_bytes(), EDITED, 'GUI saved')
    html_check((gui / 'gui-saved.html').read_bytes(), EDITED, 'GUI HTML')
    html_check((gui / 'pending.html').read_bytes(), EDITED, 'GUI pending HTML')
    package_check(gui / 'gui-package.zip', EDITED)
    for physical_side, role in enumerate(ROLES):
        path = gui / ('external-' + role + '.json')
        project_check(load(path), lambda name: (gui / name).read_bytes(), EDITED, 'GUI external ' + role, physical_side)
        check('external SaveAs literal bytes ' + role, (gui / ('external-' + role + '.text')).read_bytes() == encoded(EDITED[physical_side], physical_side))
        for suffix in ('copy', 'reopened'):
            path = work / ('external-' + role + '-' + suffix + '.json')
            project_check(load(path), lambda name, parent=path.parent: (parent / name).read_bytes(), EDITED, path.name, physical_side)
        package_check(work / ('external-' + role + '-package.zip'), EDITED, physical_side)
        for suffix in ('', '-reopened'):
            path = work / ('external-' + role + suffix + '.html')
            html_check(path.read_bytes(), EDITED, path.name)
    tampered = load(gui / 'tampered-roots.json')
    check('exact all-three tampered root evidence', [item['side'] for item in tampered] == [0, 1, 2])
    for item in tampered:
        side = item['side']
        before = pathlib.Path(item['backupPath']).read_bytes()
        after = pathlib.Path(item['rootPath']).read_bytes()
        expected = bytearray(before); expected[-1] ^= 1
        check('same-size root replacement literal bytes ' + ROLES[side], before == (ROOT / 'inputs' / (ROLES[side] + '.zip')).read_bytes() and after == expected and item['beforeSize'] == item['afterSize'] == len(before))
        check('same-mtime root replacement ' + ROLES[side], item['beforeMtimeUtc'] == item['afterMtimeUtc']
              and pathlib.Path(item['backupPath']).stat().st_mtime_ns == pathlib.Path(item['rootPath']).stat().st_mtime_ns)
        check('tampered valid ZIP fails SHA alone ' + ROLES[side], hashlib.sha256(before).hexdigest().upper() == item['beforeSha256'] and hashlib.sha256(after).hexdigest().upper() == item['afterSha256'] and item['beforeSha256'] != item['afterSha256'])
        with zipfile.ZipFile(io.BytesIO(after)) as archive:
            check('tampered ZIP still fully decodes ' + ROLES[side], archive.testzip() is None)
        check('tampered rejected save output retained ' + ROLES[side], (gui / ('tamper-' + ROLES[side] + '-saved.text')).read_bytes() == b'KEEP-TAMPER-SAVE')
    corrupt = (gui / 'late-bad-sibling.zip').read_bytes()
    check('CC0 corrupt fixture fixed source SHA', hashlib.sha256(corrupt).hexdigest().upper() == 'FA5B0EF47AB5A53318AE5907818B2848A72DE6523207C50490C1C652F3E1A453')
    with zipfile.ZipFile(io.BytesIO(corrupt)) as archive:
        check('corrupt fixture original entry order', archive.namelist() == ['inner.zip', 'after.txt'])
        with zipfile.ZipFile(io.BytesIO(archive.read('inner.zip'))) as inner:
            check('corrupt root leading leaf literal remains valid', inner.read('leaf.txt') == b'nested archive leaf\r\nUTF-8: \xe6\x97\xa5\xe6\x9c\xac\n')
        rejected = False
        try:
            archive.read('after.txt')
        except zipfile.BadZipFile:
            rejected = True
        check('later sibling CRC independently rejects after valid leaf', rejected)
    for side, role in enumerate(ROLES):
        value = load(gui / ('corrupt-' + role + '.json'))['entries'][0][FIELDS[side]]
        check('corrupt product candidate SHA matches broken source ' + role, value['rootSha256'] == 'FA5B0EF47AB5A53318AE5907818B2848A72DE6523207C50490C1C652F3E1A453' and value['entryChain'] == ['inner.zip'] and value['leafEntry'] == 'leaf.txt')
    for source in range(3):
        for destination in range(3):
            if source == destination: continue
            name = f'copy-{ROLES[source]}-to-{ROLES[destination]}.text'
            check(name + ' literal destination bytes', (gui / name).read_bytes() == encoded(EDITED[source], destination))
    check('middle external save bytes', (gui / 'middle-external.text').read_bytes() == encoded(EDITED[1], 1))
    for role in ROLES:
        check('GUI original root retained ' + role, (gui / 'inputs' / (role + '.zip')).read_bytes() == (ROOT / 'inputs' / (role + '.zip')).read_bytes())
        check('E2E original root retained ' + role, (work / 'inputs' / (role + '.zip')).read_bytes() == (ROOT / 'inputs' / (role + '.zip')).read_bytes())
    for name in ['normal', 'minimum', 'many-tabs']:
        bounds = load(gui / ('layout-' + name + '.json'))
        for role in ROLES:
            value = bounds[role + 'Editor']
            check(name + ' editor viewport ' + role, value['visible'] and value['height'] >= 100 and value['width'] > 0 and value['windowY'] >= 0 and value['windowY'] + value['height'] <= bounds['windowHeight'])
        save = bounds['middleSave']
        toolbar = bounds['toolbar']
        check(name + ' middle save window viewport', save['visible'] and save['width'] > 0 and save['height'] > 0
              and save['windowX'] >= 0 and save['windowX'] + save['width'] <= bounds['windowWidth']
              and save['windowY'] >= 0 and save['windowY'] + save['height'] <= bounds['windowHeight'])
        check(name + ' middle save actual toolbar clip', toolbar['visible'] and bounds['toolbarViewportWidth'] > 0 and bounds['toolbarViewportHeight'] > 0
              and save['windowX'] >= toolbar['windowX'] and save['windowY'] >= toolbar['windowY']
              and save['windowX'] + save['width'] <= toolbar['windowX'] + bounds['toolbarViewportWidth']
              and save['windowY'] + save['height'] <= toolbar['windowY'] + bounds['toolbarViewportHeight'])
        png = work / 'gui' / ('independent-archive-text-' + name + '.png')
        check(name + ' rendered PNG', png.read_bytes()[:8] == b'\x89PNG\r\n\x1a\n')
    commands = load(work / 'commands.json')
    expected_commands = command_oracle(work, gui)
    labels = [item['label'] for item in commands]
    check('source-defined complete unique command set and order', len(labels) == len(set(labels)) and labels == list(expected_commands))
    expected_names = {value['name'] for value in expected_commands.values()}
    raw_receipts = list(work.parents[2].glob('*-independent-archive-text-*.stdout.txt'))
    check('source-defined exact complete raw command set', len(raw_receipts) == len(expected_names)
          and {path.name.split('-', 1)[1].removesuffix('.stdout.txt') for path in raw_receipts} == expected_names)
    for item in commands:
        result = item['result']
        expected = expected_commands[item['label']]
        check('static command name exact arguments and declared exit ' + item['label'], result['Name'] == expected['name']
              and item['arguments'] == result['Arguments'] == expected['arguments'] and item['expectedExit'] == expected['exit'])
        check('process actual exit/PID ' + item['label'], result['ExitCode'] == expected['exit'] and result['Pid'] > 0)
        times = [datetime.datetime.fromisoformat(result[field].replace('Z', '+00:00')) for field in ('CreationUtc', 'LaunchUtc', 'ExitObservedUtc')]
        check('process actual OS birth and UTC interval ' + item['label'], all(value.utcoffset() == datetime.timedelta(0) for value in times)
              and times[1] <= times[0] <= times[2])
        if expected['json']:
            check('source-defined successful JSON object scope ' + item['label'], isinstance(json.loads(result['Stdout']), dict))
        raw = list(work.parents[2].glob('*-' + result['Name'] + '.stdout.txt'))
        check('exact raw stdout receipt ' + item['label'], len(raw) == 1 and raw[0].read_bytes().decode('utf-8-sig') == result['Stdout'])
        check('exact raw stderr receipt ' + item['label'], raw[0].with_name(raw[0].name.replace('.stdout.', '.stderr.')).read_bytes().decode('utf-8-sig') == result['Stderr'])
        evidence = result.get('LaunchEvidence')
        if evidence:
            check('Mac actual terminal and complete pipes ' + item['label'], evidence['Terminal'] and evidence['RawExitCode'] == item['expectedExit'] and evidence['StdoutComplete'] and evidence['StderrComplete'] and evidence['PipesReleased'] and not evidence['CleanupIssues'] and evidence['StartupGateSha256'] == '760F968A8438F91441ACE6A7F6556B64D3CC54785A3825B9D1D98112A5CD074E')
        if expected['exit'] == 2:
            check('rejection raw streams ' + item['label'], result['Stdout'] == '' and bool(result['Stderr']))
            boundary = item['label'].startswith(('reject-tamper-', 'reject-corrupt-'))
            sentinel = work / (item['label'] + ('.html' if boundary else '-output.json'))
            check('rejection literal sentinel retained ' + item['label'], sentinel.read_bytes() == (b'KEEP-BOUNDARY-REPORT' if boundary else b'KEEP-INDEPENDENT-ARCHIVE'))
    receipt = {'passed': True, 'proofPid': os.getpid(), 'utcNanoseconds': time.time_ns(), 'checks': len(CHECKS), 'checkNames': CHECKS, 'scope': 'Independent Archive synthetic fixture literal bytes + all product receipts'}
    args.receipt.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(receipt, ensure_ascii=False))

if __name__ == '__main__':
    main()
