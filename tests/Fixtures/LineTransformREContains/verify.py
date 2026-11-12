"""Independent bytes/source/CLI observation reader. No regex engine or expected-value calculator."""
import argparse
import hashlib
import json
import pathlib
import zipfile
BASE = pathlib.Path(__file__).resolve().parent
SOURCE_SHA = '882952f28801756a05dca55fd146ddd706c4b2a3fa653b8e2f5ea76a60bc35bf'
GOLDEN_SHA = 'f328780edd93b12a1dc7db93c0b4bbe7f201a96209b2be0e59b569fec987ab98'
CONTENT_SOURCE_SHA = '492581a3e1803f4ec6f3a06b0cfeedf2574f25cd24d8182fff4698fb64b0c0db'
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
    require(len(rows) == 1140 and [r['id'] for r in rows] == [c['id'] for c in cases], 'case order/count')
    with zipfile.ZipFile(source) as sources, zipfile.ZipFile(BASE/'content-source.zip') as content_sources, zipfile.ZipFile(BASE/'native-proof.zip') as proof:
        require(sources.testzip() is None and content_sources.testzip() is None and proof.testzip() is None, 'source/proof CRC')
        require(sha((BASE/'content-source.zip').read_bytes()) == CONTENT_SOURCE_SHA, 'content source bytes')
        source_manifest = json.loads((BASE/'content-source-manifest.json').read_bytes())
        require(source_manifest['commit'] == '6780924ba35f7f55af0f6f14dd302561175830a6'
                and set(content_sources.namelist()) == set(source_manifest['files']), 'content source commit/fileset')
        for name, entry in source_manifest['files'].items():
            body = content_sources.read(name)
            require(len(body) == entry['size'] and sha(body) == entry['sha256']
                    and hashlib.sha1(b'blob '+str(len(body)).encode()+b'\0'+body).hexdigest() == entry['gitBlob'], 'raw Git source '+name)
        for run in ['first', 'second']:
            prefix = run+'/'
            receipt = json.loads(proof.read(prefix+'receipt.json'))
            require(receipt['exitCode'] == 0 and not receipt['timeout'] and receipt['stdoutEof'] and receipt['stderrEof'] and receipt['heldProcessHandleSignaled'], 'native process terminal/EOF')
            require(receipt['pid'] > 0 and receipt['birthFileTime'] > 0 and receipt['caseCount'] == 1140, 'native process identity/count')
            require(proof.read(prefix+'golden.ndjson') == golden and receipt['stdoutSha256'] == GOLDEN_SHA and not proof.read(prefix+'reference.stderr'), 'native stdout/stderr')
            provenance = json.loads(proof.read(prefix+'provenance.json'))
            require(provenance['sourceZipSha256'] == SOURCE_SHA, 'source provenance')
            for span in provenance['spans']:
                original = (content_sources if 'original/'+span['path'] in content_sources.namelist() else sources).read('original/'+span['path'])
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
    indexed = {r['id']:r for r in rows}
    for encoding in ['utf8','utf8bom','utf16le','utf16be']:
        require(indexed[f'content-{encoding}-no-eol-empty-anchor-True']['searchedUtf8Hex'] == ['616263',''], 'Unicode final body and cleared EOF')
        require(indexed[f'content-{encoding}-empty-empty-anchor-True']['searchedUtf8Hex'] == [''], 'empty reader EOF')
        require(indexed[f'content-{encoding}-bom-only-empty-anchor-True']['searchedUtf8Hex'] == [''], 'BOM-only reader EOF')
    require(indexed['content-cp1252-no-eol-empty-anchor-True']['searchedUtf8Hex'] == ['616263'], 'NONE final non-EOL body returns false')
    require(indexed['content-cp1252-final-lf-empty-anchor-True']['searchedUtf8Hex'] == ['616263',''], 'NONE trailing EOL allows EOF')
    require(indexed['content-utf8-mixed-eol-True']['searchedUtf8Hex'] == ['616263','646566','676869','6a6b6c',''], 'physical body CR/LF/CRLF trace')
    observed = 0
    if args.actual:
        public = [(c,r) for c,r in zip(cases,rows) if c['public']]
        actual = json.loads(args.actual.read_bytes())
        require([x['id'] for x in actual] == [c['id'] for c,r in public], 'public coverage/order')
        for case, row, observation in ((c,r,o) for (c,r),o in zip(public,actual)):
            require(observation['expression'] == case['expression'] and observation['passed'], 'public expression/result')
            require(not observation['actualTypedValueObserved'] and not observation['nativeExceptionEquivalent'], 'public evidence boundary')
            if row['error']:
                require(observation['exitCode'] == 2 and not observation['stdout'] and observation['stderr'], 'public rejection')
            else:
                expected = bytes.fromhex(row['serializedUtf8Hex']).decode('utf-8')
                require(observation['expectedSerializedKey'] == expected and observation['exitCode'] == 0 and not observation['stderr'], 'native-derived key/exit')
                require(json.loads(observation['stdout'])['different'] is False, 'public indirect key comparison')
            observed += 1
    print(json.dumps(dict(cases=len(cases), nativeSuccesses=sum(not r['error'] for r in rows), nativeErrors=sum(bool(r['error']) for r in rows), nativeCompiles=2,
                          goldenSha256=GOLDEN_SHA, publicObservations=observed)))
if __name__ == '__main__': main()
