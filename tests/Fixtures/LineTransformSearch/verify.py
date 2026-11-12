"""原典SHA/全原文bytes/EOL/比較key bytesと任意の列選択traceを独立照合する。"""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent

def read_ndjson(path):
    rows = [json.loads(line) for line in Path(path).read_bytes().splitlines() if line.strip()]
    assert len({row['id'] for row in rows}) == len(rows), 'duplicate case ID'
    return rows

def eol(data):
    return 3 if data.endswith(b'\r\n') else 1 if data.endswith(b'\n') else 2 if data.endswith(b'\r') else 0

def verify(actual=None, require_selections=False, public_plan=False):
    assert not public_plan or actual, "--public-plan requires --actual"
    manifest = json.loads((ROOT / 'manifest.json').read_text())
    for name, expected in manifest['files'].items():
        data = (ROOT / name).read_bytes()
        assert len(data) == expected['size'], name + ': size'
        assert hashlib.sha256(data).hexdigest() == expected['sha256'], name + ': SHA-256'
    provenance = json.loads((ROOT / 'provenance.json').read_text())
    for source in provenance['sources']:
        data = (ROOT / source['file']).read_bytes()
        assert hashlib.sha256(data).hexdigest() == source['sha256'], source['file']
        assert hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() == source['blob'], source['file'] + ': Git blob'
    cases = json.loads((ROOT / 'cases.json').read_text(encoding='utf-8'))
    golden = read_ndjson(ROOT / 'golden.ndjson')
    assert [c['id'] for c in cases] == [r['id'] for r in golden]
    assert len(cases) == 68, 'fixed 68 body cases required'
    boundaries = {f'search-{op}-arity-{count}': (op, count) for op in ['strcount', 'strfind'] for count in [0, 1, 4]}
    nodes = (ROOT / 'original/FilterExpressionNodes.cpp').read_text(encoding='utf-8').splitlines()
    parser = (ROOT / 'original/FilterParser.y').read_text(encoding='utf-8').splitlines()
    assert nodes[3834].strip() == '{"strcount", StrCountFunc, 2, 2},'
    assert nodes[3835].strip() == '{"strfind", StrFindFunc, 2, 3},'
    ctor = '\n'.join(nodes[4220:4282])
    assert 'FunctionNode::FunctionNode' in ctor and 'const FunctionInfo* info = findFunction(functionName);' in ctor
    for comparison in ['argCount < static_cast<size_t>(info->minArgs)', 'argCount > static_cast<size_t>(info->maxArgs)']:
        assert comparison in ctor, 'fixed constructor argument-count check'
    assert ctor.count('throw InvalidArgumentCountError(') == 2
    grammar = '\n'.join(parser[138:190])
    assert grammar.count('new FunctionNode(') == 2
    assert grammar.count('catch (const InvalidArgumentCountError& e)') == 2
    assert grammar.count('pCtx->errorCode = FILTER_ERROR_INVALID_ARGUMENT_COUNT;') == 2
    for case, row in zip(cases, golden):
        if case['id'] not in boundaries:
            continue
        op, count = boundaries[case['id']]
        assert len(case['groups']) == 1 and len(case['groups'][0]['ast']) == 1
        ast = case['groups'][0]['ast'][0]
        assert ast['op'] == op and len(ast.get('children', [])) == count
        minimum, maximum = (2, 2) if op == 'strcount' else (2, 3)
        assert count < minimum or count > maximum
        assert row['status'] == 'ok' and row['errorPhase'] == '' and row['selections'] == []
        for line in row['lines']:
            suffix = {0: b'', 1: b'\n', 2: b'\r', 3: b'\r\n'}[line['keyEol']]
            assert bytes.fromhex(line['keyHex']) == b'none' + suffix, case['id'] + ': fixed body none'

    receipt = json.loads((ROOT / 'capture.json').read_text())
    assert receipt['exitCode'] == 0 and receipt['stdoutEof'] and receipt['stderrEof']
    assert receipt['birthFileTime'] > 0 and receipt['pid'] > 0
    assert receipt['stdoutSha256'] == hashlib.sha256((ROOT / 'golden.ndjson').read_bytes()).hexdigest()
    assert receipt['stderrSha256'] == hashlib.sha256((ROOT / 'reference.stderr').read_bytes()).hexdigest()
    assert not (ROOT / 'reference.stderr').read_bytes()
    assert receipt['caseCount'] == len(cases)
    for case, row in zip(cases, golden):
        assert len(case['lines']) == len(row['lines']), case['id']
        for original, line in zip(case['lines'], row['lines']):
            source = original.encode('utf-8')
            assert source.hex() == line['originalHex'], case['id'] + ': original bytes'
            assert eol(source) == line['originalEol'], case['id'] + ': original EOL'
            key = bytes.fromhex(line['keyHex'])
            assert eol(key) == line['keyEol'], case['id'] + ': key EOL'
            assert line['originalEol'] == line['keyEol'], case['id'] + ': outer EOL preservation'
    checks = 0
    if actual:
        results = {r['id']: r for r in read_ndjson(actual)}
        assert set(results) == {r['id'] for r in golden}, 'all fixed cases required'
        for oracle in golden:
            expected = oracle
            if public_plan and oracle['id'] in boundaries:
                expected = dict(oracle, status='error', errorPhase='compile', selections=[])
                expected['lines'] = [dict(line, keyHex=line['originalHex'], keyEol=line['originalEol']) for line in oracle['lines']]
            result = results[expected['id']]
            assert result['status'] == expected['status'], expected['id'] + ': status'
            assert result['errorPhase'] == expected['errorPhase'], expected['id'] + ': error phase'
            assert len(result['lines']) == len(expected['lines']), expected['id'] + ': row count'
            for got, want in zip(result['lines'], expected['lines']):
                for field in ['originalHex', 'keyHex', 'originalEol', 'keyEol', 'columnCount']:
                    assert got[field] == want[field], expected['id'] + ': ' + field
                    checks += 1
            if require_selections or 'selections' in result:
                assert result['selections'] == expected['selections'], expected['id'] + ': selected columns / RAW key'
                checks += 1
    return {'fixtureCases': len(cases), 'okCases': sum(r['status']=='ok' for r in golden), 'errorCases': sum(r['status']=='error' for r in golden), 'actualChecks': checks, 'selectionTraceRequired': require_selections, 'caseCountBasis': 'fixedCppFunctionBody', 'publicPlan': public_plan, 'oracleMatchedCases': len(golden) - (6 if public_plan else 0) if actual else 0, 'publicBoundaryCases': 6 if public_plan else 0, 'publicConstructorCases': 6 if public_plan else 0}

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--actual', help='product results NDJSON; all IDs required')
    parser.add_argument('--require-selections', action='store_true')
    parser.add_argument('--public-plan', action='store_true', help='require all 68 IDs; six fixed wrong-arity cases must fail at compile and retain original keys')
    args = parser.parse_args()
    print(json.dumps(verify(args.actual, args.require_selections, args.public_plan)))
