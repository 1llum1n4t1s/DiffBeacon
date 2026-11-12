"""Public projection specification; never imports or reads candidate Core code."""
import collections

def quote(s):
    return "'" + s.replace('\\', '\\\\').replace("'", "\\'") + "'"

def value(v):
    if v['kind'] == 'none': return 'None'
    if v['kind'] == 'string': return quote(v['text'])
    if v['kind'] == 'array': return 'Array(' + ','.join(map(value,v['items'])) + ')'
    raise AssertionError(v)

def argument(a,index,logged=True):
    t=a['type']
    if t == 'none': out='None'
    elif t in ('string','empty-string'): out=quote(a['text'])
    elif t == 'array': out='Array()'
    elif t == 'int-min': out='ToInt('+quote(a['text'])+')'
    else: out=a['text']
    if a['kind']=='nested-invalid-range': return 'ColumnRange('+quote(a['text'])+')'
    return 'LogInfo('+quote('side-arg-'+str(index))+','+out+')' if a['kind']=='probe' and logged else out

def classify(c):
    if any(a['kind']=='throw' for a in c['args']): return 'native-only-controlled-throw'
    if any(a['type'] in ('timestamp','regex','file') for a in c['args']): return 'native-only-internal-type'
    if any(a['kind']=='nested-invalid-range' for a in c['args']): return 'public-nested-literal-range-boundary'
    if c['label']=='wrong-arity': return 'public-constructor-arity-diagnostic'
    if any(a['kind']=='constant' for a in c['args']): return 'public-literal-range-value-or-diagnostic;native-only-cache-trace'
    return 'public-value-and-runtime-child-count'

def make_plan(cases,golden):
    plan=[]; classification=[]
    for c in cases:
        scope=classify(c); g=golden[c['id']]
        classification.append(dict(nativeId=c['id'],scope=scope,nativeOptimize=c['optimize'],nativePhase=g['phase'],nativeErrorKind=g['errorKind'],internalPhasePubliclyObserved=False))
        if scope.startswith('native-only'): continue
        call=c['name']+'('+','.join(argument(a,i) for i,a in enumerate(c['args']))+')'
        rejected=g['phase']!='complete'
        expression=call if rejected else 'AllEqual(Array('+call+','+value(g['value'])+'))'
        logs=['side-arg-'+str(i) for i in g['trace'] if c['args'][i]['kind']=='probe']
        plan.append(dict(id=c['id'],count=c['dirs'],nativeIds=[c['id']],nativeOptimize=c['optimize'],scope=scope,
            expression=('@opt ' if c['optimize'] else '@noopt ')+expression,rule='fe',selected=True,
            rejected=rejected,expectedLogTokens=logs,diagnostic=None,windowsPropertyPreparation=False,
            internalPhasePubliclyObserved=False,nativeFullAstEquivalent=False,actualTypedValueObserved=False))
    def add(id,count,expression,rule='fe',selected=True,rejected=False,logs=(),scope='source-derived-public-control'):
        plan.append(dict(id=id,count=count,nativeIds=[],nativeOptimize=None,scope=scope,expression=expression,rule=rule,selected=selected,rejected=rejected,
            expectedLogTokens=list(logs),diagnostic=None,windowsPropertyPreparation=False,
            internalPhasePubliclyObserved=False,nativeFullAstEquivalent=False,actualTypedValueObserved=False))
    for count in (2,3):
        for mode in ('','@opt ','@noopt '):
            tag=mode.strip('@ ').replace(' ','') or 'default'
            for rule in ('e','de','fe'):
                # LogInfo is a runtime node: neither literal cache nor Optimize trace is claimed.
                n=count*(2 if rule=='e' else 1)
                add(f'log-valid-range-{tag}-{rule}',count,mode+"AllEqual(Array(ColumnRange(LogInfo('side-arg-0','1')),Array("+','.join(["''"]*count)+')))',rule,logs=['side-arg-0']*n)
            add('literal-cache-'+tag,count,mode+"AllEqual(Array(ColumnRange('1'),Array("+','.join(["''"]*count)+')))',scope='literal-result-only;cache-trace-native-only')
            add('lazy-runtime-invalid-'+tag,count,mode+"If(false,Column(LogInfo('side-arg-0',0)),true)")
            add('lazy-constant-invalid-'+tag,count,mode+"If(false,ColumnRange('0'),true)",rejected=mode!='@noopt ')
        add('case-insensitive-function',count,'AllEqual(Array(rIgHtCoLuMn(1),None))')
        add('middle-side',count,'AllEqual(Array(MiddleColumn(1),None))')
        add('unknown-alias',count,'centerColumn(1)',rejected=True,scope='source-derived-unknown-name-diagnostic')
        add('filtered-false',count,'AllEqual(Array(LeftColumn(1),\'\'))',selected=False)
        add('at-runtime-invalid-skipped',count,"AllEqual(Array(LineAt(ColumnRange(LogInfo('side-arg-0','0'))),Array("+','.join(['None']*count)+')))',logs=[])
    gaps=[dict(id='child-optimize-fold-before-provider-guard',expression="@opt LineAt(ColumnRange(Substr('0',0)))",expected='reject before runtime provider guard',nativeExactAst=False,
        status='required-unimplemented; not weakened or counted as passed',reason='native child Optimize folds nonliteral Substr to StringLiteral; candidate fold not implemented')]
    return plan,classification,gaps
