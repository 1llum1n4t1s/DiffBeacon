"""Git不要。外部fresh-onlyに原関数を抽出・MSVC compile・実child stdout採取。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,subprocess,ctypes,datetime,argparse,os
BASE=pathlib.Path(__file__).resolve().parent
COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
ZIP_SHA='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
def sha(b):return hashlib.sha256(b).hexdigest()
def write(p,b):
    p.parent.mkdir(parents=True,exist_ok=True)
    if p.exists():raise RuntimeError('fresh-only '+str(p))
    p.write_bytes(b if isinstance(b,bytes) else b.encode('utf8'))
def validate(p,repo):
    p=p.absolute()
    for a in [p,*p.parents]:
        assert not a.name.startswith('.') and a.name.lower()!='artifacts' and not a.is_symlink() and not a.is_junction(),str(a)
        if a.exists():assert not(getattr(a.stat(),'st_file_attributes',0)&0x400),str(a)
    p=p.resolve()
    roots=[repo.resolve(),BASE,pathlib.Path.home()/'.codex']
    if os.environ.get('CODEX_HOME'):roots.append(pathlib.Path(os.environ['CODEX_HOME']).resolve())
    for r in roots:
        assert not p.is_relative_to(r),str(p)
    return p
def block(b,marker):
    a=b.index(marker.encode());brace=b.index(b'{',a);i=brace;level=0;state='code'
    while i<len(b):
        c=b[i:i+1];n=b[i:i+2]
        if state=='code':
            if n==b'//':state='line';i+=1
            elif n==b'/*':state='comment';i+=1
            elif c==b'"':state='string'
            elif c==b"'":state='char'
            elif c==b'{':level+=1
            elif c==b'}':
                level-=1
                if level==0:return a,i+1
        elif state=='line':
            if c==b'\n':state='code'
        elif state=='comment':
            if n==b'*/':state='code';i+=1
        elif c==b'\\':i+=1
        elif (state=='string' and c==b'"') or(state=='char' and c==b"'"):state='code'
        i+=1
    raise RuntimeError(marker)
def datum(n):
    t=n['type'];v=n.get('value')
    if t=='integer':return 'integer('+str(v)+'LL)'
    if t=='double':return 'number('+repr(v)+')'
    if t=='bool':return 'boolDatum('+str(v).lower()+')'
    if t=='none':return 'none()'
    if t=='array':return 'array({'+','.join(map(datum,v))+'})'
    if t=='timestamp':return 'timestamp('+str(v)+'LL)'
    if t in ['string','throw','raw-string']:
        values=v if t=='raw-string' else list(v.encode('utf8'))
        return ('fail' if t=='throw' else 'str')+'(bytes({'+','.join(map(str,values))+'}))'
    raise ValueError(t)
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--initial-observation',action='store_true');ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--repo',type=pathlib.Path,required=True);ap.add_argument('--reference-zip',type=pathlib.Path,required=True);ap.add_argument('--compiler',type=pathlib.Path,required=True);ap.add_argument('--vcvars',type=pathlib.Path,required=True);a=ap.parse_args()
    out=validate(a.output,a.repo);assert not out.exists();out.mkdir(parents=True);(out/'compiler-temp').mkdir()
    assert sha(a.reference_zip.read_bytes())==ZIP_SHA
    original=zipfile.ZipFile(a.reference_zip);fixed=json.loads((BASE.parent/'LineTransformAggregates/source-manifest.json').read_bytes());data={};ext=[]
    for s in fixed['sources']:
        k='original/'+s['path'];raw=original.read(k) if s['origin']=='sibling-fixed-reference-zip' else (BASE.parent/'LineTransformAggregates/additional-original'/s['path']).read_bytes()
        assert len(raw)==s['size'] and sha(raw)==s['sha256'] and hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()==s['blob']
        data[s['path']]=raw
    def extract(path,name,marker,semicolon=False):
        raw=data[path];start,end=block(raw,marker)
        if semicolon:assert raw[end:end+1]==b';';end+=1
        if name:write(out/name,raw[start:end]+b'\n')
        ext.append(dict(file=name,path=path,marker=marker,startByte=start,endByte=end,startLine=raw[:start].count(b'\n')+1,endLine=raw[:end].count(b'\n')+1,sha256=sha(raw[start:end]),outerLf=bool(name),semicolon=semicolon))
    cpp='Src/FilterEngine/FilterExpressionNodes.cpp';h='Src/FilterEngine/FilterExpressionNodes.h';fh='Src/FilterEngine/FilterExpression.h';fc='Src/FilterEngine/FilterExpression.cpp';ft='Src/FileTransform.cpp'
    for name,marker in [('eval-bool.inc','static std::optional<bool> evalAsBool('),('statistics-process.inc','static auto ProcessValueForStatistics('),('statistics-cache.inc','static const StatisticsResult& GetOrCreateStatistics('),('average.inc','static auto AverageFunc('),('sum.inc','static auto SumFunc('),('maximum.inc','static auto MaximumFunc('),('minimum.inc','static auto MinimumFunc('),('string.inc','std::string ToStringValue(')]:extract(cpp,name,marker,marker.startswith('static auto ProcessValue'))
    for name in ['ExprNode','BoolLiteral','NoneLiteral','DoubleLiteral','IntLiteral','StringLiteral','DateTimeLiteral','ArrayLiteral']:extract(h,name+'.inc','struct '+name+('\n' if name=='ExprNode' else ' : public ExprNode'),True)
    for name,marker in [('context.inc','struct FilterEvalContext\n'),('statistics-result.inc','struct StatisticsResult\n'),('range-info.inc','struct RangeInfo\n'),('shared-context.inc','struct FilterSharedContext\n')]:extract(fh,name,marker,True)
    provider='Src/FilterEngine/ILineDataProvider.h';write(out/'provider-interface.inc',data[provider]);ext.append(dict(file='provider-interface.inc',path=provider,startByte=0,endByte=len(data[provider]),startLine=1,endLine=data[provider].count(b'\n')+1,sha256=sha(data[provider]),wholeFile=True,outerLf=False))
    extract(ft,'vector-provider.inc','class VectorLineDataProvider:',True);extract(ft,'apply-caller.inc','static bool ApplyFilterExpressionsToLines(')
    extract(fc,'convert-string.inc','static std::string ConvertString(');extract(fc,'transform-line.inc','std::string FilterExpression::TransformLine(')
    extract('Src/FilterEngine/FilterError.h','filter-error-code.inc','enum FilterErrorCode\n',True)
    ah='Externals/poco/Foundation/include/Poco/Ascii.h';sh='Externals/poco/Foundation/include/Poco/String.h';th='Externals/poco/Foundation/include/Poco/Timestamp.h';tc='Externals/poco/Foundation/src/Timestamp.cpp'
    extract(ah,'ascii-class.inc','class Foundation_API Ascii',True)
    for name,marker in [('ascii-properties.inc','inline int Ascii::properties('),('ascii-isascii.inc','inline bool Ascii::isAscii('),('ascii-hasproperties.inc','inline bool Ascii::hasProperties('),('ascii-isupper.inc','inline bool Ascii::isUpper('),('ascii-lower.inc','inline int Ascii::toLower(')]:extract(ah,name,marker)
    extract('Externals/poco/Foundation/src/Ascii.cpp','ascii-table.inc','const int Ascii::CHARACTER_PROPERTIES[128]',True);extract(sh,'icompare.inc','template <class S>\nint icompare(const S& str1, const S& str2)')
    extract(th,'timestamp-class.inc','class Foundation_API Timestamp',True)
    for name,marker in [('eq','inline bool Timestamp::operator == '),('ne','inline bool Timestamp::operator != '),('gt','inline bool Timestamp::operator >  '),('ge','inline bool Timestamp::operator >= '),('lt','inline bool Timestamp::operator <  '),('le','inline bool Timestamp::operator <= ')]:extract(th,'timestamp-op'+name+'.inc',marker)
    extract(th,'timestamp-epoch.inc','inline Timestamp::TimeVal Timestamp::epochMicroseconds(')
    for name,marker in [('default','Timestamp::Timestamp()'),('value','Timestamp::Timestamp(TimeVal tv)'),('copy','Timestamp::Timestamp(const Timestamp& other)'),('dtor','Timestamp::~Timestamp()'),('assign','Timestamp& Timestamp::operator = (const Timestamp& other)'),('update','void Timestamp::update()')]:extract(tc,'timestamp-'+name+'.inc',marker)
    for name,marker in [('extract-ranges.inc','template <typename Predicate>\nstd::vector<std::pair<int, int>> ExtractRanges('),('distances.inc','static std::pair<int, int> GetDistancesToRanges('),('match-ranges.inc','static const auto& GetOrCreateMatchRanges('),('matchcontext.inc','static auto LineMatchContextFunc('),('matchblocknumber.inc','static auto MatchBlockNumberFunc('),('matchblockcount.inc','static auto MatchBlockCountFunc('),('matchblocksize.inc','static auto MatchBlockSizeFunc('),('matchblockoffset.inc','static auto MatchBlockOffsetFunc(')]:extract(cpp,name,marker)
    # Registration and full optimizer are immutable source-only boundaries, not an execution claim.
    raw=data[cpp]
    for fn in ['Average','Sum','Maximum','Minimum']:
        marker='{"'+fn.lower()+'", '+fn+'Func, 1, 2}';start=raw.index(marker.encode());end=start+len(marker.encode());ext.append(dict(file=None,path=cpp,kind='registration',startByte=start,endByte=end,startLine=raw[:start].count(b'\n')+1,endLine=raw[:end].count(b'\n')+1,sha256=sha(raw[start:end])))
    extract(cpp,None,'ExprNode* FunctionNode::Optimize()')
    write(out/'provenance.json',json.dumps(dict(commit=COMMIT,reusedArchiveSha256=ZIP_SHA,sources=fixed['sources'],extractions=ext,blockReader='same brace/string/comment scanner as frozen Within block31; original spans unmodified, one explicit outer LF only'),indent=2)+'\n')
    cases=json.loads((BASE/'cases.json').read_bytes());lines=[]
    for c in cases:
        lines.append('scan('+json.dumps(c['id'])+','+c['nativeFunction']+',{' + ','.join(map(datum,c['rows']))+'},'+datum(c['before'])+','+datum(c['after'])+','+json.dumps(c['mode'])+');')
    write(out/'cases.inc','\n'.join(lines)+'\n');write(out/'adapter.cpp',(BASE/'adapter.cpp').read_bytes())
    compiler=a.compiler.resolve();vcvars=a.vcvars.resolve();assert compiler.is_file() and vcvars.is_file()
    rsp=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/Fe:'+str(out/'reference.exe'),'/Fo:'+str(out/'adapter.obj'),str(out/'adapter.cpp')]
    write(out/'compile.rsp','\n'.join('"'+x+'"' for x in rsp));write(out/'compile.cmd','@echo off\ncall "'+str(vcvars)+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+str(compiler)+'" @compile.rsp\nexit /b %errorlevel%\n')
    env=dict(os.environ,TEMP=str(out/'compiler-temp'),TMP=str(out/'compiler-temp'))
    started=datetime.datetime.now(datetime.timezone.utc).isoformat();cp=subprocess.run(['cmd','/c',str(out/'compile.cmd')],cwd=out,capture_output=True,timeout=120,env=env)
    write(out/'compile.stdout',cp.stdout);write(out/'compile.stderr',cp.stderr);write(out/'compile-receipt.json',json.dumps(dict(exitCode=cp.returncode,timeout=False,stdoutEof=True,stderrEof=True,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),compilerSha256=sha(compiler.read_bytes()),inputSha256={p.name:sha(p.read_bytes()) for p in sorted(out.glob('*.inc'))+sorted(out.glob('*.cpp'))+sorted(out.glob('*.rsp'))},TEMP=env['TEMP'],TMP=env['TMP']),indent=2)+'\n')
    if cp.returncode:raise RuntimeError('compile failed; raw compile.stdout/compile.stderr retained in '+str(out))
    command=[str(out/'reference.exe')];started=datetime.datetime.now(datetime.timezone.utc).isoformat();proc=subprocess.Popen(command,cwd=out,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=env)
    class FT(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
    times=[FT() for _ in range(4)];handle=ctypes.c_void_p(int(proc._handle));assert ctypes.windll.kernel32.GetProcessTimes(handle,*[ctypes.byref(t) for t in times]);birth=(times[0].high<<32)|times[0].low
    timed=False
    try:stdout,stderr=proc.communicate(timeout=30)
    except subprocess.TimeoutExpired:timed=True;proc.kill();stdout,stderr=proc.communicate(timeout=10)
    signaled=ctypes.windll.kernel32.WaitForSingleObject(handle,0)==0
    write(out/'golden.ndjson',stdout);write(out/'reference.stderr',stderr)
    receipt=dict(initialObservation=a.initial_observation,sourceCommit=COMMIT,command=command,pid=proc.pid,birthFileTime=birth,heldProcessHandleSignaled=signaled,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=proc.returncode,timeout=timed,stdoutEof=True,stderrEof=True,stdoutSha256=sha(stdout),stderrSha256=sha(stderr),caseCount=len(cases),inputSha256=sha((BASE/'cases.json').read_bytes()),adapterSha256=sha((BASE/'adapter.cpp').read_bytes()),compilerPath=str(compiler),compilerSha256=sha(compiler.read_bytes()),executableSha256=sha((out/'reference.exe').read_bytes()))
    write(out/'receipt.json',json.dumps(receipt,indent=2)+'\n');assert not timed and not stderr and signaled and proc.returncode==0
    results=[json.loads(l) for l in stdout.splitlines()];assert [r['id'] for r in results]==[c['id'] for c in cases]
    if (BASE/'golden.ndjson').exists() and not a.initial_observation:assert stdout==(BASE/'golden.ndjson').read_bytes()
    print(json.dumps(receipt))
if __name__=='__main__':main()
