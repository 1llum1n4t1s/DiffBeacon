"""CC0。既存固定ZipCrypto原本を読み、最小の追加入力だけ新規生成する。"""
import hashlib
import io
import json
import pathlib
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent
REPO = ROOT.parents[2]
SOURCE = REPO / 'tests/Fixtures/Archives/Sources'
OUTER = b'outer-source-fixture'
INNER = b'inner-source-fixture'
LEAF = b'nested archive leaf\r\nUTF-8: \xe6\x97\xa5\xe6\x9c\xac\n'


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def zip_bytes(entries):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, data in entries:
            item = zipfile.ZipInfo(name, (2024, 1, 1, 0, 0, 0))
            item.create_system = 0
            item.external_attr = 0x10 if name.endswith('/') else 0x20
            archive.writestr(item, data)
    return stream.getvalue()


def generate():
    target = ROOT / 'inputs'
    target.mkdir(exist_ok=False)
    with zipfile.ZipFile(SOURCE / 'two-passwords.zip') as archive:
        for entry in archive.infolist():
            archive.read(entry, pwd=OUTER)
        inner = archive.read('inner.zip', pwd=OUTER)
    with zipfile.ZipFile(io.BytesIO(inner)) as archive:
        assert archive.read('leaf.txt', pwd=INNER) == LEAF
    files = {
        'siblings.zip': zip_bytes([('left.zip', inner), ('right.zip', inner), ('after.txt', b'sibling tail\n')]),
        'dangerous.zip': zip_bytes([('../leaf.txt', b'unsafe leaf\n'), ('after.txt', b'tail checked\n')]),
    }
    generated = []
    for name, data in files.items():
        (target / name).write_bytes(data)
        generated.append({'path': 'inputs/' + name, 'bytes': len(data), 'sha256': sha(data)})
    names = ['two-passwords.zip', 'plain-encrypted.zip', 'leaf.zip', 'late-bad-sibling.zip', 'depth-8.zip', 'depth-9.zip', 'root-a.zip']
    pins = []
    for name in names + ['README.md', 'regenerate.py', 'manifest.json', '.gitattributes']:
        data = (SOURCE / name).read_bytes()
        pins.append({'path': 'tests/Fixtures/Archives/Sources/' + name, 'bytes': len(data), 'sha256': sha(data)})
    manifest = {
        'schema': 'input301-fixture-v1', 'license': 'CC0-1.0',
        'literalLeafHex': LEAF.hex(), 'literalLeafText': LEAF.decode('utf8'),
        'reusedSource': 'tests/Fixtures/Archives/Sources/README.md',
        'reusedPins': pins, 'generated': generated,
        'cases': [
            {'id': 'password-retry', 'accepted': True, 'root': 'two-passwords.zip', 'chain': ['inner.zip'], 'leaf': 'leaf.txt'},
            {'id': 'siblings', 'accepted': True, 'root': 'siblings.zip', 'chain': ['right.zip'], 'leaf': 'leaf.txt'},
            {'id': 'root-clear', 'accepted': False},
            {'id': 'password-limit', 'accepted': False},
            {'id': 'late-crc', 'accepted': False},
            {'id': 'dangerous', 'accepted': False},
            {'id': 'directory', 'accepted': False},
            {'id': 'missing', 'accepted': False},
            {'id': 'depth8', 'accepted': True, 'root': 'depth-8.zip', 'chain': ['inner.zip'] * 8, 'leaf': 'leaf.txt'},
            {'id': 'depth9', 'accepted': False},
            {'id': 'root-budget', 'accepted': False},
            {'id': 'work-budget', 'accepted': False},
            {'id': 'link', 'accepted': False},
            {'id': 'stale-root', 'accepted': False},
            {'id': 'close-pending', 'accepted': False},
        ],
    }
    with (ROOT / 'manifest.json').open('x', encoding='utf8', newline='\n') as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
        stream.write('\n')


if __name__ == '__main__':
    generate()
