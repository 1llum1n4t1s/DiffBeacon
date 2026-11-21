"""Tiny independent synthetic lifetime fixtures; refuses existing outputs."""
import hashlib, io, json, pathlib, zipfile
ROOT = pathlib.Path(__file__).resolve().parent
TEXTS = ('lifetime left café\r\nleft tail\n', 'lifetime middle £\r\nmiddle tail\n', 'lifetime right €\r\nright tail\n')

def main():
    inputs = ROOT / 'inputs'; inputs.mkdir(exist_ok=True)
    pins = []
    for side, role in enumerate(('left', 'middle', 'right')):
        raw = TEXTS[side].encode('utf-8')
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_STORED) as archive:
            entry = zipfile.ZipInfo('leaf.txt', (2024, 1, 1, 0, 0, 0)); entry.external_attr = 0o100644 << 16
            archive.writestr(entry, raw)
        for name, data in ((role + '.txt', raw), (role + '.zip', stream.getvalue())):
            path = inputs / name
            if path.exists(): raise FileExistsError(path)
            path.write_bytes(data)
            pins.append({'path': 'inputs/' + name, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest().upper()})
    (ROOT / 'manifest.json').write_text(json.dumps({'schema':'independent-text-input-lifetime-fixture-v1', 'source':'generate.py synthetic literals; not WinMerge golden', 'license':'MIT', 'files':pins, 'texts':TEXTS, 'recipes':['PPP','UUU','AAA','APU','UAP','PUA']},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
if __name__ == '__main__': main()
