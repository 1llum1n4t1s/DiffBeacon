"""Synthetic literal originals, never derived from product output."""
import hashlib, io, json, pathlib, zipfile
ROOT = pathlib.Path(__file__).resolve().parent
ROLES = ('left', 'middle', 'right')
TEXTS = ('shared café\r\nLEFT €\nend-left\r', 'shared café\r\nMIDDLE £\nend-middle\r', 'shared café\r\nRIGHT “quote”\nend-right\r')
EDITS = tuple(t.replace('end-', 'edited-') for t in TEXTS)
CODECS = ('utf-8', 'utf-16-le', 'utf-8')
BOMS = (b'\xef\xbb\xbf', b'\xff\xfe', b'')
def encoded(text, side): return BOMS[side] + text.encode(CODECS[side])
def main():
    inputs = ROOT / 'inputs'; inputs.mkdir(exist_ok=False)
    for side, role in enumerate(ROLES):
        (inputs / (role + '.txt')).write_bytes(encoded(TEXTS[side], side))
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w', zipfile.ZIP_STORED) as archive:
            archive.comment = b'synthetic-input322-literal'
            for name, data in [('docs/leaf.txt', encoded(TEXTS[side], side)), ('docs/other.bin', bytes([side, 0, 255, 13, 10]))]:
                info = zipfile.ZipInfo(name, (2024, 1, 1, 0, 0, 0)); info.external_attr = 0o100644 << 16
                archive.writestr(info, data)
        (inputs / (role + '.zip')).write_bytes(stream.getvalue())
    pins = {str(p.relative_to(ROOT)).replace('\\', '/'): {'size': p.stat().st_size, 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(inputs.iterdir())}
    (ROOT / 'manifest.json').write_text(json.dumps({'schema': 'input322-fixtures-v1', 'inputs': pins}, indent=2) + '\n', encoding='utf-8')
if __name__ == '__main__': main()
