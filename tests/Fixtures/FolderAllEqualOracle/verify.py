"""製品やcapture実装をimportしない独立reader。固定原典・採取証拠・型付き入力を照合する。"""
import sys
sys.dont_write_bytecode = True
import argparse, hashlib, json, pathlib, zipfile
BASE = pathlib.Path(__file__).resolve().parent

def sha(raw): return hashlib.sha256(raw).hexdigest()
def read_json(path): return json.loads(path.read_bytes())

def expected(case):
    # JSONの型名を比較するためPython自身のbool/intやint/doubleの緩い==に依存しない。
    def equal(left, right):
        if left['type'] != right['type']: return False
        if left['type'] == 'none': return True
        if left['type'] == 'array':
            return len(left['value']) == len(right['value']) and all(equal(a,b) for a,b in zip(left['value'],right['value']))
        return left['value'] == right['value']
    trace = []
    def evaluate(index):
        trace.append(index)
        if case['args'][index].get('throws'): raise RuntimeError('argument-'+str(index))
        return case['args'][index]['value']
    result = None; error = ''
    try:
        first = evaluate(0)
        if first['type'] == 'array':
            items = first['value']
            result = all(equal(items[0], value) for value in items[1:])
        else:
            result = all(equal(first,evaluate(index)) for index in range(1,len(case['args'])))
    except RuntimeError as exception: error = str(exception)
    return dict(id=case['id'], result=result, error=error, evaluated=trace)

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--observed', type=pathlib.Path)
    args = parser.parse_args()
    fixed = read_json(BASE/'fixed-manifest.json')['files']
    assert {item.name for item in BASE.iterdir() if item.is_file()} == set(fixed)|{'fixed-manifest.json'}
    for name, record in fixed.items():
        raw = (BASE/name).read_bytes()
        assert len(raw) == record['size'] and sha(raw) == record['sha256'], name
    manifest = read_json(BASE/'source-manifest.json')
    assert manifest['commit'] == '6780924ba35f7f55af0f6f14dd302561175830a6'
    data = {}
    with zipfile.ZipFile(BASE/'reference-source.zip') as archive:
        assert archive.testzip() is None
        assert set(archive.namelist()) == {'original/'+record['path'] for record in manifest['sources']}
        for record in manifest['sources']:
            raw = archive.read('original/'+record['path']); data[record['path']] = raw
            assert len(raw) == record['size'] and sha(raw) == record['sha256']
            assert hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest() == record['blob']
    extracted = {}
    for span in read_json(BASE/'provenance.json'):
        raw = data[span['path']]; start = span['startByte']; end = span['endByte']; piece = raw[start:end]
        assert sha(piece) == span['sha256'] and 0 <= start < end <= len(raw)
        assert raw[:start].count(b'\n')+1 == span['startLine'] and raw[:end].count(b'\n')+1 == span['endLine']
        extracted.setdefault(span['output'], b''); extracted[span['output']] += piece+b'\n'
    assert b'AllEqualFunc' in extracted['all-equal.inc'] and b'valueEquals' in extracted['all-equal.inc']
    assert b'"allequal"' in extracted['registration-evidence.txt']
    plan = read_json(BASE/'case-plan.json')
    assert len(plan) == len({row['id'] for row in plan}) and all(row['args'] for row in plan)
    golden = (BASE/'golden.ndjson').read_bytes()
    rows = [json.loads(line) for line in golden.splitlines()]
    assert rows == [expected(case) for case in plan], 'native result/evaluation order differs from independent typed reader'
    births = []
    with zipfile.ZipFile(BASE/'native-proof.zip') as archive:
        assert archive.testzip() is None
        assert len(archive.namelist()) == len(set(archive.namelist()))
        for index in [1,2]:
            prefix = 'run'+str(index)+'/'
            def get(name): return archive.read(prefix+name)
            receipt = json.loads(get('receipt.json')); compilation = json.loads(get('compile-receipt.json'))
            assert receipt['exitCode'] == compilation['exitCode'] == 0
            assert receipt['heldProcessHandleSignaled'] and receipt['stdoutEof'] and receipt['stderrEof']
            assert get('golden.ndjson') == golden and not get('reference.stderr')
            assert receipt['stdoutSha256'] == sha(golden) and receipt['caseCount'] == len(plan)
            assert receipt['adapterSha256'] == sha((BASE/'adapter.cpp').read_bytes())
            assert receipt['casePlanSha256'] == sha((BASE/'case-plan.json').read_bytes())
            assert receipt['executableSha256'] == sha(get('reference.exe'))
            assert get('adapter.cpp') == (BASE/'adapter.cpp').read_bytes()
            for name, raw in extracted.items(): assert get(name) == raw, prefix+name
            for name, digest in compilation['inputSha256'].items(): assert sha(get(name)) == digest, prefix+name
            births.append((receipt['pid'],receipt['birthFileTime']))
        assert births[0] != births[1], 'independent child processes required'
    if args.observed:
        assert args.observed.resolve().is_file()
        assert args.observed.read_bytes() == golden, 'new native stdout differs from frozen raw stdout'
    print(json.dumps(dict(cases=len(rows), goldenSha256=sha(golden), independentRuns=2,
                         checked='raw blobs/spans/native receipts/stdout/results/evaluation order')))
if __name__ == '__main__': main()
