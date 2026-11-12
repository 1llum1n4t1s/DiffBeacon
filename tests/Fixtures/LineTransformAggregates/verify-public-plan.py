"""Independent static Native141/public projection reader. No producer import or product execution."""
import sys
sys.dont_write_bytecode=True
import argparse, collections, hashlib, json, math, pathlib, struct
BASE=pathlib.Path(__file__).resolve().parent
GOLDEN='74e1f5b6bb58af0433cb0d198ee3360da1ebcd1f8845da9b5ce2fd0e2f549e91'
PROOF='039e9b4e77d8164b4e97bb726350675a4d8c6de2d73fe8daa1a3de03cdca7d4f'
def sha(b):return hashlib.sha256(b).hexdigest()
def unique_object(pairs):
    d={}
    for k,v in pairs:
        if k in d:raise AssertionError('duplicate JSON key '+k)
        d[k]=v
    return d
def load(b):return json.loads(b,object_pairs_hook=unique_object)
def require(ok,reason):
    if not ok:raise AssertionError(reason)
def nodes(v):
    yield v
    if v['type']=='array':
        for child in v['value']:yield from nodes(child)
def representable(c,f):
    if c['mode'] not in ('scan','literal-scan') or not c['rows'] or not f.get('returned'):return False
    for v in [n for r in c['rows']+(c['conditions'] or []) for n in nodes(r)]:
        if v['type']=='timestamp':return False
        if v['type']=='double' and not math.isfinite(float(v['value'])):return False
        if v['type'] in ('string','raw-string'):
            try:s=v['value'] if v['type']=='string' else bytes(v['value']).decode('utf-8','strict');s.encode('utf-8','strict')
            except UnicodeError:return False
            if '\0' in s:return False
    return True
def projected_literal(v,throw_allowed):
    t=v['type']
    if t=='none':return 'none'
    if t=='bool':return 'true' if v['value'] else 'false'
    if t=='integer':
        require(-(2**63)<=v['value']<2**63,'int64 input range')
        return 'ToInt("'+str(v['value'])+'")'
    if t=='double':
        n=float(v['value']);s=repr(n)
        if '.' not in s and 'e' not in s.lower():s+='.0'
        require(math.isfinite(n) and struct.pack('>d',float(s))==struct.pack('>d',n),'double input bits')
        return '('+s+')'
    if t in ('string','raw-string'):
        s=v['value'] if t=='string' else bytes(v['value']).decode('utf-8','strict')
        require('\0' not in s,'NUL public quote')
        return '"'+s.replace('"','""')+'"'
    if t=='array':return 'Array('+','.join(projected_literal(x,throw_allowed) for x in v['value'])+')'
    if t=='throw' and throw_allowed:return '(1/(LineNumber-LineNumber))'
    raise AssertionError('unrepresented projection '+t)
