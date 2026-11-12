"""Independent bytes/source/CLI observation reader. No regex engine or expected-value calculator."""
import argparse
import hashlib
import json
import pathlib
import zipfile
BASE = pathlib.Path(__file__).resolve().parent
SOURCE_SHA = '882952f28801756a05dca55fd146ddd706c4b2a3fa653b8e2f5ea76a60bc35bf'
GOLDEN_SHA = '1e656f041600072588ac168fc4ad546eb2abbea78ca7e762c36b6e17ef5598ff'
def sha(data): return hashlib.sha256(data).hexdigest()
def require(value, reason):
    if not value: raise ValueError(reason)
def main():
    ap = argparse.ArgumentParser(); ap.add_argument('--actual', type=pathlib.Path); args = ap.parse_args()
    manifest = json.loads((BASE/'fixed-manifest.json').read_bytes())
    require({p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file()} == set(manifest['files'])|{'fixed-manifest.json'}, 'fixed fileset')
    for name, item in manifest['files'].items():
        data = (BASE/name).read_bytes()
        require(len(data) == item['size'] and sha(data) == item['sha256'], 'fixed bytes '+name)
    source = BASE.parent/'LineTransformRegex/reference-source.zip'
    require(sha(source.read_bytes()) == SOURCE_SHA, 'native source zip')
    golden = (BASE/'golden.ndjson').read_bytes(); require(sha(golden) == GOLDEN_SHA, 'golden bytes')
    rows = [json.loads(line) for line in golden.splitlines()]
    cases = json.loads((BASE/'cases.json').read_bytes())
    require(len(rows) == 106 and [r['id'] for r in rows] == [c['id'] for c in cases], 'case order/count')
    with zipfile.ZipFile(source) as sources, zipfile.ZipFile(BASE/'native-proof.zip') as proof:
        require(sources.testzip() is None and proof.testzip() is None, 'source/proof CRC')
        for run in ['first', 'second']:
            prefix = run+'/'
            receipt = json.loads(proof.read(prefix+'receipt.json'))
            require(receipt['exitCode'] == 0 and not receipt['timeout'] and receipt['stdoutEof'] and receipt['stderrEof'] and receipt['heldProcessHandleSignaled'], 'native process terminal/EOF')
            require(receipt['pid'] > 0 and receipt['birthFileTime'] > 0 and receipt['caseCount'] == 106, 'native process identity/count')
            require(proof.read(prefix+'golden.ndjson') == golden and receipt['stdoutSha256'] == GOLDEN_SHA and not proof.read(prefix+'reference.stderr'), 'native stdout/stderr')
            provenance = json.loads(proof.read(prefix+'provenance.json'))
            require(provenance['sourceZipSha256'] == SOURCE_SHA, 'source provenance')
            for span in provenance['spans']:
                original = sources.read('original/'+span['path'])
                body = original[span['startByte']:span['endByte']]
                require(sha(original) == span['sourceSha256'] and sha(body) == span['sha256'] and proof.read(prefix+span['file']) == body+b'\n', 'unchanged span '+span['file'])
            require(proof.read(prefix+'adapter.cpp') == (BASE/'adapter.cpp').read_bytes(), 'compiled adapter')
            header = (BASE.parent/'LineTransformArrays/original/FilterParser.h').read_bytes()
            require(sha(header) == provenance['parserHeaderSha256'] and header == proof.read(prefix+'FilterParser.h'), 'fixed token header')
        require(json.loads(proof.read('first/receipt.json'))['pid'] != json.loads(proof.read('second/receipt.json'))['pid'], 'independent native processes')
    for case, row in zip(cases, rows):
        if row['error']:
            require(row['value'] is None and row['phase'] in ['optimize','evaluate'], 'error phase')
        else:
            require(not row['phase'] and row['value']['kind'] in ['bool','none','array'], 'native typed result')
        if not case['optimize']:
            require(row['evaluated'] == ([0] if case['id'] == 'left-throw' else [0,1]), 'left-to-right evaluation')
    observed = 0
    if args.actual:
        actual = json.loads(args.actual.read_bytes())
        require([x['id'] for x in actual] == [c['id'] for c in cases], 'public coverage/order')
        for case, row, observation in zip(cases, rows, actual):
            require(observation['expression'] == case['expression'] and observation['passed'], 'public expression/result')
            require(not observation['actualTypedValueObserved'] and not observation['nativeExceptionEquivalent'], 'public evidence boundary')
            if row['error']:
                require(observation['exitCode'] == 2 and not observation['stdout'] and observation['stderr'], 'public rejection')
            else:
                expected = bytes.fromhex(row['serializedUtf8Hex']).decode('utf-8')
                require(observation['expectedSerializedKey'] == expected and observation['exitCode'] == 0 and not observation['stderr'], 'native-derived key/exit')
                require(json.loads(observation['stdout'])['different'] is False, 'public indirect key comparison')
            observed += 1
    print(json.dumps(dict(cases=len(cases), nativeSuccesses=100, nativeErrors=6, nativeCompiles=2,
                          goldenSha256=GOLDEN_SHA, publicObservations=observed)))
if __name__ == '__main__': main()
