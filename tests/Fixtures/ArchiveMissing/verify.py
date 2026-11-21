import hashlib
import io
import json
import pathlib
import sys
import zipfile
from html.parser import HTMLParser


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def resolve(project_path, entry, side):
    value = entry.get(side + 'ArchiveInput')
    if value is None:
        return None
    root = pathlib.Path(value['rootPath'])
    if not root.is_absolute():
        root = project_path.parent / root
    data = root.read_bytes()
    assert sha(data) == value['rootSha256'].upper()
    for name in value['entryChain']:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            assert archive.testzip() is None
            data = archive.read(name.replace('\\', '/').rstrip('/'))
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        assert archive.testzip() is None
        missing = value.get('missingEntryChain')
        if missing is not None:
            assert missing and value.get('leafEntry') is None
            anchor = missing[0].replace('\\', '/').rstrip('/')
            assert all(name.replace('\\', '/').rstrip('/') != anchor for name in archive.namelist())
            return {'missing': True, 'text': '', 'rootSha256': sha(root.read_bytes()), 'tail': missing}
        leaf = archive.read(value['leafEntry'].replace('\\', '/').rstrip('/'))
        return {'missing': False, 'text': leaf.decode('utf-8-sig'), 'rootSha256': sha(root.read_bytes()), 'tail': None}


class Cells(HTMLParser):
    def __init__(self):
        super().__init__()
        self.rows = []
        self.current = None
        self.in_pre = False

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'td' and 'data-side' in attrs:
            self.current = {'attrs': attrs, 'text': ''}
        if tag == 'pre' and self.current is not None:
            self.in_pre = True

    def handle_data(self, data):
        if self.current is not None and self.in_pre:
            self.current['text'] += data

    def handle_endtag(self, tag):
        if tag == 'pre':
            self.in_pre = False
        if tag == 'td' and self.current is not None:
            self.rows.append(self.current)
            self.current = None


def report(path, values):
    data = path.read_text(encoding='utf-8-sig')
    reader = Cells(); reader.feed(data)
    endings = {'CRLF': '\r\n', 'LF': '\n', 'CR': '\r', 'None': ''}
    for side, value in values.items():
        if value is None:
            continue
        rows = [row for row in reader.rows if row['attrs']['data-side'] == side and row['attrs']['data-missing'] == 'false']
        rows.sort(key=lambda row: int(row['attrs']['data-line']))
        text = ''.join(row['text'] + endings[row['attrs']['data-ending']] for row in rows)
        assert text == value['text'], (path, side)
    assert data.count('（存在しない）') >= sum(value is not None and value['missing'] for value in values.values())
    return sha(path.read_bytes())


proof_path = pathlib.Path(sys.argv[1]).resolve()
proof = json.loads(proof_path.read_text(encoding='utf-8-sig'))
assert len(proof['cases']) == 8
rows = []
patch_checks = []
for case in proof['cases']:
    copy = pathlib.Path(case['copy'])
    document = json.loads(copy.read_text(encoding='utf-8-sig'))
    assert document['formatVersion'] == 3 and len(document['entries']) == 1
    entry = document['entries'][0]
    values = {side: resolve(copy, entry, side) for side in ('left', 'base', 'right')}
    original_report = report(pathlib.Path(case['html']), values)
    packed_project = pathlib.Path(case['extracted']) / 'project.json'
    packed = json.loads(packed_project.read_text(encoding='utf-8-sig'))
    assert packed['formatVersion'] == 3
    packed_values = {side: resolve(packed_project, packed['entries'][0], side) for side in values}
    assert packed_values == values
    with zipfile.ZipFile(case['package']) as archive:
        assert archive.testzip() is None and len(archive.namelist()) == len(set(archive.namelist()))
        for item in archive.infolist():
            relative = pathlib.PurePosixPath(item.filename)
            assert not relative.is_absolute() and '..' not in relative.parts and not item.is_dir()
            assert ((item.external_attr >> 16) & 0o170000) != 0o120000
            assert archive.read(item) == (pathlib.Path(case['extracted']) / pathlib.Path(*relative.parts)).read_bytes()
    reopened = report(pathlib.Path(case['reopened']), values)
    patch = (pathlib.Path(case['extracted']) / 'patch.diff').read_text(encoding='utf-8-sig')
    if case['name'] in ('create-empty', 'delete-empty'):
        assert patch.startswith('diff --git ') and len(patch.splitlines()) == 3
        assert ('new file mode' if case['name'] == 'create-empty' else 'deleted file mode') in patch
    if case['name'] not in ('both-missing', 'missing-ancestor'):
        source = proof_path.parent / (case['name'] + '-patch-input.txt')
        expected = proof_path.parent / (case['name'] + '-patch-expected.txt')
        source.write_bytes(values['left']['text'].encode('utf-8'))
        expected.write_bytes(values['right']['text'].encode('utf-8'))
        patch_checks.append({'name': case['name'], 'source': str(source), 'expected': str(expected),
                             'patch': str(pathlib.Path(case['extracted']) / 'patch.diff')})
    rows.append({'case': case['name'], 'allRootCrcAndAbsenceVerified': True, 'allPackageFilesVerified': True,
                 'reportSha256': original_report, 'reopenedReportSha256': reopened, 'values': values})
result = {'casesVerified': 8, 'cases': rows, 'allVirtualAnchorsAndFullReportsVerified': True}
(proof_path.parent / 'patch-checks.json').write_text(json.dumps(patch_checks, indent=2) + '\n', encoding='utf-8')
(proof_path.parent / 'independent-proof.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'casesVerified': 8, 'allVirtualAnchorsAndFullReportsVerified': True}))
