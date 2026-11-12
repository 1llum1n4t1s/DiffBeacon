"""独立source/data reader。captureの関数・数学期待値・製品実装を呼ばない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,argparse
BASE=pathlib.Path(__file__).resolve().parent
COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
ZIP_SHA='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
def sha(b):return hashlib.sha256(b).hexdigest()
def require(v,reason):
    if not v:raise AssertionError(reason)
def load(b):return json.loads(b)
def independent_block(raw,marker,semi=False):
    """block31方式と同じC++ lexical境界をreader自身で走査する。"""
    start=raw.index(marker.encode());pos=raw.index(b'{',start);depth=0;quoted=None
    while pos<len(raw):
        if quoted:
            if raw[pos]==92:pos+=2;continue
            if raw[pos]==quoted:quoted=None
        elif raw[pos:pos+2]==b'//':
            newline=raw.find(b'\n',pos);pos=len(raw) if newline<0 else newline;continue
        elif raw[pos:pos+2]==b'/*':pos=raw.index(b'*/',pos+2)+2;continue
        elif raw[pos] in [34,39]:quoted=raw[pos]
        elif raw[pos]==123:depth+=1
        elif raw[pos]==125:
            depth-=1
            if depth==0:
                end=pos+1
                if semi:require(raw[end:end+1]==b';','original semicolon');end+=1
                return start,end
        pos+=1
    raise AssertionError(marker)
def verify(reference,actual1=None,actual2=None):
    fixed=load((BASE/'fixed-manifest.json').read_bytes())
    files={p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file()}
    require(files=={e['path'] for e in fixed['files']}|{'fixed-manifest.json'},'exact family files')
    for e in fixed['files']:
        b=(BASE/e['path']).read_bytes();require(len(b)==e['size'] and sha(b)==e['sha256'],'fixed bytes '+e['path'])
    require(sha(reference.read_bytes())==ZIP_SHA,'sibling immutable reference ZIP')
    sourceManifest=load((BASE/'source-manifest.json').read_bytes());p=load((BASE/'provenance.json').read_bytes());require(sourceManifest['sources']==p['sources'],'source list')
    require(p['commit']==COMMIT and p['reusedArchiveSha256']==ZIP_SHA,'fixed source commit/archive')
    rawSources={}
    with zipfile.ZipFile(reference) as z:
        require(z.testzip() is None,'reference CRC')
        for s in p['sources']:
            raw=z.read('original/'+s['path']) if s['origin']=='sibling-fixed-reference-zip' else (BASE/'additional-original'/s['path']).read_bytes()
            require(len(raw)==s['size'] and sha(raw)==s['sha256'] and hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()==s['blob'],'raw Git blob '+s['path']);rawSources[s['path']]=raw
        require(sha(z.read('original/LICENSE.md'))=='aef8b4222b79d0dbf6bf17cfff71c90a6a6bb8917a4162abe417b469ed22da2e','GPL license bytes')
        require(sha(z.read('original/Externals/poco/LICENSE'))=='c4b1e1e5f36d8331737231fefcc30f5714326aec7c387ad59a8115eb0ba7d6b5','BSL license bytes')
    require(len(p['sources'])==15,'15 source blobs')
    proofManifest=load((BASE/'native-proof-manifest.json').read_bytes());receipts=[]
    with zipfile.ZipFile(BASE/'native-proof.zip') as z:
        require(z.testzip() is None and len(z.infolist())==len(proofManifest),'proof entries/CRC')
        require(sorted(z.namelist())==sorted(e['path'] for e in proofManifest),'proof exact entry set')
        for e in proofManifest:
            raw=z.read(e['path']);require(len(raw)==e['size'] and sha(raw)==e['sha256'],'proof entry '+e['path'])
        for e in p['extractions']:
            raw=rawSources[e['path']];start,end=e['startByte'],e['endByte']
            require(sha(raw[start:end])==e['sha256'] and raw[:start].count(b'\n')+1==e['startLine'] and raw[:end].count(b'\n')+1==e['endLine'],'span SHA/line '+str(e['file']))
            if e.get('marker'):require(independent_block(raw,e['marker'],e.get('semicolon',False))==(start,end),'independent lexical block '+str(e['file']))
            if e['file']:
                expected=raw[start:end]+(b'\n' if e['outerLf'] else b'')
                for run in ['run1','run2']:require(z.read(run+'/'+e['file'])==expected,'unmodified native extraction '+run+'/'+e['file'])
        registrations=[e for e in p['extractions'] if e.get('kind')=='registration']
        require(len(registrations)==4,'four registration spans')
        for e in registrations:require(rawSources[e['path']][e['startByte']:e['endByte']].endswith(b'Func, 1, 2}'),'original registered arity 1..2')
        golden=(BASE/'golden.ndjson').read_bytes();cases=load((BASE/'cases.json').read_bytes());rows=[load(line) for line in golden.splitlines()]
        require(len(cases)==len(rows)==141 and len({r['id'] for r in rows})==141,'141 cases exact')
        require([c['id'] for c in cases]==[r['id'] for r in rows],'input/output order')
        for run in ['run1','run2']:
            require(z.read(run+'/golden.ndjson')==golden,'original double native stdout '+run)
            r=load(z.read(run+'/receipt.json'));receipts.append(r)
            require(r['exitCode']==0 and r['timeout'] is False and r['stdoutEof'] and r['stderrEof'] and r['heldProcessHandleSignaled'],'native completion '+run)
            require(r['sourceCommit']==COMMIT and r['caseCount']==141 and r['inputSha256']==sha((BASE/'cases.json').read_bytes()) and r['stdoutSha256']==sha(golden) and r['stderrSha256']==sha(b''),'native input/output receipt '+run)
            require(sha(z.read(run+'/reference.exe'))==r['executableSha256'],'retained native exe '+run)
            require(z.read(run+'/adapter.cpp')==(BASE/'adapter.cpp').read_bytes(),'actual adapter input '+run)
            require(z.read(run+'/reference.stderr')==b'','empty native stderr '+run)
            compileReceipt=load(z.read(run+'/compile-receipt.json'))
            require(compileReceipt['exitCode']==0 and compileReceipt['stdoutEof'] and compileReceipt['stderrEof'] and not compileReceipt['timeout'],'actual native compile '+run)
            for name,digest in compileReceipt['inputSha256'].items():require(sha(z.read(run+'/'+name))==digest,'compile input '+run+'/'+name)
        require(receipts[0]['pid']!=receipts[1]['pid'] and receipts[0]['birthFileTime']!=receipts[1]['birthFileTime'],'independent child identity')
        require(receipts[0]['adapterSha256']==receipts[1]['adapterSha256']==sha((BASE/'adapter.cpp').read_bytes()),'actual native adapter bytes both compiles')
    for actual in [actual1,actual2]:
        if actual:require(actual.read_bytes()==golden,'reproduced stdout equals immutable original')
    byId={r['id']:r for r in rows}
    for c,r in zip(cases,rows):
        require(r['contextRetained'] is True and r['steps'],'context copy retention '+c['id'])
        for step in r['steps']:
            require(all(t['contextRetained'] for t in step['trace']),'trace context '+c['id'])
        if c['mode'] in ['scan','literal-scan']:
            first,repeat=r['steps'];require(first['label']=='first' and repeat['label']=='repeat','scan steps')
            require(first['value']==repeat['value'] and first['errorUtf8Hex']==repeat['errorUtf8Hex'],'same-node repeat result')
            if first['returned']:require(repeat['trace']==[] and first['cacheAfter']==repeat['cacheAfter']==1,'only success cached '+c['id'])
            else:require(first['cacheAfter']==repeat['cacheAfter']==0 and first['trace']==repeat['trace'],'error no partial outer cache '+c['id'])
            if c['conditions'] is None:require([t['lineIndex'] for t in first['trace']]==list(range(len(first['trace']))),'ascending value scan')
            else:
                seen=[t['lineIndex'] for t in first['trace'] if t['node']=='condition'];require(seen==list(range(len(seen))),'ascending condition scan')
                for j,t in enumerate(first['trace']):
                    if t['node']=='value':require(j>0 and first['trace'][j-1]['node']=='condition' and first['trace'][j-1]['lineIndex']==t['lineIndex'],'condition precedes value')
            for step in r['steps']:
                require(step['outerLineIndexBefore']==step['outerLineIndexAfter']==c['outerLineIndex'],'outer context lineIndex retained')
                if step['returned'] and step['value']['kind']=='timestamp':require(not step['serializationApplied'] and step['serializedUtf8Hex'] is None,'typed Timestamp formatting not executed')
                elif step['returned']:require(step['serializationApplied'] and isinstance(step['serializedUtf8Hex'],str),'actual ToStringValue executed')
    zeroIds=[c['id'] for c in cases if c['id'].endswith('-condition-all-false') or c['id'].endswith('-condition-all-false-value-throws')]
    require(len(zeroIds)==8,'exact eight added zero-condition cases')
    for id in zeroIds:
        first,repeat=byId[id]['steps']
        require(first['returned'] and repeat['returned'] and first['value']==repeat['value']=={'kind':'none'},'all-false condition zero count none '+id)
        require(first['errorUtf8Hex']==repeat['errorUtf8Hex']=='','all throw values suppressed '+id)
        require([(t['node'],t['lineIndex']) for t in first['trace']]==[('condition',i) for i in range(3)],'condition only ascending trace, no value evaluation '+id)
        require(first['statistics']==repeat['statistics'] and first['statistics'][0]['count']==0 and first['statistics'][0]['valueType']==0,'actual zero-count original statistics '+id)
        require(first['cacheHitAtEntry'] is False and repeat['cacheHitAtEntry'] is True and first['cacheAfter']==repeat['cacheAfter']==1 and repeat['trace']==[],'success none result cached '+id)
        require(first['serializationApplied'] and repeat['serializationApplied'] and first['serializedUtf8Hex']==repeat['serializedUtf8Hex']=='6e6f6e65','original none serializer '+id)
    steps=byId['nested-inner-success-outer-error']['steps'];require([s['cacheAfter'] for s in steps]==[1,2,2],'inner cache retained after outer error')
    require(steps[0]['returned'] is False and steps[1]['returned'] is True and steps[2]['trace']==[],'outer retry plus inner hit')
    require(byId['cache-live-mutation']['steps'][1]['trace']==[],'provider mutation does not evict pointer cache')
    require([s['cacheAfter'] for s in byId['shared-four-functions-one-node']['steps']]==[1,1,1,1],'all functions share statistics key')
    require(byId['equivalent-distinct-value-nodes']['steps'][-1]['cacheAfter']==2,'distinct value identity')
    require(byId['same-value-distinct-condition-nodes']['steps'][-1]['cacheAfter']==2,'distinct condition identity')
    require(byId['same-value-same-condition-cache']['steps'][-1]['trace']==[],'same condition identity cache')
    for id in ['caller-live-rule-cache','caller-next-invocation-new-epoch']:
        for step in byId[id]['steps']:
            require(step['callerSucceeded'] and step['trace'] and step['independentRuleCaches'],'original live caller succeeded with independent rule caches')
            roots=[t for t in step['trace'] if t['node'] in ['rule0','rule1']]
            require([(t['lineIndex'],t['node']) for t in roots]==[(i,name) for i in range(3) for name in ['rule0','rule1']],'original outer-line inner-rule order')
    require(len(byId['caller-next-invocation-new-epoch']['steps'])==2,'next invocation epoch captured')
    require(not byId['caller-failure-keeps-prior-setline']['steps'][0]['callerSucceeded'],'caller error captured')
    require(byId['caller-failure-keeps-prior-setline']['steps'][0]['ruleErrors']==[0,11],'runtime original evaluation error enum=11')
    errorHeader=rawSources['Src/FilterEngine/FilterError.h'];require(b'FILTER_ERROR_INVALID_REGULAR_EXPRESSION = 8' in errorHeader and b'FILTER_ERROR_EVALUATION_FAILED = 11' in errorHeader,'fixed original error enum bytes')
    excluded=load((BASE/'unsupported-boundaries.json').read_bytes());require(all(e['executed'] is False for e in excluded),'unsupported boundaries not executed')
    return dict(passed=True,caseCount=141,sourceGitBlobs=15,exactSourceSpans=len(p['extractions']),nativeProcesses=2,nativeCompileRuns=2,nativeStdoutSha256=sha(golden),proofZipSha256=sha((BASE/'native-proof.zip').read_bytes()),actualComparisonCount=int(actual1 is not None)+int(actual2 is not None),scope='native original aggregates/serializer/provider/caller at explicit adapter boundary; no public/parser/fullAST compatibility')
if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('--reference-zip',type=pathlib.Path,required=True);ap.add_argument('--actual1',type=pathlib.Path);ap.add_argument('--actual2',type=pathlib.Path);args=ap.parse_args()
    print(json.dumps(verify(args.reference_zip,args.actual1,args.actual2)))
