#!/usr/bin/env python3
"""固定ZIPを展開せず読む独立reader。Nativeや製品プロセスを起動しない。"""
import hashlib
import io
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parent
MANIFEST_SHA = '0d0c57cb90449124bf0ac8f42765930cc8b3e6f079255d34de52168bc04e53f5'

def sha(data):
    return hashlib.sha256(data).hexdigest()

manifest_bytes = (ROOT / 'fixed-manifest.json').read_bytes()
assert sha(manifest_bytes) == MANIFEST_SHA
manifest = json.loads(manifest_bytes)
payload = manifest['files'][0]
archive = (ROOT / payload['path']).read_bytes()
assert len(archive) == payload['size'] and sha(archive) == payload['sha256']

def subset(expected, actual):
    if isinstance(expected, dict):
        # 固定case descriptorのint64.valueは文字列、実rawではvalueDecimal。
        return all((key := ('valueDecimal' if k == 'value' and expected.get('kind') == 'int64' else k)) in actual
                   and subset(v, actual[key]) for k, v in expected.items())
    if isinstance(expected, list):
        return len(expected) == len(actual) and all(subset(a, b) for a, b in zip(expected, actual))
    return expected == actual

with zipfile.ZipFile(io.BytesIO(archive)) as closure:
    names = closure.namelist()
    assert len(names) == len(set(names)) == 52 and closure.testzip() is None
    assert all(not n.startswith('/') and '\\' not in n and '..' not in n.split('/') for n in names)
    read = closure.read
    load = lambda name: json.loads(read(name))
    files = load('closure-manifest.json')['files']
    for file in files:
        data = read(file['path'])
        assert len(data) == file['size'] and sha(data) == file['sha256']
    # read_portable_v2.pyの同じ原本契約を、抽出とプロセス実行なしで照合する。
    with zipfile.ZipFile(io.BytesIO(read('reference-source.zip'))) as reference:
        assert len(reference.namelist()) == 394 and reference.testzip() is None
        nodes = reference.read('source/Src/FilterEngine/FilterExpressionNodes.cpp')
        assert sha(nodes) == manifest['nodesSha256']
        assert any(n.startswith('licenses/') for n in reference.namelist())
        spans = {'toLocalDateTime': (1684, 1691), 'applyToScalarOrArray': (1693, 1704),
                 'ToDateStrFunc': (2145, 2155), 'ToDateTimeFunc': (2642, 2660)}
        for name in names:
            if not name.startswith('original-pieces/') or not name.endswith('.inc'):
                continue
            stem = Path(name).stem
            if stem in spans:
                low, high = spans[stem]
                expected = b''.join(nodes.splitlines(keepends=True)[low - 1:high])
            else:
                expected = reference.read(name)
            assert read(name) == expected
        assert read('timestamp_original.hpp') == reference.read('adapter/timestamp_original.hpp')
    plan = load('execution-case-plan.v5.json')['cases']
    execution = load('execution-result.v9.json')
    captures = []
    for receipt in execution['receipts']:
        assert receipt == load('proof/' + receipt['id'] + '.receipt.json')
        assert receipt['osExitCode'] == 0 and receipt['birthValidatedOnHeldHandleAfterExit']
        assert receipt['handleHeldThroughOsExitAndBothEof'] and receipt['jobAssignedBeforeResume']
        for channel in ('stdout', 'stderr'):
            stream = receipt[channel]
            basename = stream['path'].replace('\\', '/').rsplit('/', 1)[-1]
            assert stream['eof'] and sha(read('proof/' + basename)) == stream['sha256']
        if receipt['id'].startswith('native-run-'):
            rows = [json.loads(line) for line in read('proof/' + receipt['id'] + '.stdout.raw').splitlines()]
            assert len(rows) == 121 and sum(row['bodyDidRun'] for row in rows) == 111
            for row, case in zip(rows, plan):
                assert row['id'] == case['id'] and row['bodyDidRun'] == case['bodyPlanned']
                assert row['processId'] == receipt['pid']
                assert int(row['processBirthFileTimeDecimal']) == receipt['birthFileTime']
                assert row['sourceCommit'] == manifest['sourceCommit']
                assert row['originalNodesSha256'] == manifest['nodesSha256']
            captures.append(rows)
    ignored = {'processId', 'processBirthFileTimeDecimal', 'clockBeforeEpochMicrosecondsInt64Decimal', 'clockAfterEpochMicrosecondsInt64Decimal'}
    stable = lambda rows: [{k: v for k, v in row.items() if k not in ignored} for row in rows]
    assert len(captures) == 2 and stable(captures[0]) == stable(captures[1])
    golden = load('native-golden-candidate.v9.json')
    assert golden['candidateCount'] == len(golden['entries']) == 111
    assert len(golden['excludedNoGolden']) == 10 and not golden['NativeParserASTOptimizeQualified']
    by_id = {row['id']: row for row in captures[0]}
    for entry in golden['entries']:
        row = by_id[entry['id']]
        assert row['bodyDidRun'] and row['status'] == entry['status']
        if entry['inputDescriptor']['kind'] == 'no-args':
            # adapterは引数なしのinput欄へnoneを記録。これは公開arityのgoldenではない。
            assert entry['id'] == 'todatetime-no-args' and row['input']['kind'] == 'none'
        else:
            assert subset(entry['inputDescriptor'], row['input'])
        assert subset(entry['typedResult'], row['result'])
        if entry['bodyException']:
            for field, raw_field in [('typeNameHex', 'exceptionTypeNameHex'), ('nameHex', 'exceptionNameHex'), ('messageHex', 'exceptionMessageHex')]:
                assert entry['bodyException'][field] == row[raw_field]
        for binding in entry['rawReferences']:
            raw = read(f"proof/native-run-{binding['run']}.stdout.raw")
            assert sha(raw) == binding['sha256']
            assert json.loads(raw.splitlines()[binding['line'] - 1])['id'] == entry['id']
    excluded = {row['id'] for row in captures[0] if not row['bodyDidRun']}
    assert excluded == set(golden['excludedNoGolden'])
    assert all(row['result'] is None for row in captures[0] if not row['bodyDidRun'])
    assert not golden['coverageGap']['ToDateTimeNonSyntaxExceptionNativeObserved']
print(json.dumps({'status': 'pass', 'closureEntries': 52, 'sourceEntries': 394, 'rawRuns': 2,
                  'rawRowsEach': 121, 'measuredBodyEach': 111, 'publicBodyCases': 110,
                  'excludedNoGolden': 10, 'NativeParserASTOptimizeQualified': False,
                  'ToDateTimeNonSyntaxNativeObserved': False, 'NativeReexecution': False}))
