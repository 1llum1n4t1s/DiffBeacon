"""製品やcapture実装をimportしない独立reader。固定原典・採取証拠・型付き入力を照合する。"""
import sys
sys.dont_write_bytecode = True
import datetime
import argparse, hashlib, json, pathlib, zipfile
BASE = pathlib.Path(__file__).resolve().parent

def sha(raw): return hashlib.sha256(raw).hexdigest()
def read_json(path): return json.loads(path.read_bytes())

def expected(case):
    trace = []; result = None; error = ''; stringified = None
    if case['mode'] == 'arity':
        count = len(case['args'])
        if count < 1: error = 'strlen function requires at least 1 argument'
        elif count > 1: error = 'strlen function requires at most 1 argument'
        else: result = True
    else:
        assert case['mode'] == 'evaluate' and len(case['args']) == 1
        trace.append(0)
        arg = case['args'][0]
        if arg.get('throws'): error = 'argument-0'
        else:
            value = arg['value']
            def old_string(item):
                kind=item['type']; x=item.get('value')
                if kind=='none': return 'none'
                if kind=='string': x.encode('utf8',errors='strict'); return x
                if kind=='integer': return str(x)
                if kind=='double': return format(x,'.6f')
                if kind=='boolean': return 'true' if x else 'false'
                if kind=='timestamp': return (datetime.datetime(1970,1,1)+datetime.timedelta(microseconds=x,hours=9)).strftime('%Y-%m-%d %H:%M:%S')
                if kind=='array': return '['+', '.join(old_string(child) for child in x)+']'
                raise AssertionError(kind)
            def scalar(item): return None if item['type']=='none' else len(old_string(item))
            stringified = old_string(value)
            result = [scalar(child) for child in value['value']] if value['type']=='array' else scalar(value)
    return dict(id=case['id'], mode=case['mode'], result=result, error=error, stringified=stringified, evaluated=trace)

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
    assert b'StrlenFunc' in extracted['strlen.inc']
    assert b'ToStringValue' in extracted['to-string.inc'] and b'func(item.value)' in extracted['apply.inc']
    assert b'Utf8len_fromLeadByte' in extracted['utf8.inc'] and b'stringlen_of_utf8' in extracted['utf8.inc']
    assert b'info->minArgs' in extracted['arity-check.inc'] and b'info->maxArgs' in extracted['arity-check.inc']
    assert b'{"strlen", StrlenFunc, 1, 1}' in extracted['registration.inc']
    plan = read_json(BASE/'case-plan.json')
    assert len(plan) == len({row['id'] for row in plan})
    assert {row['mode'] for row in plan} == {'evaluate', 'arity'}
    golden = (BASE/'golden.ndjson').read_bytes()
    rows = [json.loads(line) for line in golden.splitlines()]
    for row in rows:
        if row['mode'] == 'evaluate' and not row['error']:
            assert row['result'] is None or type(row['result']) is int or (type(row['result']) is list and all(item is None or type(item) is int for item in row['result']))
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
            assert receipt['timezone'] == compilation['timezone'] == 'JST-9'
            for path,digest in compilation['sourceSha256'].items(): assert sha(data[path]) == digest and get(path) == data[path]
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
