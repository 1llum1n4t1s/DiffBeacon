"""Independent strict reader for duration public serialized diagnostics (external draft).
No product execution; no Native parser/type/cache/normalizer equivalence claim.
"""
import sys
sys.dont_write_bytecode = True
import argparse, collections, hashlib, json, pathlib, re
GOLDEN = 'bbd10685c14154365a905239292b0fcd3024d31ab70cd7eaf96b7d5453693aec'
PLAN = 'e891e218b68d8501d31fa38ff84a16806f6913df60a28b8b48a96b0dadd70ab9'
PROPOSALS = 'fc7b7ebd5d1146f1c7f95660199ee8c87f181ed7299b74af67b0620132484f18'
PROOF = 'faf6aade2043c2845ea4ec9b268f523426cb7712913bde699a38a9054c377d8e'
MAIN = set('id classification publicExpression qualification nativeParserExecuted nativeDirectiveExecuted contextEquivalent actualTypedObserved actualTypedValueObserved actualNumericBitsHex nativeValueKind nativeValueBitsHex nativeRecord rawNativeReferenceLine nativeReference nativeGoldenSha256 nativeLineNumber nativeRecordSha256 reason publicCompileExecuted producerCompiled executed skipped matched expectedSerializedUtf8Hex actualKeys actualExceptionType actualErrorUtf8Hex sourceRetained sourceShaBefore sourceShaAfter workUsed outputCharacters'.split())
PROBE = set('id expression qualification staticProposal nativeExecuted nativeParserExecuted nativeDirectiveExecuted actualTypedObserved contextEquivalent publicCompileExecuted producerCompiled executed expectedCompileSuccess passed nativeGoldenValue workUsed outputCharacters actualKeys actualExceptionType actualErrorUtf8Hex'.split())
ENGINEERING = set('id engineering classification qualification expression expectedCompileSuccess publicCompileExecuted producerCompiled executed nativeParserExecuted nativeDirectiveExecuted actualTypedObserved actualTypedValueObserved contextEquivalent expectedSerializedUtf8Hex passed sourceRetained sourceShaBefore sourceShaAfter workUsed outputCharacters actualKeys actualExceptionType actualErrorUtf8Hex'.split())
BUDGET = set('id qualification compileWork firstWork secondWork compileOutputCharacters firstOutputCharacters secondOutputCharacters budgetDomain limitedWork limitedRejected compileCancelled transformCancelled passed actualKeys sourceShaBefore sourceShaAfter'.split())
KEY = {'text', 'utf16Length', 'utf8Length', 'utf8Hex', 'sha256'}
FALSE_FLAGS = ('nativeParserExecuted','nativeDirectiveExecuted','actualTypedObserved','actualTypedValueObserved','contextEquivalent')
REJECTIONS = {'System.ArgumentException','System.ArgumentOutOfRangeException','System.ArgumentNullException','System.FormatException','System.ArithmeticException','System.OverflowException','System.DivideByZeroException','System.InvalidOperationException'}
MAIN_QUAL = 'public serialized UTF8 keys only; Native parser/directive/typed AST context unexecuted'
PROBE_QUAL = 'separate public Compile observation; original static proposal preserved; no Native parser/directive/typed qualification'
ENGINEERING_QUAL = 'engineering public Compile/Transform serialized observation; not original Native parser/directive/typed compatibility; historical main106/static-proposals21 unchanged'

def require(ok, reason):
    if not ok: raise AssertionError(reason)
def sha(b): return hashlib.sha256(b).hexdigest()
def same(a,b): return json.dumps(a,sort_keys=True,ensure_ascii=True,separators=(',',':'))==json.dumps(b,sort_keys=True,ensure_ascii=True,separators=(',',':'))
def public_expression(item):
    case=item['nativeCase'];expression=item['expression']
    if case['mode']!='array':return expression
    require(isinstance(case['items'],list) and all(isinstance(x,str) for x in case['items']), 'recorded native array items')
    items=', '.join(case['items']);label='['+items+']';actual='Array('+items+')'
    if case['aggregate']:label=case['aggregate']+'('+label+')';actual=case['aggregate']+'('+actual+')'
    require(expression==label, 'immutable array label -> public Array function mapping')
    return actual

def unique(pairs):
    result = {}
    for key,value in pairs:
        require(key not in result, 'duplicate JSON field: '+key)
        result[key]=value
    return result
def load(raw):
    return json.loads(raw.decode('utf-8','strict'), object_pairs_hook=unique,
        parse_constant=lambda v: (_ for _ in ()).throw(AssertionError('nonfinite JSON: '+v)))
