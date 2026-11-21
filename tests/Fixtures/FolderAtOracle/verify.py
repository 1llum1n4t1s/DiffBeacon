"""製品やcapture実装をimportしない独立reader。固定原典・採取証拠・型付き入力を照合する。"""
import sys
sys.dont_write_bytecode = True
import argparse, hashlib, json, pathlib, zipfile
BASE = pathlib.Path(__file__).resolve().parent

def sha(raw): return hashlib.sha256(raw).hexdigest()
def read_json(path): return json.loads(path.read_bytes())

def validate_shape(value):
    assert type(value) is dict
    kind = value['type']
    assert set(value) == ({'type'} if kind == 'none' else {'type','value'})
    if kind == 'none': return
    scalar = value['value']
    if kind == 'array':
        assert type(scalar) is list
        for item in scalar: validate_shape(item)
    elif kind in ['integer','timestamp']: assert type(scalar) is int and -(1<<63) <= scalar < (1<<63)
    elif kind == 'double': assert type(scalar) in [int,float]
    elif kind == 'boolean': assert type(scalar) is bool
    elif kind in ['string','regex','content']: assert type(scalar) is str; scalar.encode('utf8',errors='strict')
    else: raise AssertionError(kind)

def logical_truth(value):
    if value['type'] == 'boolean': return value['value']
    if value['type'] == 'array': return any(v['type']=='boolean' and v['value'] for v in value['value'])
    return None

def direct_truth(value):
    if value['type'] == 'boolean': return value['value']
    if value['type'] == 'array': return any(direct_truth(v) for v in value['value'])
    return False

def expected(case):
    trace = []; error = ''; result = None; logical = None; direct = None
    def evaluate(arg, index):
        trace.append(index)
        if arg.get('throws'): raise RuntimeError('argument-'+str(index))
        return arg['value']
    try:
        if case['mode'] == 'arity':
            count = len(case['args'])
            if count < 2: raise RuntimeError('at function requires at least 2 arguments')
            if count > 2: raise RuntimeError('at function requires at most 2 arguments')
            result = True
        else:
            if case['mode'] == 'array-expr':
                trace.append(0)
                first = dict(type='array',value=[evaluate(v,10+i) for i,v in enumerate(case['elements'])])
            else: first = evaluate(case['args'][0],0)
            result = dict(type='none')
            if first['type'] == 'array':
                index = evaluate(case['args'][1],1)
                if index['type'] == 'integer':
                    position = index['value']; length = len(first['value'])
                    if position < 0: position += length
                    if 0 <= position < length: result = first['value'][position]
            logical = logical_truth(result); direct = direct_truth(result)
    except RuntimeError as failure:
        error = str(failure); result = None
    return dict(id=case['id'],mode=case['mode'],result=result,logicalTruth=logical,directTruth=direct,error=error,evaluated=trace)


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
    assert b'ArrayFunc' in extracted['array.inc'] and b'AtFunc' in extracted['at.inc']
    assert b'Poco::toLower(name)' in extracted['case-normalization.txt']
    assert b'ContainsTrue(item.value)' in extracted['direct-truth.inc'] and b'evalAsBool' in extracted['logical-truth.inc']
    assert b'info->minArgs' in extracted['arity-check.inc'] and b'info->maxArgs' in extracted['arity-check.inc']
    assert b'{"at", AtFunc, 2, 2}' in extracted['registration.inc']
    plan = read_json(BASE/'case-plan.json')
    assert len(plan) == len({row['id'] for row in plan})
    assert {row['mode'] for row in plan} == {'evaluate', 'array-expr', 'arity'}
    for case in plan:
        for arg in case['args']+case.get('elements',[]): validate_shape(arg['value'])
    golden = (BASE/'golden.ndjson').read_bytes()
    rows = [json.loads(line) for line in golden.splitlines()]
    for row in rows:
        if row['mode'] != 'arity' and not row['error']: validate_shape(row['result'])
        if row['mode'] == 'arity' and not row['error']: assert row['result'] is True
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
