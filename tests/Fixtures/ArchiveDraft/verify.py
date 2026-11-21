"""Independent stdlib reader for actual application draft/save/package products."""
import hashlib
import io
import json
import pathlib
import sys
import zipfile
from html.parser import HTMLParser

DRAFT = 'draft 日本\r\nsecond\n'
LATEST = 'latest edit 日本\r\n'
LEFT = 'header\r\nleft 日本\r\ntail\n'

class Report(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.cells, self.cell, self.in_pre = [], None, False
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'td' and 'data-side' in attrs:
            self.cell = {'attrs': attrs, 'text': ''}
            self.cells.append(self.cell)
        if tag == 'pre' and self.cell is not None:
            self.in_pre = True
    def handle_endtag(self, tag):
        if tag == 'pre': self.in_pre = False
        if tag == 'td': self.cell = None
    def handle_data(self, data):
        if self.in_pre and self.cell is not None: self.cell['text'] += data
    def text(self, side):
        endings = {'CRLF': '\r\n', 'LF': '\n', 'CR': '\r', 'None': ''}
        return ''.join(cell['text'] + endings[cell['attrs']['data-ending']]
                       for cell in self.cells if cell['attrs']['data-side'] == side
                       and cell['attrs']['data-missing'] == 'false')

def zip_bytes(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        assert archive.testzip() is None
        names = archive.namelist()
        assert len(names) == len(set(names))
        for name in names:
            path = pathlib.PurePosixPath(name)
            assert not path.is_absolute() and '..' not in path.parts and '\\' not in name
        return {name: archive.read(name) for name in names}

root = pathlib.Path(sys.argv[1]).resolve()
ui = json.loads((root/'ui-report.json').read_text(encoding='utf-8-sig'))
assert all(item['passed'] for item in ui['assertions'])
products = list(root.rglob('archive-sources/draft-proof.json'))
assert len(products) == 1
proof_path = products[0]
proof = json.loads(proof_path.read_text(encoding='utf-8-sig'))
assert proof['draft'] == DRAFT and proof['latest'] == LATEST
source = [pathlib.Path(path).read_bytes() for path in proof['roots']]
assert [hashlib.sha256(data).hexdigest().upper() for data in source] == proof['hashes']
outer = zip_bytes(source[0]); assert set(outer) == {'one.zip'}
middle = zip_bytes(outer['one.zip']); assert set(middle) == {'two.zip'}
leaf = zip_bytes(middle['two.zip']); assert leaf == {'leaf.txt': LEFT.encode(), 'empty.txt': b''}
assert zip_bytes(source[1]) == {}
partial = pathlib.Path(proof['partialRoot']).read_bytes()
assert hashlib.sha256(partial).hexdigest().upper() == proof['partialHash']
partial_outer = zip_bytes(partial); assert set(partial_outer) == {'one.zip'}
assert zip_bytes(partial_outer['one.zip']) == {}
assert pathlib.Path(proof['partialSaved']).read_bytes() == LATEST.encode()
assert pathlib.Path(proof['saved']).read_bytes() == LATEST.encode()
assert pathlib.Path(proof['adoptionOutput']).read_bytes() == DRAFT.encode()
assert pathlib.Path(proof['emptySaved']).read_bytes() == b''
for name, expected in [('canceled', b'keep cancel'), ('staleOutput', b'keep stale')]:
    actual = pathlib.Path(proof[name]).read_bytes(); assert actual == expected
    field = 'canceledHash' if name == 'canceled' else 'staleHash'
    assert hashlib.sha256(actual).hexdigest().upper() == proof[field]
report = Report(); raw = pathlib.Path(proof['html']).read_text(encoding='utf-8-sig'); report.feed(raw)
assert report.text('left') == LEFT and report.text('right') == DRAFT
assert '未保存の編集' in __import__('html').unescape(raw)
workspace = json.loads(pathlib.Path(proof['workspace']).read_text(encoding='utf-8-sig'))
entries = [entry for entry in workspace['entries'] if entry['rightPath'] == proof['saved']]
assert len(entries) == 1 and entries[0].get('rightArchiveInput') is None and entries[0]['rightReadOnly'] is False
package = zip_bytes(pathlib.Path(proof['package']).read_bytes())
assert set(package) == {'original/missing-left.zip', 'altered/draft-saved.txt', 'report.files/1.html', 'report.html', 'patch.diff', 'project.json'}
assert package['original/missing-left.zip'] == source[0] and package['altered/draft-saved.txt'] == LATEST.encode()
packed = json.loads(package['project.json'])
assert len(packed['entries']) == 1 and packed['formatVersion'] == 2
entry = packed['entries'][0]
assert entry['rightPath'] == 'altered/draft-saved.txt' and entry.get('rightArchiveInput') is None and entry['rightReadOnly'] is False
left = entry['leftArchiveInput']
assert left['rootPath'] == 'original/missing-left.zip' and left['entryChain'] == ['one.zip', 'two.zip'] and left['leafEntry'] == 'leaf.txt'
assert left['rootSha256'].upper() == proof['hashes'][0] and entry['leftReadOnly'] is True
packed_report = Report(); packed_report.feed(package['report.files/1.html'].decode('utf-8-sig'))
assert packed_report.text('left') == LEFT and packed_report.text('right') == LATEST
if len(sys.argv) > 2:
    reopened = Report(); reopened.feed(pathlib.Path(sys.argv[2]).read_text(encoding='utf-8-sig'))
    assert reopened.text('left') == LEFT and reopened.text('right') == LATEST
folder = proof_path.parent
patch_source, patch_expected, patch = folder/'draft-patch-source.txt', folder/'draft-patch-expected.txt', folder/'draft-packaged.patch'
patch_source.write_bytes(LEFT.encode()); patch_expected.write_bytes(LATEST.encode()); patch.write_bytes(package['patch.diff'])
result = {'actualValidationExit': 0, 'guiAssertions': len(ui['assertions']), 'packageEntries': len(package),
          'fullOriginalZipAndNestedCrc': True, 'draftAndSavedBytesIndependent': True,
          'unsavedAndPackagedHtmlAllTextAndEndings': True, 'patchSource': str(patch_source),
          'patchExpected': str(patch_expected), 'patch': str(patch), 'package': proof['package'],
          'reopenedCliReportAllTextAndEndings': len(sys.argv) > 2}
(folder/'draft-independent.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(result, ensure_ascii=False))
