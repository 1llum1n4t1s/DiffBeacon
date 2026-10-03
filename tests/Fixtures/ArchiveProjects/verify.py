import argparse
import bz2
import gzip
import hashlib
import io
import json
import pathlib
import tarfile
import zipfile
from html.parser import HTMLParser


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def physical(project_path, value):
    path = pathlib.Path(value.replace('\\', '/'))
    return path if path.is_absolute() else project_path.parent / path


def container(data, name):
    while name.endswith(('.gz', '.bz2')):
        if name.endswith('.gz'):
            data = gzip.decompress(data)
            name = name[:-3]
        else:
            data = bz2.decompress(data)
            name = name[:-4]
    result = {}
    if data.startswith(b'PK'):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            assert archive.testzip() is None
            for item in archive.infolist():
                key = item.filename.rstrip('/')
                assert key not in result
                result[key] = None if item.is_dir() else archive.read(item)
    else:
        with tarfile.open(fileobj=io.BytesIO(data), mode='r:') as archive:
            for item in archive:
                assert item.isdir() or item.isfile()
                key = item.name.rstrip('/')
                assert key not in result
                result[key] = None if item.isdir() else archive.extractfile(item).read()
    return result


def content(project, side, path):
    descriptor = project.get(side + 'ArchiveInput')
    if descriptor is None:
        return physical(path, project[side + 'Path']).read_bytes()
    assert project[side + 'Path'] == '' and project[side + 'ReadOnly'] is True
    root = physical(path, descriptor['rootPath'])
    data = root.read_bytes()
    assert sha(data) == descriptor['rootSha256'].upper()
    name = root.name
    for entry in descriptor['entryChain']:
        data = container(data, name)[entry.replace('\\', '/')]
        assert data is not None
        name = entry
    data = container(data, name)[descriptor['leafEntry'].replace('\\', '/')]
    assert data is not None
    return data


class BodyText(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.hidden = 0
        self.parts = []

    def handle_starttag(self, tag, attrs):
        if tag in ['script', 'style']:
            self.hidden += 1

    def handle_endtag(self, tag):
        if tag in ['script', 'style']:
            self.hidden -= 1

    def handle_data(self, data):
        if self.hidden == 0:
            self.parts.append(data)


def verify_report(path, bodies):
    data = path.read_bytes()
    assert len(data) <= 32 * 1024 * 1024 and b'<!doctype html>' in data.lower()
    parser = BodyText()
    parser.feed(data.decode('utf-8-sig'))
    text = ''.join(parser.parts)
    assert '左 <原画> 日本' in text and '右 & 内包' in text
    for body in bodies:
        for line in body.decode('utf-8-sig').splitlines():
            assert line in text, (str(path), line)
    return sha(data)


parser = argparse.ArgumentParser()
parser.add_argument('--products', type=pathlib.Path, required=True)
parser.add_argument('--proof', type=pathlib.Path, required=True)
options = parser.parse_args()
products = load(options.products)
assert len(products) == 7
records = []
for row in products:
    source = pathlib.Path(row['source'])
    copied = pathlib.Path(row['copy'])
    original_json = load(source)
    copied_json = load(copied)
    assert original_json['formatVersion'] == copied_json['formatVersion'] == 2
    expected = original_json['entries'][0]
    actual = copied_json['entries'][0]
    sides = ['left', 'base', 'right'] if expected.get('baseArchiveInput') or expected.get('basePath') else ['left', 'right']
    bodies = [content(expected, side, source) for side in sides]
    for index, side in enumerate(sides):
        assert content(actual, side, copied) == bodies[index]
        old = expected.get(side + 'ArchiveInput')
        new = actual.get(side + 'ArchiveInput')
        if old is not None:
            assert set(new) == {'rootPath', 'entryChain', 'leafEntry', 'rootSha256'}
            assert new['entryChain'] == old['entryChain'] and new['leafEntry'] == old['leafEntry'] and new['rootSha256'] == old['rootSha256']
    report_sha = verify_report(pathlib.Path(row['report']), bodies)
    package = pathlib.Path(row['package'])
    extracted = pathlib.Path(row['extracted'])
    with zipfile.ZipFile(package) as archive:
        assert archive.testzip() is None and len(set(archive.namelist())) == len(archive.namelist())
        for item in archive.infolist():
            assert not item.is_dir() and not item.filename.startswith('/') and '..' not in pathlib.PurePosixPath(item.filename).parts
            assert ((item.external_attr >> 16) & 0o170000) != 0o120000
            assert archive.read(item) == (extracted / item.filename).read_bytes()
        packed = json.loads(archive.read('project.json'))
        assert packed['formatVersion'] == 2
        packed_entry = packed['entries'][0]
        for index, side in enumerate(sides):
            assert content(packed_entry, side, extracted / 'project.json') == bodies[index]
            descriptor = packed_entry.get(side + 'ArchiveInput')
            if descriptor is not None:
                assert not pathlib.Path(descriptor['rootPath']).is_absolute()
                assert descriptor['entryChain'] == actual[side + 'ArchiveInput']['entryChain']
                assert descriptor['leafEntry'] == actual[side + 'ArchiveInput']['leafEntry']
                root_bytes = archive.read(descriptor['rootPath'])
                assert sha(root_bytes) == descriptor['rootSha256'].upper()
        verify_report(extracted / 'report.files/1.html', bodies)
        patch = archive.read('patch.diff')
        if bodies[0] == bodies[-1]:
            assert patch == b'' and row['applied'] is None
        else:
            assert row['applied'] is not None and pathlib.Path(row['exported']).read_bytes() == bodies[0]
            assert pathlib.Path(row['applied']).read_bytes() == bodies[-1]
            assert b'--- original/same.txt' in patch and b'+++ altered/same.txt' in patch
    reopened_sha = verify_report(pathlib.Path(row['packedReport']), bodies)
    records.append({'name': row['name'], 'reportSha256': report_sha, 'reopenedReportSha256': reopened_sha, 'packageSha256': sha(package.read_bytes()), 'leafSha256': [sha(body) for body in bodies], 'fullPackageCrcContentVerified': True, 'sourceRootAndChainRetained': True, 'externalLeafPatchVerified': patch != b''})
proof = {'cases': records, 'casesVerified': 7, 'allPackageFilesVerified': True, 'allLeavesAndReportsVerified': True, 'allCopiedAndPackedProjectsVerified': True, 'patchesApplied': sum(row['externalLeafPatchVerified'] for row in records)}
options.proof.write_text(json.dumps(proof, indent=2), encoding='utf-8')
print(json.dumps({key: value for key, value in proof.items() if key != 'cases'}))
