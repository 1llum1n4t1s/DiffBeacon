"""Plain-Python byte/CRC/source reader. It does not evaluate a glob or import product code."""
import argparse
import hashlib
import json
import pathlib
import zipfile

BASE = pathlib.Path(__file__).resolve().parent
def sha(data): return hashlib.sha256(data).hexdigest()
def read_json(data): return json.loads(data.decode('utf-8-sig'))
def records(data):
    result = [json.loads(line) for line in data.splitlines()]
    assert len({x['id'] for x in result}) == len(result), 'duplicate ID'
    return result

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--actual', type=pathlib.Path)
    args = ap.parse_args()
    fixed = read_json((BASE/'fixed-manifest.json').read_bytes())
    files = sorted(p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file())
    assert files == sorted([*fixed['files'], 'fixed-manifest.json']), 'fixed file set differs'
    for name, expected in fixed['files'].items():
        assert sha((BASE/name).read_bytes()) == expected, name
    with zipfile.ZipFile(BASE/'reference-source.zip') as archive:
        assert archive.testzip() is None
        assert len(set(archive.namelist())) == len(archive.namelist())
        source = {name[9:]: archive.read(name) for name in archive.namelist()}
    manifest = read_json((BASE/'source-manifest.json').read_bytes())
    assert sorted(source) == sorted(x['path'] for x in manifest['sources'])
    for item in manifest['sources']:
        data = source[item['path']]
        assert len(data) == item['size'] and sha(data) == item['sha256'], item['path']
        assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest() == item['gitBlob']
    cases = read_json((BASE/'cases.json').read_bytes())
    golden = (BASE/'golden.ndjson').read_bytes()
    expected = records(golden)
    assert [r['id'] for r in expected] == [c['id'] for c in cases]
    for result in expected:
        assert result['status'] in ('ok', 'error')
        if result['status'] == 'ok': assert type(result['value']) is bool
        else: assert result['errorType'] == 'SyntaxException'
    by_id = {r['id']: r for r in expected}
    # These requirements were recorded from source before the native observation.
    for name, value in [('invalid-b', False), ('invalid-neg-b', True), ('empty-both', True),
                        ('invalid-empty', False), ('terminal-backslash-empty', False),
                        ('question-bmp', True), ('question-astral', True), ('question-combining', False)]:
        assert by_id[name] == dict(id=name, status='ok', value=value), name
    assert by_id['invalid-a']['status'] == 'error'
    receipts = read_json((BASE/'capture.json').read_bytes())
    assert len(receipts) == 2 and receipts[0]['pid'] != receipts[1]['pid']
    with zipfile.ZipFile(BASE/'native-proof.zip') as proof:
        assert proof.testzip() is None
        names = proof.namelist()
        assert len(names) == len(set(names))
        for index, run in enumerate(['run-1', 'run-2']):
            def get(name): return proof.read(run+'/'+name)
            receipt = read_json(get('receipt.json'))
            assert receipt == receipts[index]
            assert receipt['pid'] > 0 and receipt['birthFileTime'] > 0
            assert receipt['exitCode'] == 0 and not receipt['timeout']
            assert all(receipt[x] for x in ['heldProcessHandleSignaled', 'stdoutEof', 'stderrEof'])
            assert receipt['caseCount'] == len(cases)
            assert get('golden.ndjson') == golden and get('reference.stderr') == b''
            assert receipt['stdoutSha256'] == sha(golden)
            assert receipt['stderrSha256'] == sha(b'')
            assert get('adapter.cpp') == (BASE/'adapter.cpp').read_bytes()
            for path in (BASE/'support').rglob('*.h'):
                assert get('include/'+path.relative_to(BASE/'support').as_posix()) == path.read_bytes()
            for name in ['Glob', 'TextIterator', 'UTF8Encoding', 'Unicode']:
                assert get('include/Poco/'+name+'.h') == source['Externals/poco/Foundation/include/Poco/'+name+'.h']
            spans = read_json(get('extractions.json'))
            bodies = []
            for span in spans:
                data = source[span['path']][span['startByte']:span['endByte']]
                assert sha(data) == span['sha256']
                bodies.append(data)
            assert get('glob.inc') == b'\n\n'.join(bodies)+b'\n'
            def literal(value): return 'bytes({'+','.join(str(b) for b in value.encode('utf-8'))+'})'
            inputs = '\n'.join('run('+json.dumps(c['id'])+','+literal(c['subject'])+','+
                literal(c['pattern'])+','+str(c['caseSensitive']).lower()+');' for c in cases)+'\n'
            assert get('cases.inc') == inputs.encode('utf-8')
    if args.actual:
        assert records(args.actual.read_bytes()) == expected, 'actual observations differ'
    print(json.dumps(dict(verified=True, cases=len(cases), sourceFiles=len(source),
        nativeRuns=2, goldenSha256=sha(golden), actualCompared=bool(args.actual))))

if __name__ == '__main__': main()
