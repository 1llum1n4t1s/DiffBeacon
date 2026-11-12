"""Strict Size public NDJSON reader. Native qualification is a separate process/precondition."""
import sys
sys.dont_write_bytecode=True
import argparse,pathlib,json,hashlib,re,collections
GOLDEN='abe36d531ca546c5683a153b36be4e1f5ed31f971b9b3c322f535cf55e1e3fe8'
PROOF='e8f149d4e038ce36ea1e425d6c69c7862e0dee2bb0cb52990547d545f7588b0a'
MAIN={'id','classification','qualification','nativeParserExecuted','nativeDirectiveExecuted','contextEquivalent','actualTypedObserved','actualTypedValueObserved','actualNumericBitsHex','nativeValueKind','nativeValueBitsHex','nativeRecord','nativeGoldenSha256','nativeLineNumber','nativeRecordSha256','reason','publicCompileExecuted','producerCompiled','executed','skipped','matched','expectedSerializedUtf8Hex','actualKeys','actualExceptionType','actualErrorUtf8Hex','sourceRetained','sourceShaBefore','sourceShaAfter','workUsed','outputCharacters'}
DIRECT={'id','originalNativeId','originalRecordSha256','expression','nativeParserExecuted','nativeDirectiveExecuted','actualTypedObserved','contextEquivalent','qualification','producerCompiled','matched','actualKeys','actualExceptionType','actualErrorUtf8Hex','expectedSerializedUtf8Hex','workUsed','sourceShaBefore','sourceShaAfter'}
PROBE={'id','expression','qualification','nativeExecuted','actualTypedObserved','contextEquivalent','producerCompiled','expectedCompileSuccess','passed','nativeGoldenValue','workUsed','actualKeys','actualExceptionType','actualErrorUtf8Hex'}
KEY={'text','utf16Length','utf8Length','utf8Hex','sha256'}
MAIN_QUAL='public serialized UTF8 keys only; Native parser/directive/typed AST context unexecuted'
DIRECT_QUAL='separate CSharp public direct expression observation; main95 historical skip unchanged'
TOKEN_QUAL='static token proposal; CSharp Compile observation only'
UB_QUAL='Native UB cast unexecuted; no golden expected value'
REJECTIONS={'System.ArgumentException','System.ArgumentOutOfRangeException','System.ArgumentNullException','System.FormatException','System.ArithmeticException','System.OverflowException','System.DivideByZeroException','System.InvalidOperationException'}
def require(ok,reason):
    if not ok:raise AssertionError(reason)
def object_unique(pairs):
    d={}
    for k,v in pairs:
        require(k not in d,'duplicate JSON field '+k);d[k]=v
    return d
def load(b):return json.loads(b,object_pairs_hook=object_unique,parse_constant=lambda x:(_ for _ in ()).throw(AssertionError('nonfinite JSON '+x)))
def sha(b):return hashlib.sha256(b).hexdigest()
def fields(r,names,label):require(isinstance(r,dict) and set(r)==names,'exact '+label+' field set')
def records(path):
    raw=path.read_bytes();require(raw.endswith(b'\n') and not raw.startswith(b'\xef\xbb\xbf'),'NDJSON complete LF/no BOM')
    lines=raw.split(b'\n')[:-1];require(lines and all(lines),'empty or blank NDJSON')
    return [load(b.decode('utf-8','strict')) for b in lines],raw,lines
def byte_hex(s):
    require(isinstance(s,str) and re.fullmatch(r'(?:[0-9a-f]{2})*',s) is not None,'canonical byte hex')
    return bytes.fromhex(s)
def counter(r,output=False):
    require(type(r['workUsed']) is int and 0<=r['workUsed']<=128*1024*1024,'public cumulative work domain')
    if output:require(type(r['outputCharacters']) is int and 0<=r['outputCharacters']<=64*1024*1024,'public cumulative output domain')
def source_sha(source):
    # The agreed writer serializes these ASCII-only source strings with default JsonSerializer.
    require(all(all(ord(c)<128 and c not in '<>&\"\'' for c in s) for s in source),'source hash schema only admits known ASCII producer strings')
    return sha(json.dumps(source,separators=(',',':'),ensure_ascii=False).encode('utf-8'))
def error(r,expected):
    if not expected:require(r['actualExceptionType'] is None and r['actualErrorUtf8Hex'] is None,'successful operation/unexecuted row has no error')
    else:
        require(r['actualExceptionType'] in REJECTIONS,'actual public compile rejection type; cancellation/incomplete run is not rejection success')
        b=byte_hex(r['actualErrorUtf8Hex']);require(bool(b) and bool(b.decode('utf-8','strict')),'nonempty actual rejection message')
