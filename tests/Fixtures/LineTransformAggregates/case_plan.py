"""固定入力の記述だけ。期待集計値は生成せず実C++ stdoutを原本とする。"""
import sys
sys.dont_write_bytecode = True

def n(t, v=None):
    return dict(type=t, **({} if v is None else dict(value=v)))
I=lambda v:n('integer',v)
D=lambda v:n('double',v)
S=lambda v:n('string',v)
B=lambda v:n('bool',v)
N=lambda:n('none')
A=lambda *v:n('array',list(v))
T=lambda v:n('timestamp',v)
R=lambda v:n('raw-string',v)
X=lambda v:n('throw',v)

def build():
    cases=[]
    patterns=[
        ('integers',[I(1),I(2),I(6)]),
        ('fractional',[D(1.25),I(-2),D(3.75)]),
        ('negative',[I(-7),I(-2),I(-4)]),
        ('integer-double-result',[D(2.0),D(4.0)]),
        ('precision-safe',[I(9007199254740991),I(1)]),
        ('booleans',[B(False),B(True),B(True)]),
        ('all-false',[B(False),B(False)]),
        ('all-true',[B(True),B(True)]),
        ('empty-provider',[]),
        ('all-none',[N(),N()]),
        ('strings-cs',[S('a'),S('B'),S('c')]),
        ('strings-ci',[S('a'),S('B'),S('c')]),
        ('strings-ci-tie',[S('a'),S('A'),S('b')]),
        ('unicode',[S('い'),S('あ'),S('é')]),
        ('array-values',[A(I(1),I(2)),A(I(4),I(8))]),
        ('array-empty-nested',[A(),A(A(I(9)),I(2),N())]),
        ('array-bool',[A(B(True),B(False)),A(B(True))]),
        ('number-first-mixed',[I(5),B(True),S('z'),I(7)]),
        ('bool-first-mixed',[B(True),I(5),S('z'),B(False)]),
        ('string-first-mixed',[S('a'),I(5),B(False),S('Z')]),
        ('condition-all-false',[I(1),I(2),I(3)],[B(False),B(False),B(False)]),
        ('condition-all-false-value-throws',[X('suppressed-zero'),X('suppressed-one'),X('suppressed-two')],[B(False),B(False),B(False)]),
        ('condition-false',[I(1),X('must-not-evaluate'),I(3)],[B(True),B(False),B(True)]),
        ('condition-nonbool',[I(1),X('must-not-evaluate'),X('must-not-evaluate')],[B(True),I(1),S('true')]),
        ('condition-arrays',[I(1),I(2),I(3),I(4)],[A(B(False),B(True)),A(I(1),B(False)),A(A(B(True))),A()]),
        ('condition-throw',[I(1),I(2),I(3)],[B(True),X('condition-error'),B(True)]),
        ('value-throw',[I(1),X('value-error'),I(3)]),
        ('raw-string',[R([97,0,98]),R([97,0,99])]),
        ('typed-timestamp',[T(10),T(20),T(-4)]),
    ]
    for fn in ['Average','Sum','Maximum','Minimum']:
        for pattern in patterns:
            name,rows,*cond=pattern
            cases.append(dict(id=fn.lower()+'-'+name,mode='scan',function=fn,rows=rows,
                conditions=cond[0] if cond else None,caseSensitive='-ci' not in name,
                outerLineIndex=7,classification=('typed-timestamp-observation-no-format' if name=='typed-timestamp' else 'raw-byte-observation' if name=='raw-string' else 'native-node-provider-observation'),
                contextSentinel=dict(diffItemPresent=True,matchRangesSeed=1,insideRangesSeed=1)))
        for suffix,row in [('original-direct-scalar',I(5)),('original-direct-array',A(I(1),I(2),N()))]:
            cases.append(dict(id=fn.lower()+'-'+suffix,mode='literal-scan',function=fn,rows=[row,row,row],conditions=None,caseSensitive=True,outerLineIndex=7,classification='original-native-literal-leaf-at-controlled-trace-boundary',contextSentinel=dict(diffItemPresent=True,matchRangesSeed=1,insideRangesSeed=1)))
    scenarios=[
        ('cache-live-mutation','cache-mutation'),
        ('shared-four-functions-one-node','cross-function-cache'),
        ('equivalent-distinct-value-nodes','distinct-values'),
        ('same-value-distinct-condition-nodes','distinct-conditions'),
        ('same-value-same-condition-cache','same-condition-cache'),
        ('first-call-after-provider-update','first-call-delayed'),
        ('failed-value-retry-no-partial-cache','retry-value-error'),
        ('failed-condition-retry-no-partial-cache','retry-condition-error'),
        ('nested-inner-success-outer-error','nested-error'),
        ('nested-aggregate-constant-per-line','nested-success'),
        ('nested-condition-shared-cache','nested-condition'),
        ('none-result-success-cache','empty-cache'),
        ('provider-null-does-not-evaluate','provider-null'),
        ('context-case-change-cache-identity','case-change-cache'),
        ('caller-live-rule-cache','caller-live'),
        ('caller-next-invocation-new-epoch','caller-epoch'),
        ('caller-failure-keeps-prior-setline','caller-error'),
    ]
    for id,mode in scenarios:
        cases.append(dict(id=id,mode=mode,function='Sum',rows=[I(1),I(2),I(3)],
            conditions=None,caseSensitive=True,outerLineIndex=1,
            classification='native-caller-observation' if mode.startswith('caller') else 'native-node-provider-observation',
            contextSentinel=dict(diffItemPresent=True,matchRangesSeed=1,insideRangesSeed=1)))
    assert len(cases)==141
    return cases

UNSUPPORTED=[
    dict(boundary='out-of-range-double-to-int64',executed=False,reason='C++ floating to signed integer conversion outside representable range has no portable defined result; no golden generated.'),
    dict(boundary='nan-infinity',executed=False,reason='Sum/Maximum/Minimum integral-result cast may be undefined; no inferred native result.'),
    dict(boundary='null-sharedContext',executed=False,reason='Original code dereferences sharedContext without a safety contract.'),
    dict(boundary='same-node-recursion-concurrent-reentry',executed=False,reason='Original success cache has no recursion or parallel synchronization guarantee.'),
    dict(boundary='full-parser-optimizer-public-AST',executed=False,reason='Capture uses explicit controlled ExprNode/provider adapter; registration bytes are source evidence only.'),
    dict(boundary='typed-Timestamp-format',executed=False,reason='Timestamp type/value/comparison observed, original ToStringValue timestamp formatting path deliberately not entered; full Poco LocalDateTime formatting outside scope.'),
    dict(boundary='invalid-arity-zero-three',executed=False,reason='Registration 1..2 is fixed source evidence; unguarded direct function args[0] is not a parser/constructor rejection observation.'),
    dict(boundary='macOS-other-compiler',executed=False,reason='Native capture qualified only Windows x64 MSVC 14.51.'),
]