def fields(row, names, label): require(isinstance(row,dict) and set(row)==names, 'exact '+label+' field set')
def read(path, maximum=32*1024*1024):
    require(path.is_file() and path.stat().st_size <= maximum,'missing/oversize input: '+str(path))
    raw=path.read_bytes(); require(len(raw)<=maximum,'input changed beyond limit')
    return raw
def ndjson(path):
    raw=read(path); require(raw.endswith(b'\n') and not raw.startswith(b'\xef\xbb\xbf'),'complete LF NDJSON/no BOM')
    lines=raw.split(b'\n')[:-1]; require(lines and all(lines),'blank NDJSON row')
    return [load(line) for line in lines], raw, lines
def byte_hex(value):
    require(isinstance(value,str) and re.fullmatch(r'(?:[0-9a-f]{2})*',value) is not None,'canonical lowercase byte hex')
    return bytes.fromhex(value)
def flags(row):
    for key in FALSE_FLAGS:
        if key in row: require(row[key] is False,'no Native/type/context qualification: '+key)
def integer(value,limit,label): require(type(value) is int and 0<=value<=limit,label+' integer domain')
def counters(row):
    integer(row['workUsed'],128*1024*1024,'cumulative work')
    integer(row['outputCharacters'],64*1024*1024,'cumulative output')
def source_sha(source):
    # Known ASCII writer inputs avoid .NET default JSON escaping differences.
    require(all(all((ord(c)<128 or c=='ſ') and c not in '<>&\"\'' for c in item) for item in source),'source serialization domain')
    return sha(json.dumps(source,separators=(',',':'),ensure_ascii=True).replace('\\u017f','\\u017F').encode())
def source(row,expected):
    if 'sourceRetained' in row: require(row['sourceRetained'] is True,'source retained')
    require(row['sourceShaBefore']==row['sourceShaAfter']==source_sha(expected),'exact original source SHA before/after')
def error(row,rejected):
    if rejected:
        require(row['actualExceptionType'] in REJECTIONS,'real public rejection, not cancellation')
        message=byte_hex(row['actualErrorUtf8Hex']);require(bool(message) and bool(message.decode('utf-8','strict')),'nonempty rejection message')
    else: require(row['actualExceptionType'] is None and row['actualErrorUtf8Hex'] is None,'no unexpected error')
def keys(row,expected,count):
    require(type(row['actualKeys']) is list and len(row['actualKeys'])==count,'actual key count')
    total=0
    for item in row['actualKeys']:
        fields(item,KEY,'key'); require(type(item['text']) is str,'key text')
        raw=item['text'].encode('utf-8','strict'); units=len(item['text'].encode('utf-16-le','strict'))//2
        require(raw==expected and byte_hex(item['utf8Hex'])==raw and item['sha256']==sha(raw),'actual key bytes/hash match independent expectation')
        require(type(item['utf16Length']) is int and item['utf16Length']==units and type(item['utf8Length']) is int and item['utf8Length']==len(raw),'actual UTF16/UTF8 length')
        total+=units
    return total