def keys(r,expected,count):
    require(isinstance(r['actualKeys'],list) and len(r['actualKeys'])==count,'exact actual key count')
    total=0
    for key in r['actualKeys']:
        fields(key,KEY,'key');require(isinstance(key['text'],str),'key text')
        b=key['text'].encode('utf-8','strict');length=len(key['text'].encode('utf-16-le','strict'))//2
        require(type(key['utf16Length']) is int and key['utf16Length']==length and type(key['utf8Length']) is int and key['utf8Length']==len(b),'key UTF8/UTF16 lengths')
        require(byte_hex(key['utf8Hex'])==b==expected and key['sha256']==sha(b),'all actual key original bytes/SHA')
        total+=length
    return total
def fixture_data(fixture,public_plan,probe_plan):
    golden=(fixture/'golden.ndjson').read_bytes();require(sha(golden)==GOLDEN,'frozen Native95 golden')
    require(sha((fixture/'native-proof.zip').read_bytes())==PROOF,'frozen Native95 proof')
    fixed=load((fixture/'fixed-manifest.json').read_bytes());frozen={r['path']:r for r in fixed['files']}
    for name,path in (('public-case-plan.json',public_plan),('public-probes-and-boundaries.json',probe_plan),('cases.json',fixture/'cases.json')):
        b=path.read_bytes();require(len(b)==frozen[name]['size'] and sha(b)==frozen[name]['sha256'],'original frozen plan/cases bytes '+name)
    plan=load(public_plan.read_bytes());proposals=load(probe_plan.read_bytes());cases=load((fixture/'cases.json').read_bytes());lines=golden.splitlines()
    require(len(plan['cases'])==len(cases)==len(lines)==95 and plan['goldenSha256']==GOLDEN,'95 original cases')
    ids=set()
    for n,(item,c,line) in enumerate(zip(plan['cases'],cases,lines),1):
        native=load(line);ref=item['nativeReference'];require(item['id']==c['id']==native['id'] and item['id'] not in ids,'original ID/order/unique');ids.add(item['id'])
        require(item['nativeCase']==c and ref['record']==native and ref['file']=='golden.ndjson' and ref['lineNumber']==n and ref['fileSha256']==GOLDEN and ref['lineSha256WithoutTerminator']==sha(line),'original record reference')
        require(all(item[k] is False for k in ('producerCompiled','executed','parserEquivalent','directiveEquivalent','typedPublicValueObserved')),'historical main plan unexecuted')
    require(dict(collections.Counter(x['classification'] for x in plan['cases']))==plan['counts']=={'planned-public-serialized-bridge':77,'constructor-only-unsupported-public':14,'defined-cast-platform-rounding-observation':4},'77/14/4 preserved')
    require(len(proposals['tokenRuleProbes'])==14,'14 token proposals')
    return plan,proposals
