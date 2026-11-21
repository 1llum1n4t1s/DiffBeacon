"""Literal stdlib-only flat/nested Archive originals; no product golden."""
import hashlib,io,json,pathlib,zipfile
ROOT=pathlib.Path(__file__).resolve().parent
ROLES=('left','middle','right')
TEXTS=('shared café\r\nLEFT €\nend-left\r','shared café\r\nMIDDLE £\nend-middle\r','shared café\r\nRIGHT “quote”\nend-right\r')
CODECS=('utf-8','utf-16-le','utf-8'); BOMS=(b'\xef\xbb\xbf',b'\xff\xfe',b'')
def zip_bytes(entries):
    buffer=io.BytesIO()
    with zipfile.ZipFile(buffer,'w',zipfile.ZIP_STORED) as archive:
        archive.comment=b'synthetic-input348-literal'
        for name,raw in entries:
            info=zipfile.ZipInfo(name,(2024,1,1,0,0,0)); info.external_attr=0o100644<<16; archive.writestr(info,raw)
    return buffer.getvalue()
def main():
    inputs=ROOT/'inputs'; inputs.mkdir(exist_ok=False)
    for shape in ('flat','nested'):
        folder=inputs/shape; folder.mkdir()
        for side,role in enumerate(ROLES):
            raw=BOMS[side]+TEXTS[side].encode(CODECS[side])
            archive=zip_bytes([('docs/leaf.txt',raw),('docs/other.bin',bytes([side,0,255,13,10]))])
            if shape=='nested':
                archive=zip_bytes([('deep.zip',archive),('inner.bin',bytes([side,22,0]))])
                archive=zip_bytes([('inner.zip',archive),('outer.bin',bytes([side,44,0]))])
            (folder/(role+'.zip')).write_bytes(archive)
    pins={str(p.relative_to(ROOT)).replace('\\','/'):{'size':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(inputs.rglob('*.zip'))}
    (ROOT/'manifest.json').write_text(json.dumps({'schema':'input348-literal-fixture-v1','inputs':pins},indent=2)+'\n',encoding='utf-8')
if __name__=='__main__': main()