def verify(actual,probes,engineering,fixture,proof):
    golden_raw=read(fixture/'golden.ndjson'); require(sha(golden_raw)==GOLDEN,'immutable Native golden SHA')
    plan_raw=read(fixture/'public-case-plan.json');require(sha(plan_raw)==PLAN,'immutable public plan SHA')
    proposals_raw=read(fixture/'public-probes-and-boundaries.json');require(sha(proposals_raw)==PROPOSALS,'immutable static proposals SHA')
    require(sha(read(proof,64*1024*1024))==PROOF,'immutable Native proof SHA; proof not executed by this reader')
    plan=load(plan_raw); proposal_plan=load(proposals_raw); native_rows,_,native_lines=ndjson(fixture/'golden.ndjson')
    # Frozen golden uses CRLF; references bind bytes excluding the entire terminator.
    require(all(line.endswith(b'\r') for line in native_lines),'immutable Native CRLF terminators')
    native_lines=[line[:-1] for line in native_lines]
    require(len(native_rows)==len(plan['cases'])==106,'Native106 complete')
    require(collections.Counter(x['classification'] for x in plan['cases'])=={'planned-public-serialized-bridge':92,'constructor-only-unsupported-public':10,'defined-cast-platform-rounding-observation':4},'fixed 92/10/4 classifications')
    require(len({x['id'] for x in plan['cases']})==106,'unique immutable IDs')
    for n,(item,native,line) in enumerate(zip(plan['cases'],native_rows,native_lines),1):
        ref=item['nativeReference']; require(item['id']==native['id'] and ref['record']==native,'Native ID/record binding')
        require(ref['file']=='golden.ndjson' and ref['fileSha256']==GOLDEN and type(ref['lineNumber']) is int and ref['lineNumber']==n and ref['lineSha256WithoutTerminator']==sha(line) and byte_hex(ref['lineUtf8HexWithoutTerminator'])==line,'raw Native reference binding')
    rows,raw,_=ndjson(actual);require(len(rows)==106,'main106 complete')
    for n,(row,item,native,line) in enumerate(zip(rows,plan['cases'],native_rows,native_lines),1):
        fields(row,MAIN,'main');flags(row); bridge=item['classification']=='planned-public-serialized-bridge'
        require(row['id']==item['id'] and row['classification']==row['reason']==item['classification'] and row['qualification']==MAIN_QUAL,'main ID/order/classification')
        require(same(row['nativeReference'],item['nativeReference']) and same(row['nativeRecord'],native) and row['rawNativeReferenceLine'].encode('utf-8')==line and row['nativeGoldenSha256']==GOLDEN and row['nativeRecordSha256']==sha(line) and type(row['nativeLineNumber']) is int and row['nativeLineNumber']==n,'main raw immutable Native provenance')
        require(row['actualNumericBitsHex'] is None and row['nativeValueKind']==native['value']['kind'] and row['nativeValueBitsHex']==native['value'].get('bitsHex'),'native kind/bits reference only')
        require(row['publicExpression']==(public_expression(item) if bridge else None),'recorded public expression; frozen native label preserved')
        require(row['expectedSerializedUtf8Hex']==native['serializedUtf8Hex'],'frozen expected bytes')
        require(row['publicCompileExecuted'] is bridge and row['producerCompiled'] is bridge and row['executed'] is bridge and row['skipped'] is (not bridge),'actual compile/execute/skip')
        count=item['nativeCase']['providerCount'] if bridge else 0
        source(row,['source RAW '+item['id']+' '+str(i) for i in range(count)]);counters(row);error(row,False)
        if bridge:
            require(row['matched'] is True and row['workUsed']>0,'actual bridge work/success')
            require(row['outputCharacters']>=keys(row,byte_hex(native['serializedUtf8Hex']),count),'cumulative output covers current keys')
        else: require(row['matched'] is False and row['actualKeys']==[] and row['workUsed']==row['outputCharacters']==0,'historical14 unexecuted, not passes')
    probe_rows,probe_raw,_=ndjson(probes)
    definitions=proposal_plan['proposals']+proposal_plan['excluded'];require(len(definitions)==len(probe_rows)==21,'21 static compile observations')
    for row,proposal in zip(probe_rows,definitions):
        fields(row,PROBE,'proposal');flags(row);success=proposal.get('expectedWholeTokenShape',True) if proposal in proposal_plan['proposals'] else proposal['id']=='excluded-2'
        require(row['id']==proposal['id'] and row['expression']==proposal['input'] and same(row['staticProposal'],proposal) and row['qualification']==PROBE_QUAL,'proposal unchanged/expression/order')
        require(row['nativeExecuted'] is False and row['nativeGoldenValue'] is None and row['executed'] is False and row['actualKeys']==[],'compile-only, no fake Native golden')
        require(row['publicCompileExecuted'] is True and row['expectedCompileSuccess'] is success and row['producerCompiled'] is success and row['passed'] is True,'actual compile outcome')
        counters(row);error(row,not success) # Rejection can retain compile storage/work reservations.
    erows,eraw,_=ndjson(engineering);require(len(erows)==25,'one budget +24 engineering observations')
    row=erows[0];fields(row,BUDGET,'budget');require(row['id']=='engineering-shared-budget-cancellation' and row['qualification']=='engineering observation; not fixed Native106','engineering budget identity')
    require(row['budgetDomain']=='shared cumulative compile storage and Transform work/output; not exact API-only cost','budget semantics')
    for field in ('compileWork','firstWork','secondWork','limitedWork'):integer(row[field],128*1024*1024,field)
    for field in ('compileOutputCharacters','firstOutputCharacters','secondOutputCharacters'):integer(row[field],64*1024*1024,field)
    require(row['secondWork']>row['firstWork']>row['compileWork'] and row['secondOutputCharacters']>row['firstOutputCharacters']>row['compileOutputCharacters'],'real cumulative increases')
    require(row['limitedWork']<=row['firstWork'] and all(row[k] is True for k in ('limitedRejected','compileCancelled','transformCancelled','passed')),'shared budget/cancellation outcomes')
    source(row,['left raw\r','right RAW','']);require(row['firstOutputCharacters']>=keys(row,b'1500',3),'first output covers keys')
    definitions=[]
    for ident,adjacent,spaced in [('milliseconds-integer-and','1ms==1000AND TRUE','1ms==1000 AND TRUE'),('longest-unit-and','1milliseconds==1msAND TRUE','1milliseconds==1ms AND TRUE'),('seconds-integer-or','1s==1000000OR FALSE','1s==1000000 OR FALSE')]:
        for suffix,expr in [('adjacent',adjacent),('spaced',spaced)]:definitions.append(('engineering-'+ident+'-'+suffix,expr,'74727565',True,'engineering-adjacency-public-serialized-observation',None))
    for expr in ('1MB==1048576','1m==60000000','1ms==1000'):definitions.append(('engineering-precedence-'+expr,expr,'74727565',True,'engineering-size-duration-precedence',None))
    for expr in ('1millisecondsX','1msecX','1ms2','1msANDTRUE','1xs','1 ms','1e2ms','.1ms','1.ms','1ſ'):definitions.append(('engineering-reject-'+expr,expr,None,False,'engineering-public-rejection-observation',None))
    catch0=next(x for x in proposal_plan['excluded'] if x['id']=='excluded-2')
    require(catch0['input']=='9'*309+'w','309 digit parse overflow catch0 independent of finite scaled cast UB')
    definitions.append(('engineering-proposal-raw-stod-overflow-catch0',catch0['input'],'30',True,'engineering-raw-stod-overflow-catch0; Native excluded unexecuted',None))
    for item in plan['cases']:
        if item['classification']=='defined-cast-platform-rounding-observation':definitions.append(('engineering-public-'+item['id'],item['nativeCase']['input'],item['nativeReference']['record']['serializedUtf8Hex'],True,item['classification'],item))
    require(len(definitions)==24,'24 independent engineering definitions')
    for row,(ident,expr,expected,success,classification,item) in zip(erows[1:],definitions):
        fields(row,ENGINEERING|({'originalNativeId','nativeReference'} if item else set()),'engineering');flags(row)
        require(row['id']==ident and row['expression']==expr and row['classification']==classification and row['qualification']==ENGINEERING_QUAL and row['engineering'] is True,'engineering identity/classification')
        require(row['publicCompileExecuted'] is True and row['producerCompiled'] is success and row['executed'] is success and row['expectedCompileSuccess'] is success and row['passed'] is True,'actual engineering compile/execute outcome')
        require(row['expectedSerializedUtf8Hex']==expected,'engineering expectation')
        if item:require(row['originalNativeId']==item['id'] and same(row['nativeReference'],item['nativeReference']),'rounding provenance separate from main unexecuted row')
        source(row,['engineering source RAW '+ident]);counters(row);error(row,not success)
        if success:require(row['workUsed']>0 and row['outputCharacters']>=keys(row,byte_hex(expected),1),'engineering current keys and cumulative output')
        else:require(row['actualKeys']==[],'compile rejection has no transform keys')
    return {'passed':True,'mainCases':106,'publicBridgeCases':92,'arrayFunctionBridgeObservations':6,'unexecutedCases':14,'constructorOnlyCases':10,'roundingUnexecutedCases':4,'proposalCompileObservations':21,'engineeringObservations':24,'sharedBudgetObservations':1,'diagnosticsSha256':sha(raw),'probesSha256':sha(probe_raw),'engineeringSha256':sha(eraw),'nativeGoldenSha256':GOLDEN,'publicPlanSha256':PLAN,'staticProposalsSha256':PROPOSALS,'nativeProofSha256':PROOF,'nativeParserExecuted':False,'nativeDirectiveExecuted':False,'actualTypedObserved':False,'actualTypedValueObserved':False,'contextEquivalent':False,'normalizerOnlyQualified':False,'cacheEquivalent':False,'nativeReaderExecutedImplicitly':False,'qualification':'Public Compile/reject and current serialized UTF8 keys only; summary assertions are not evidence; no Native source counter/rowcount equality claim.'}

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('actual','probes','engineering','fixture','native-proof'):parser.add_argument('--'+name,type=pathlib.Path,required=True)
    parser.add_argument('--receipt',type=pathlib.Path)
    args=parser.parse_args()
    result=verify(args.actual,args.probes,args.engineering,args.fixture,args.native_proof)
    if args.receipt:
        with args.receipt.open('x',encoding='utf-8',newline='\n') as stream:json.dump(result,stream,indent=2);stream.write('\n')
    print(json.dumps(result))
if __name__=='__main__':main()