def verify(actual,probes,fixture,public_plan,probe_plan):
    plan,proposals=fixture_data(fixture,public_plan,probe_plan)
    rows,raw,_=records(actual);require(len(rows)==95,'complete actual main95 required')
    matched=0;unexecuted=0
    for n,(r,item) in enumerate(zip(rows,plan['cases']),1):
        fields(r,MAIN,'main');native=item['nativeReference']['record'];ref=item['nativeReference'];bridge=item['classification']=='planned-public-serialized-bridge'
        require(r['id']==item['id'] and r['classification']==item['classification'] and r['reason']==item['reason'],'main ID/order/classification/reason')
        require(r['qualification']==MAIN_QUAL and all(r[k] is False for k in ('nativeParserExecuted','nativeDirectiveExecuted','contextEquivalent','actualTypedObserved','actualTypedValueObserved')) and r['actualNumericBitsHex'] is None,'CSharp serialized observation / Native parser/type/context unexecuted')
        require(r['nativeRecord']==native and r['nativeGoldenSha256']==GOLDEN and type(r['nativeLineNumber']) is int and r['nativeLineNumber']==n and r['nativeRecordSha256']==ref['lineSha256WithoutTerminator'],'original Native record/line hash')
        require(r['nativeValueKind']==native['value']['kind'] and r['nativeValueBitsHex']==native['value'].get('bitsHex'),'original kind/bits reference only')
        require(r['expectedSerializedUtf8Hex']==native['serializedUtf8Hex'],'frozen serialized bytes')
        require(r['publicCompileExecuted'] is bridge and r['producerCompiled'] is bridge and r['executed'] is bridge and r['skipped'] is (not bridge),'actual main compilation/transform/skip flags')
        source=['source RAW '+item['id']+' '+str(i) for i in range(item['nativeCase']['providerCount'])] if bridge else []
        require(r['sourceRetained'] is True and r['sourceShaBefore']==r['sourceShaAfter']==source_sha(source),'source before/after exact producer SHA')
        counter(r,True);error(r,False)
        if bridge:
            require(r['matched'] is True and r['workUsed']>0,'bridge matched=false is not pass')
            total=keys(r,byte_hex(native['serializedUtf8Hex']),len(source));require(r['outputCharacters']>=total,'cumulative output covers actual UTF16 keys');matched+=1
        else:
            require(r['matched'] is False and r['actualKeys']==[] and r['workUsed']==r['outputCharacters']==0,'18 main historical exclusions remain unexecuted, not compatibility passes');unexecuted+=1
    probe_rows,probe_raw,probe_lines=records(probes);require(len(probe_rows)==18,'complete2 direct+14 token+2 UB probes')
    byid={x['id']:x for x in plan['cases']}
    for r,native_id in zip(probe_rows[:2],('constructor-probe-10','constructor-stod-underflow')):
        fields(r,DIRECT,'direct');item=byid[native_id];ref=item['nativeReference'];expected=byte_hex(ref['record']['serializedUtf8Hex'])
        require(expected==b'0' and ref['record']['value']=={'kind':'integer','decimal':'0'},'original catch0 reference; never new numeric golden')
        require(r['id']=='additional-public-probe-'+native_id and r['originalNativeId']==native_id and r['originalRecordSha256']==ref['lineSha256WithoutTerminator'] and r['expression']==item['nativeCase']['input'],'separate direct ID/order/expression/original hash')
        require(r['qualification']==DIRECT_QUAL and all(r[k] is False for k in ('nativeParserExecuted','nativeDirectiveExecuted','actualTypedObserved','contextEquivalent')),'separate CSharp direct / Native unexecuted boundary')
        require(r['producerCompiled'] is True and r['matched'] is True and r['expectedSerializedUtf8Hex']==ref['record']['serializedUtf8Hex'],'separate actual compiled/matched key0')
        require(r['sourceShaBefore']==r['sourceShaAfter']==source_sha(['public direct constructor edge source RAW']),'direct source exact before/after SHA')
        error(r,False);counter(r);keys(r,expected,1);require(r['workUsed']>0,'direct actual work')
    definitions=[('public-token-'+p['text'],p['text'],p['sizeLexemeMatchesStaticRule'],TOKEN_QUAL) for p in proposals['tokenRuleProbes']]
    upper=next(p for p in proposals['unsupported'] if p['id']=='int64-upper-cast');require(upper['executed'] is False and len(upper['inputs'])==2,'Native UB boundaries unexecuted')
    definitions += [('public-reject-'+s,s,False,UB_QUAL) for s in upper['inputs']]
    successes=0;rejections=0
    for r,(id,expression,success,qualification) in zip(probe_rows[2:],definitions):
        fields(r,PROBE,'compile probe');require(r['id']==id and r['expression']==expression and r['qualification']==qualification,'probe ID/order/expression/qualification')
        require(r['nativeExecuted'] is False and r['actualTypedObserved'] is False and r['contextEquivalent'] is False and r['nativeGoldenValue'] is None and r['actualKeys']==[],'compile-only; no Native UB value or typed equivalence')
        require(r['expectedCompileSuccess'] is success and r['producerCompiled'] is success and r['passed'] is True,'expected public compile outcome required');counter(r);error(r,not success)
        if success:successes+=1
        else:rejections+=1
    require(matched==77 and unexecuted==18 and successes==3 and rejections==13,'counts remain separated')
    return {'passed':True,'mainCases':95,'bridgeCases':77,'unexecutedCases':18,'constructorOnlyCases':14,'platformRoundingCases':4,'additionalDirectCases':2,'tokenProbeCases':14,'tokenCompileSuccessCases':3,'tokenRejectCases':11,'ubRejectCases':2,'rejectionCases':13,'actualSha256':sha(raw),'probesSha256':sha(probe_raw),'additionalDirectRawRowsSha256':sha(b'\n'.join(probe_lines[:2])+b'\n'),'nativeGoldenSha256':GOLDEN,'publicPlanSha256':sha(public_plan.read_bytes()),'probePlanSha256':sha(probe_plan.read_bytes()),'actualTypedObserved':False,'actualTypedValueObserved':False,'contextEquivalent':False,'nativeParserExecuted':False,'nativeDirectiveExecuted':False,'nativeReaderExecutedImplicitly':False,'budgetOutputCounterObserved':{'main':True,'additionalDirect':False,'compileProbes':False},'qualification':'CSharp serialized keys and compile rejection only;18 historical exclusions are not main compatibility passes; Native qualification is separate; direct/probe output counter absent in agreed writer'}
def main():
    p=argparse.ArgumentParser();p.add_argument('--actual',type=pathlib.Path,required=True);p.add_argument('--probes',type=pathlib.Path,required=True);p.add_argument('--fixture',type=pathlib.Path,required=True);p.add_argument('--public-plan',type=pathlib.Path);p.add_argument('--probe-plan',type=pathlib.Path);p.add_argument('--receipt',type=pathlib.Path);a=p.parse_args()
    result=verify(a.actual,a.probes,a.fixture,a.public_plan or a.fixture/'public-case-plan.json',a.probe_plan or a.fixture/'public-probes-and-boundaries.json')
    if a.receipt:
        with a.receipt.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2);f.write('\n')
    print(json.dumps(result))
if __name__=='__main__':main()
