"""CC0: 固定ZipCryptoのlocal/centralを複数entryへ拡張する独立encoder。"""
import hashlib
import io
import json
import pathlib
import struct
import zipfile
import zlib

ROOT = pathlib.Path(__file__).resolve().parent
REPO = ROOT.parents[2]
OUTER = b'input306-public-outer'
LEFT = b'input306-public-left'
RIGHT = b'input306-public-right'
LEFT_TEXT = b'left cipher leaf\r\nUTF-8: \xe6\x97\xa5\xe6\x9c\xac\n'
RIGHT_TEXT = b'right cipher leaf\nUTF-8: \xe5\x8f\xb3\r\n'


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def encrypt(data, password):
    table = []
    for value in range(256):
        for _ in range(8):
            value = (value >> 1) ^ (0xEDB88320 if value & 1 else 0)
        table.append(value)
    keys = [0x12345678, 0x23456789, 0x34567890]
    def update(value):
        keys[0] = (keys[0] >> 8) ^ table[(keys[0] ^ value) & 255]
        keys[1] = ((keys[1] + (keys[0] & 255)) * 134775813 + 1) & 0xFFFFFFFF
        keys[2] = (keys[2] >> 8) ^ table[(keys[2] ^ (keys[1] >> 24)) & 255]
    for value in password:
        update(value)
    plain = bytes(range(11)) + bytes([zlib.crc32(data) >> 24]) + data
    cipher = bytearray()
    for value in plain:
        temporary = keys[2] | 2
        cipher.append(value ^ ((temporary * (temporary ^ 1) >> 8) & 255))
        update(value)
    return bytes(cipher)


def encrypted_zip(entries, password):
    local, central = bytearray(), bytearray()
    # 2024-01-01 00:00:00 DOS timestamp。headerの12byte目はCRC上位byte。
    date = ((2024 - 1980) << 9) | (1 << 5) | 1
    for name, data in entries:
        encoded = name.encode('ascii')
        cipher = encrypt(data, password)
        crc, start = zlib.crc32(data), len(local)
        local.extend(struct.pack('<I5H3I2H', 0x04034B50, 20, 1, 0, 0, date, crc, len(cipher), len(data), len(encoded), 0))
        local.extend(encoded + cipher)
        central.extend(struct.pack('<I6H3I5H2I', 0x02014B50, 20, 20, 1, 0, 0, date, crc, len(cipher), len(data), len(encoded), 0, 0, 0, 0, 0, start))
        central.extend(encoded)
    trailer = struct.pack('<I4H2IH', 0x06054B50, 0, 0, len(entries), len(entries), len(central), len(local), 0)
    result = bytes(local + central + trailer)
    with zipfile.ZipFile(io.BytesIO(result)) as archive:
        assert [(i.filename, archive.read(i, pwd=password)) for i in archive.infolist()] == entries
        assert all(i.flag_bits == 1 for i in archive.infolist())
    return result


def fixture_bytes():
    left = encrypted_zip([('leaf.txt', LEFT_TEXT), ('empty.txt', b''), ('tail.txt', b'left later sibling\n')], LEFT)
    right = encrypted_zip([('leaf.txt', RIGHT_TEXT), ('empty.txt', b''), ('tail.txt', b'right later sibling\r\n')], RIGHT)
    return encrypted_zip([('left.zip', left), ('right.zip', right), ('after.txt', b'encrypted outer later sibling\n')], OUTER), left, right


def generate():
    folder = ROOT / 'inputs'
    folder.mkdir(exist_ok=False)
    root, left, right = fixture_bytes()
    (folder / 'cipher-siblings.zip').write_bytes(root)
    source = REPO / 'tests/Fixtures/Archives/Sources/regenerate.py'
    metadata = {
        'schema': 'input306-fixture-v1', 'license': 'CC0-1.0',
        'root': {'path': 'inputs/cipher-siblings.zip', 'bytes': len(root), 'sha256': sha(root)},
        'leftContainer': {'bytes': len(left), 'sha256': sha(left)},
        'rightContainer': {'bytes': len(right), 'sha256': sha(right)},
        'leftLiteralHex': LEFT_TEXT.hex(), 'rightLiteralHex': RIGHT_TEXT.hex(),
        'source': {'path': str(source.relative_to(REPO)).replace('\\', '/'), 'bytes': source.stat().st_size, 'sha256': sha(source.read_bytes()), 'license': 'CC0-1.0', 'referenceLines': '35-71;172'},
        'case': {'id': 'encrypted-outer-different-inner-siblings', 'chain': ['right.zip'], 'leaf': 'leaf.txt', 'accepted': True},
    }
    with (ROOT / 'manifest.json').open('x', encoding='utf8', newline='\n') as stream:
        json.dump(metadata, stream, ensure_ascii=False, indent=2)
        stream.write('\n')


if __name__ == '__main__':
    generate()
