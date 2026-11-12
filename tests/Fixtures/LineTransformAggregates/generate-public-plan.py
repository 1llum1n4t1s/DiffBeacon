"""Portable static Native141 public plan producer; no Compile, native execution or golden arithmetic."""
import sys
sys.dont_write_bytecode = True
import argparse
import collections
import hashlib
import json
import math
import pathlib
import struct

GOLDEN_SHA = '74e1f5b6bb58af0433cb0d198ee3360da1ebcd1f8845da9b5ce2fd0e2f549e91'
PROOF_SHA = '039e9b4e77d8164b4e97bb726350675a4d8c6de2d73fe8daa1a3de03cdca7d4f'

def digest(b):
    return hashlib.sha256(b).hexdigest()

def literal(v, allow_throw):
    t = v['type']
    if t == 'none':
        return 'none'
    if t == 'bool':
        return 'true' if v['value'] else 'false'
    if t == 'integer':
        assert -(2**63) <= v['value'] < 2**63
        return 'ToInt("' + str(v['value']) + '")'
    if t == 'double':
        x = float(v['value'])
        if not math.isfinite(x):
            raise ValueError('nonfinite double unavailable through public lexer')
        s = repr(x)
        if '.' not in s and 'e' not in s.lower():
            s += '.0'
        assert struct.pack('>d', float(s)) == struct.pack('>d', x)
        return '(' + s + ')'
    if t == 'string' or t == 'raw-string':
        s = v['value'] if t == 'string' else bytes(v['value']).decode('utf-8', 'strict')
        s.encode('utf-8', 'strict')
        if '\x00' in s:
            raise ValueError('NUL is rejected by public quoted-string lexer')
        return '"' + s.replace('"', '""') + '"'
    if t == 'array':
        return 'Array(' + ','.join(literal(x, allow_throw) for x in v['value']) + ')'
    if t == 'throw' and allow_throw:
        return '(1/(LineNumber-LineNumber))'
    raise ValueError('native ' + t + ' node/type unavailable as public equivalent')

def has_throw(v):
    return v['type'] == 'throw' or v['type'] == 'array' and any(has_throw(x) for x in v['value'])

def sequence(rows, allow_throw):
    return 'Choose(LineNumber-1,' + ','.join(literal(v, allow_throw) for v in rows) + ')'

