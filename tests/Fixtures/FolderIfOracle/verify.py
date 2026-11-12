"""製品やcapture実装をimportしない独立reader。固定原典・採取証拠・型付き入力を照合する。"""
import sys
sys.dont_write_bytecode = True
import argparse, hashlib, json, pathlib, zipfile
BASE = pathlib.Path(__file__).resolve().parent

def sha(raw): return hashlib.sha256(raw).hexdigest()
def read_json(path): return json.loads(path.read_bytes())

def expected(case):
    trace=[]; result=None; error=''; identity=None
    if case['mode']=='arity':
        n=len(case['args'])
        if n<3: error='if function requires at least 3 arguments'
        elif n>3: error='if function requires at most 3 arguments'
        else: result={'type':'boolean','value':True}
    else:
        trace.append(0); condition=case['args'][0]
        if condition['throws']: error='argument-0'
        else:
            v=condition['value']
            choose=v['type']=='boolean' and v['value'] is True
            if v['type']=='array': choose=any(x['type']=='boolean' and x['value'] is True for x in v['value'])
            index=1 if choose else 2; trace.append(index); selected=case['args'][index]
            if selected['throws']: error='argument-'+str(index)
            else:
                result=selected['value']
                if result['type']=='array': identity=True
    return dict(id=case['id'],mode=case['mode'],result=result,error=error,evaluated=trace,sameArrayStorage=identity)

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
    assert b'IfFunc' in extracted['if.inc']
    assert b'info->minArgs' in extracted['arity-check.inc'] and b'info->maxArgs' in extracted['arity-check.inc']
    assert b'{"if", IfFunc, 3, 3}' in extracted['registration.inc']
    plan = read_json(BASE/'case-plan.json')
    assert len(plan) == len({row['id'] for row in plan}) == 79
    assert {len(c['args']) for c in plan if c['mode']=='arity'} == {0,1,2,3,4}
    assert all(a['throws'] for c in plan if c['mode']=='arity' for a in c['args'])
    kinds={'none','boolean','integer','double','string','timestamp','regex','file-ref','array'}
    assert {c['args'][0]['value']['type'] for c in plan if c['id'].startswith('condition-')} == kinds
    for branch,index in [('true',1),('false',2)]:
        assert {c['args'][index]['value']['type'] for c in plan if c['id'].startswith('return-'+branch+'-')} == kinds
        assert any(c['id']=='selected-throw-'+branch for c in plan)
        assert any(c['id']=='unselected-throw-'+branch for c in plan)
    assert {row['mode'] for row in plan} == {'evaluate', 'arity'}
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
            assert not get('compile.stderr')
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
