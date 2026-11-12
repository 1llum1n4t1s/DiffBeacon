"""初回の二回独立採取をfixtureとして固定する。既存ファイルは上書きしない。"""
import sys
sys.dont_write_bytecode = True
import argparse, json, pathlib, zipfile
from capture import BASE, sha, validate, write, js, load

parser = argparse.ArgumentParser()
parser.add_argument('--run1', required=True, type=pathlib.Path)
parser.add_argument('--run2', required=True, type=pathlib.Path)
args = parser.parse_args()
load()
runs = [validate(args.run1), validate(args.run2)]
assert runs[0] != runs[1]
raw = [(path/'golden.ndjson').read_bytes() for path in runs]
assert raw[0] == raw[1]
for path in runs:
    receipt = json.loads((path/'receipt.json').read_bytes())
    compile_receipt = json.loads((path/'compile-receipt.json').read_bytes())
    assert receipt['exitCode'] == compile_receipt['exitCode'] == 0
    assert receipt['heldProcessHandleSignaled'] and receipt['stdoutEof'] and receipt['stderrEof']
    assert receipt['stdoutSha256'] == sha(raw[0])
    assert receipt['adapterSha256'] == sha((BASE/'adapter.cpp').read_bytes())
    assert receipt['casePlanSha256'] == sha((BASE/'case-plan.json').read_bytes())
    assert not (path/'reference.stderr').read_bytes()
    assert receipt['executableSha256'] == sha((path/'reference.exe').read_bytes())
    for name, digest in compile_receipt['inputSha256'].items(): assert sha((path/name).read_bytes()) == digest
write(BASE/'golden.ndjson', raw[0])
with zipfile.ZipFile(BASE/'native-proof.zip', 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for index, path in enumerate(runs,1):
        files = [item for item in path.rglob("*") if item.is_file()]
        for item in sorted(files):
            # objectは再生成可能。exe・source・全ログとreceiptを固定して照合可能にする。
            if item.suffix == '.obj': continue
            info = zipfile.ZipInfo('run'+str(index)+'/'+item.relative_to(path).as_posix(), (2026,10,17,0,0,0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, item.read_bytes())
js(BASE/'fixed-manifest.json', dict(schema=1, files={item.name:dict(size=item.stat().st_size, sha256=sha(item.read_bytes()))
    for item in sorted(BASE.iterdir()) if item.is_file() and item.name != 'fixed-manifest.json'}))
print(json.dumps(dict(rows=len(raw[0].splitlines()), goldenSha256=sha(raw[0]), proofSha256=sha((BASE/'native-proof.zip').read_bytes()))))
