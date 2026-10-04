"""Independent stdlib verification of actual working Text GUI/CLI artifacts."""
import hashlib
import io
import json
import pathlib
import sys
import zipfile
from html.parser import HTMLParser

class Report(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.cells, self.cell, self.in_pre = [], None, False
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'td' and 'data-side' in attrs:
            self.cell = {'attrs': attrs, 'text': ''}; self.cells.append(self.cell)
        if tag == 'pre' and self.cell is not None: self.in_pre = True
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

def zipped(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        assert archive.testzip() is None
        assert len(archive.namelist()) == len(set(archive.namelist()))
        for name in archive.namelist():
            assert not pathlib.PurePosixPath(name).is_absolute() and '..' not in pathlib.PurePosixPath(name).parts
        return {name: archive.read(name) for name in archive.namelist()}

def report(data, left, right):
    parsed = Report(); parsed.feed(data.decode('utf-8-sig'))
    assert parsed.text('left') == left and parsed.text('right') == right

def snapshots(project_path, expected):
    path = pathlib.Path(project_path)
    project = json.loads(path.read_text(encoding='utf-8-sig'))
    assert project['formatVersion'] == 4
    entry = project['entries'][0]
    for side, data in zip(['left', 'right'], expected):
        source = entry[side+'ArchiveInput']; snapshot, = source['workingTexts']
        assert snapshot['entryChain'] == ['inner.zip'] and snapshot['leafEntry'] == 'leaf.txt'
        assert set(snapshot) == {'entryChain', 'leafEntry', 'snapshotPath', 'sha256', 'encodingName', 'hasBom'}
        asset = (path.parent/snapshot['snapshotPath']).resolve()
        assert asset.is_relative_to(path.parent.resolve())
        assert asset.read_bytes() == data
        assert hashlib.sha256(data).hexdigest().upper() == snapshot['sha256']
    return project

def review(root):
    facts_path, = root.rglob('review-working/facts.json')
    facts = json.loads(facts_path.read_text(encoding='utf-8-sig'))
    sha = lambda data: hashlib.sha256(data).hexdigest().upper()
    assert sha(pathlib.Path(facts['source']).read_bytes()) == facts['sourceSha']
    assert sha(pathlib.Path(facts['restoredAsset']).read_bytes()) == facts['restoredAssetSha'] == facts['actualRestoredAssetSha']
    parsed = Report(); parsed.feed(pathlib.Path(facts['html']).read_text(encoding='utf-8-sig'))
    for side in ['left', 'base', 'right']:
        assert parsed.text(side) == facts['expected'+side.title()]
    assert facts['ancestorEditor'] == facts['expectedBase']
    assert facts['manualResult'] == 'manual result\n' and facts['undoResult'] != facts['manualResult']
    assert facts['manualHistoryPreserved'] and facts['dirty'] and facts['staleAdoptionRefused'] and facts['failedRestartPreserved']
    assert facts['leftCaption'] == '左' and facts['rightCaption'] == '右'
    data = pathlib.Path(facts['branches']).read_bytes(); assert sha(data) == facts['branchesSha']
    branches = zipped(data); assert set(branches) == {'a.zip', 'b.zip'}
    for branch in ['a', 'b']:
        with zipfile.ZipFile(io.BytesIO(branches[branch+'.zip'])) as archive:
            archive.setpassword(('branch-'+branch+'-fixture').encode())
            assert archive.testzip() is None and archive.namelist() == ['leaf.txt']
            assert archive.read('leaf.txt') == ('branch '+branch+' original\n').encode()
    project_path = pathlib.Path(facts['branchWorkspace']); project = json.loads(project_path.read_text(encoding='utf-8-sig'))
    assert project['formatVersion'] == 4
    for snapshot in project['entries'][0]['leftArchiveInput']['workingTexts']:
        assert set(snapshot) == {'entryChain', 'leafEntry', 'snapshotPath', 'sha256', 'encodingName', 'hasBom'}
        asset = (project_path.parent/snapshot['snapshotPath']).resolve()
        data = ('working '+snapshot['entryChain'][0]+'\n').encode()
        assert asset.read_bytes() == data and sha(data) == snapshot['sha256']
    assert facts['requestedBranches'] == ['a.zip', 'b.zip'] and facts['branchDialogs'] == 2 and facts['branchMasked'] and facts['branchIsolated']
    receipt = {'threeWayHtmlFullTextAndEndings': True, 'assetFullBytesSha': True, 'encryptedBranchesAllBytesCrc': True,
               'manualMergeDirtyHistoryStaleObserved': True, 'finalCaptionFieldsObserved': True}
    (facts_path.parent/'independent.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    return receipt

if sys.argv[1] == '--review-only':
    root = pathlib.Path(sys.argv[2]).resolve()
    ui = json.loads((root/'ui-report.json').read_text(encoding='utf-8-sig'))
    assert all(item['passed'] for item in ui['assertions'])
    print(json.dumps(review(root))); sys.exit(0)

root = pathlib.Path(sys.argv[1]).resolve()
ui = json.loads((root/'ui-report.json').read_text(encoding='utf-8-sig'))
assert all(item['passed'] for item in ui['assertions'])
review_receipt = review(root)
proof_path, = root.rglob('archive-working-text/proof.json')
proof = json.loads(proof_path.read_text(encoding='utf-8-sig'))
source = [pathlib.Path(path).read_bytes() for path in proof['roots']]
assert [hashlib.sha256(data).hexdigest().upper() for data in source] == proof['hashes']
original = [proof['originalLeft'].encode('cp1252'), b'\xef\xbb\xbf'+proof['originalRight'].encode()]
working = [proof['savedLeft'].encode('cp1252'), b'\xef\xbb\xbf'+proof['latestRight'].encode()]
for data, expected in zip(source, original):
    outer = zipped(data); assert set(outer) == {'inner.zip', 'other.txt'} and outer['other.txt'] == b'other'
    assert zipped(outer['inner.zip']) == {'leaf.txt': expected, 'empty.txt': b''}
assert pathlib.Path(proof['anotherRoot']).read_bytes() == source[0]
assert pathlib.Path(proof['external']).read_bytes() == working[1]
snapshots(proof['workspace'], working)
report(pathlib.Path(proof['html']).read_bytes(), proof['savedLeft'], proof['latestRight'])
report(pathlib.Path(proof['pendingHtml']).read_bytes(), proof['savedLeft'], proof['savedRight'])
assert '未保存の編集' in pathlib.Path(proof['pendingHtml']).read_text(encoding='utf-8-sig')
packed = zipped(pathlib.Path(proof['package']).read_bytes())
assert packed['original/left.zip'] == source[0] and packed['altered/right.zip'] == source[1]
project = json.loads(packed['project.json']); assert project['formatVersion'] == 4
for side, data in zip(['left', 'right'], working):
    snapshot, = project['entries'][0][side+'ArchiveInput']['workingTexts']
    assert packed[snapshot['snapshotPath']] == data
report(packed['report.files/1.html'], proof['savedLeft'], proof['latestRight'])
large = b'\xef\xbb\xbf'+b'x'*(4*1024*1024+257)+b'\r\n'
snapshots(proof['largeWorkspace'], [working[0], large])
large_packed = zipped(pathlib.Path(proof['largePackage']).read_bytes())
large_project = json.loads(large_packed['project.json'])
large_snapshot, = large_project['entries'][0]['rightArchiveInput']['workingTexts']
assert large_packed[large_snapshot['snapshotPath']] == large
assert large_packed['original/left.zip'] == source[0] and large_packed['altered/right.zip'] == source[1]
if len(sys.argv) > 2:
    folder = pathlib.Path(sys.argv[2]).resolve()
    for name in ['cli.html', 'reopened.html', 'copied.html']:
        report((folder/name).read_bytes(), proof['savedLeft'], proof['latestRight'])
    snapshots(folder/'copied.json', working)
    snapshots(folder/'large-copied.json', [working[0], large])
    extracted = folder/'extracted'
    for name, data in packed.items(): assert (extracted/name).read_bytes() == data
    cli_pack = zipped((folder/'cli-package.zip').read_bytes())
    assert cli_pack == packed
    assert (folder/'patch-applied.txt').read_bytes() == proof['latestRight'].encode()
folder = proof_path.parent
patch_source, patch_expected, patch = folder/'patch-source.txt', folder/'patch-expected.txt', folder/'packaged.patch'
patch_source.write_bytes(proof['savedLeft'].encode('cp1252'))
patch_expected.write_bytes(proof['latestRight'].encode())
patch.write_bytes(packed['patch.diff'])
result = {'guiAssertions': len(ui['assertions']), 'review': review_receipt, 'rootZipFullBytesAndAllCrc': True, 'workingAndLargeSnapshotFullBytes': True,
          'reportAllTextAndMixedEndings': True, 'packageAllEntryBytes': True, 'workspace': proof['workspace'], 'largeWorkspace': proof['largeWorkspace'],
          'package': proof['package'], 'patchSource': str(patch_source), 'patchExpected': str(patch_expected), 'patch': str(patch),
          'cliReopenedAndPatchFullBytes': len(sys.argv) > 2}
(folder/'independent.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(result, ensure_ascii=False))