def main():
    a = argparse.ArgumentParser()
    a.add_argument('--fixture', type=pathlib.Path, required=True)
    a.add_argument('--output', type=pathlib.Path, required=True)
    a.add_argument('--repo', type=pathlib.Path, help='Optional legacy CLI argument; historical parser evidence is fixed, not regenerated from current implementation')
    args = a.parse_args()
    raw = (args.fixture / 'golden.ndjson').read_bytes()
    assert digest(raw) == GOLDEN_SHA
    assert digest((args.fixture / 'native-proof.zip').read_bytes()) == PROOF_SHA
    cases_raw = (args.fixture / 'cases.json').read_bytes()
    cases = json.loads(cases_raw)
    lines = raw.splitlines()
    native = {json.loads(line)['id']: (i + 1, line, json.loads(line)) for i, line in enumerate(lines)}
    assert len(cases) == len(native) == 141
    assert {c['id'] for c in cases} == set(native)
    result = []
    for c in cases:
        line_number, line_bytes, g = native[c['id']]
        first = g['steps'][0]
        item = dict(id=c['id'], nativeMode=c['mode'], executed=False, producerCompiled=False,
                    contextEquivalent=False, expression=None, classification='unrepresentable-not-executed',
                    nativeCase=c, reason='', nativeFirstStepReference={
                        'file': 'golden.ndjson', 'fileSha256': GOLDEN_SHA,
                        'lineNumber': line_number, 'lineSha256WithoutTerminator': digest(line_bytes),
                        'stepIndex': 0, 'label': first['label'], 'returned': first.get('returned'),
                        'serializedUtf8Hex': first.get('serializedUtf8Hex'), 'value': first.get('value'),
                        'exceptionType': first.get('exceptionType'), 'errorUtf8Hex': first['errorUtf8Hex'],
                        'originalStep': first})
        try:
            if c['mode'] not in ('scan', 'literal-scan'):
                raise ValueError('controlled state/cache/shared AST/caller/provider lifetime is not represented by public expression')
            if not c['rows']:
                raise ValueError('zero-row provider is not a public invocation with an evaluated input line')
            if not first['returned']:
                raise ValueError('original native error class/std bytes are not public C# exception equivalence')
            throwing = any(has_throw(v) for v in c['rows'])
            producer = sequence(c['rows'], throwing)
            condition = None if c['conditions'] is None else sequence(c['conditions'], False)
            item['expression'] = ('@cs ' if c['caseSensitive'] else '@ci ') + c['function'] + '(' + producer + (',' + condition if condition else '') + ')'
            item['classification'] = 'serialized-key/value-bridge-planned'
            item['reason'] = 'Derived public typed-source producer; first result bytes/value bridge only, not native AST/context/trace/cache equivalence.'
            item['publicInvocationPlan'] = dict(providerLines=[''] * len(c['rows']), rootLineIndex=0,
                                                targetResult='first invocation serialized key; internal type/bits require separately qualified observer',
                                                typedSourceValuesReproduced=not throwing)
            if throwing:
                item['throwSubstitution'] = dict(expression='1/(LineNumber-LineNumber)',
                    runtimeOnly=True, providerDependent=True, originalExceptionEquivalent=False,
                    meaning='runtime zero division replaces native throw sentinel only to check that rejected rows never evaluate value',
                    expectedNativeTraceValueIndices=[t['lineIndex'] for t in first['trace'] if t['node'] == 'value'])
        except (ValueError, UnicodeError) as e:
            item['reason'] = str(e)
        result.append(item)
    source = json.loads((args.fixture / 'source-manifest.json').read_bytes())
    metadata = dict(schema='aggregate-public-bridge-plan-v1', fixture='.',
                    goldenSha256=GOLDEN_SHA, casesSha256=digest(cases_raw), nativeProofSha256=PROOF_SHA,
                    sourceManifestSha256=digest((args.fixture / 'source-manifest.json').read_bytes()),
                    sourceFileCount=len(source['sources']), scope='static public serialized-key bridge plan; no producer execution or typed/context equivalence qualification',
                    localParserEvidence=dict(path='Src/DiffBeacon.Core/LineTransformExpression.cs', sha256='702f005f46b20b5d12ba17d2761c63687d0920279de276f60e53eb76187c4552', historical=True,
                        lines={'LineNumber':59, 'Choose':237, 'OptimizeAllChildren':285, 'ToInt':277,
                               'Serializer':567, 'F6Double':633, 'Prefixes':869, 'FunctionArity':972,
                               'QuotedStringNul':1015, 'Int32Lexer':1063}),
                    classifications=dict(collections.Counter(x['classification'] for x in result)),
                    producerCompiled=False, executed=False, contextEquivalent=False)
    probes = dict(schema='aggregate-separate-public-contract-probes-v1', executed=False,
                  mainCaseCount=141, probes=[dict(id=f'{name.lower()}-arity-{arity}',
                  expression=name+'('+','.join(['ToInt("1")']*arity)+')',
                  classification='separate-public-contract', expected='parser argument-count rejection after candidate integration',
                  evidence='native source registration arity 1..2; not native constructor result/error equivalence',
                  executed=False, producerCompiled=False) for name in ['Average','Sum','Maximum','Minimum'] for arity in [0,3]],
                  engineeringPlans=[dict(id='nonfinite-or-outside-int64-conversion', executed=False,
                    expression=None, classification='separate-engineering-not-native-golden',
                    policy='reject integer conversion unless finite and in [-2^63,2^63)',
                    nativeBoundary='undefined/uncollected native conversion outside range; no compatibility expectation')])
    for name, payload in [('public-case-plan.json', dict(metadata=metadata,cases=result)), ('probes.json',probes), ('public-plan-metadata.json',metadata)]:
        path = args.output/name
        with path.open('x', encoding='utf-8', newline='\n') as f:
            json.dump(payload, f, indent=2, ensure_ascii=False)
            f.write('\n')
    print(json.dumps({'count':len(result),'classifications':metadata['classifications']}))

if __name__ == '__main__':
    main()