def projection(rows,throw_allowed):return 'Choose(LineNumber-1,'+','.join(projected_literal(v,throw_allowed) for v in rows)+')'
def verify_plan(fixture=BASE,plan_dir=BASE):
    plan=load((plan_dir/'public-case-plan.json').read_bytes());m=plan['metadata']
    require(set(plan)=={'metadata','cases'},'plan top-level fields')
    require(m['schema']=='aggregate-public-bridge-plan-v1' and m['fixture']=='.','portable fixture base')
    raw=(fixture/'golden.ndjson').read_bytes();cases_raw=(fixture/'cases.json').read_bytes();source_raw=(fixture/'source-manifest.json').read_bytes()
    require(sha(raw)==GOLDEN==m['goldenSha256'],'Native141 frozen stdout')
    require(sha((fixture/'native-proof.zip').read_bytes())==PROOF==m['nativeProofSha256'],'Native141 frozen proof')
    require(sha(cases_raw)==m['casesSha256'] and sha(source_raw)==m['sourceManifestSha256'],'case/source SHA')
    require(len(load(source_raw)['sources'])==m['sourceFileCount']==15,'15 source blobs')
    require(m['producerCompiled'] is False and m['executed'] is False and m['contextEquivalent'] is False,'metadata is planned, not runtime')
    require(m['localParserEvidence']['path']=='Src/DiffBeacon.Core/LineTransformExpression.cs' and m['localParserEvidence']['historical'] is True and m['localParserEvidence']['sha256']=='702f005f46b20b5d12ba17d2761c63687d0920279de276f60e53eb76187c4552','historical parser evidence')
    cases=load(cases_raw);lines=raw.splitlines();require(len(plan['cases'])==len(cases)==len(lines)==141,'main141')
    require(len({c['id'] for c in cases})==141,'unique Native IDs')
    false_normal=0;false_throw=0
    for n,(item,c,line) in enumerate(zip(plan['cases'],cases,lines),1):
        g=load(line);f=g['steps'][0];ref=item['nativeFirstStepReference']
        require(item['id']==c['id']==g['id'] and item['nativeCase']==c and item['nativeMode']==c['mode'],'Native original ID/order/case')
        require(item['executed'] is False and item['producerCompiled'] is False and item['contextEquivalent'] is False,'planned flags')
        require(ref['file']=='golden.ndjson' and ref['fileSha256']==GOLDEN and ref['lineNumber']==n and ref['lineSha256WithoutTerminator']==sha(line),'BASE-relative original line reference')
        require(ref['stepIndex']==0 and ref['label']==f['label'] and ref['originalStep']==f,'raw original first step')
        for k in ('returned','serializedUtf8Hex','value','exceptionType','errorUtf8Hex'):require(ref[k]==f.get(k),'original first step '+k)
        bridge=representable(c,f)
        require(bridge==(item['classification']=='serialized-key/value-bridge-planned'),'independent representability')
        if not bridge:
            require(item['classification']=='unrepresentable-not-executed' and item['expression'] is None and bool(item['reason'].strip()),'37 exclusions remain unexecuted')
            require('publicInvocationPlan' not in item and 'throwSubstitution' not in item,'no invocation for excluded case')
            continue
        throwing=any(x['type']=='throw' for r in c['rows'] for x in nodes(r))
        expr=('@cs ' if c['caseSensitive'] else '@ci ')+c['function']+'('+projection(c['rows'],throwing)
        if c['conditions'] is not None:expr+=','+projection(c['conditions'],False)
        expr+=')';require(item['expression']==expr,'exact typed-source producer projection')
        inv=item['publicInvocationPlan'];require(set(inv)=={'providerLines','rootLineIndex','targetResult','typedSourceValuesReproduced'},'invocation fields')
        require(inv['providerLines']==['']*len(c['rows']) and inv['rootLineIndex']==0 and inv['typedSourceValuesReproduced']==(not throwing),'public provider/context boundary')
        if throwing:
            sub=item['throwSubstitution'];require(sub['expression']=='1/(LineNumber-LineNumber)' and sub['runtimeOnly'] is True and sub['providerDependent'] is True and sub['originalExceptionEquivalent'] is False,'runtime-only throw substitution')
            require(sub['expectedNativeTraceValueIndices']==[t['lineIndex'] for t in f['trace'] if t['node']=='value'],'original suppressed value trace')
        else:require('throwSubstitution' not in item,'no unnecessary throw bridge')
        if c['conditions'] and all(v['type']=='bool' and v['value'] is False for v in c['conditions']):
            require(f['returned'] and f['value']['kind']=='none' and not [t for t in f['trace'] if t['node']=='value'],'Native false condition zero-value first step')
            if throwing:false_throw+=1
            else:false_normal+=1
    counts=dict(collections.Counter(x['classification'] for x in plan['cases']))
    require(counts==m['classifications']=={'serialized-key/value-bridge-planned':104,'unrepresentable-not-executed':37},'104/37 counts')
    require(false_normal==false_throw==4,'four functions times two all-false patterns')
    require(load((plan_dir/'public-plan-metadata.json').read_bytes())==m,'standalone public metadata')
    probes=load((plan_dir/'probes.json').read_bytes())
    require(probes['mainCaseCount']==141 and probes['executed'] is False and len(probes['probes'])==8,'eight separate probes')
    expected=[(name.lower()+'-arity-'+str(arity),name+'('+','.join(['ToInt("1")']*arity)+')') for name in ('Average','Sum','Maximum','Minimum') for arity in (0,3)]
    for probe,(pid,expression) in zip(probes['probes'],expected):require(probe['id']==pid and probe['expression']==expression and probe['classification']=='separate-public-contract' and probe['executed'] is False and probe['producerCompiled'] is False,'probe source contract only')
    require(len(probes['engineeringPlans'])==1 and probes['engineeringPlans'][0]['classification']=='separate-engineering-not-native-golden' and probes['engineeringPlans'][0]['expression'] is None and probes['engineeringPlans'][0]['executed'] is False,'UB remains separate engineering plan')
    return plan,probes,{'result':'PASS','kind':'static-public-plan-only','mainCases':141,'plannedBridge':104,'unrepresentedNotExecuted':37,'separateProbes':8,'falseConditionNormal':4,'falseConditionThrowSubstitution':4,'producerCompiled':False,'executed':False,'actualTypedValueObserved':False,'contextEquivalent':False,'nativeGoldenSha256':GOLDEN,'preservedOriginalFirstSteps':True,'portableReferences':True}
def main():
    p=argparse.ArgumentParser();p.add_argument('--fixture',type=pathlib.Path,default=BASE);p.add_argument('--plan-dir',type=pathlib.Path,default=BASE);p.add_argument('--receipt',type=pathlib.Path);a=p.parse_args()
    _,_,result=verify_plan(a.fixture,a.plan_dir)
    if a.receipt:
        with a.receipt.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2);f.write('\n')
    print(json.dumps(result))
if __name__=='__main__':main()