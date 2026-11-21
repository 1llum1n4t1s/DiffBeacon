"""初回の二回独立採取を固定する。既存fixtureを上書きしない。"""
import sys
sys.dont_write_bytecode = True
import argparse, json, pathlib, zipfile
from capture import BASE, inputs, load, sha, validate, write, js
ap=argparse.ArgumentParser();ap.add_argument('--run1',type=pathlib.Path,required=True);ap.add_argument('--run2',type=pathlib.Path,required=True);a=ap.parse_args()
load();runs=[validate(a.run1),validate(a.run2)]
raw=[(p/'golden.ndjson').read_bytes() for p in runs];assert raw[0]==raw[1]
for p in runs:
    r=json.loads((p/'receipt.json').read_bytes());c=json.loads((p/'compile-receipt.json').read_bytes())
    assert r['exitCode']==0 and c['exitCode']==0 and r['heldProcessHandleSignaled'] and r['stdoutEof'] and r['stderrEof']
    assert r['stdoutSha256']==sha(raw[0]) and r['adapterSha256']==sha((BASE/'adapter.cpp').read_bytes())
    assert not (p/'reference.stderr').read_bytes()
write(BASE/'golden.ndjson',raw[0])
with zipfile.ZipFile(BASE/'input.zip','x',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for n,b in inputs().items():
        zi=zipfile.ZipInfo(n,(2026,10,17,0,0,0));zi.compress_type=zipfile.ZIP_DEFLATED;z.writestr(zi,b)
js(BASE/'input-manifest.json',dict(mtimeNanoseconds='1700000000123456700',files=[dict(name=n,size=len(b),sha256=sha(b)) for n,b in inputs().items()]))
with zipfile.ZipFile(BASE/'native-proof.zip','x',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for index,p in enumerate(runs,1):
        files=[x for x in p.iterdir() if x.suffix in ['.inc','.cpp','.rsp','.cmd'] or x.name in ['compile.stdout','compile.stderr','compile-receipt.json','receipt.json','golden.ndjson','reference.stderr']]
        for f in sorted(files):
            zi=zipfile.ZipInfo('run'+str(index)+'/'+f.name,(2026,10,17,0,0,0));zi.compress_type=zipfile.ZIP_DEFLATED;z.writestr(zi,f.read_bytes())
rows=[json.loads(x) for x in raw[0].splitlines()]
js(BASE/'case-plan.json',[dict(id=r['id'],scope=('typed-conversion' if r['id'].startswith('convert-') else 'truth-predicate' if r['id'].startswith('truth-') else 'context-adapter-native-prop' if r['id'].startswith('side-') else 'real-windows-services' if r['id'].startswith(('file-','cache-')) else 'canonical-and-argument-validation')) for r in rows])
print(json.dumps(dict(rows=len(rows),goldenSha256=sha(raw[0]),proofSha256=sha((BASE/'native-proof.zip').read_bytes()))))
