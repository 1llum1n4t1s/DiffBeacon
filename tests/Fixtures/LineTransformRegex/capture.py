import pathlib, subprocess, json, hashlib, os, re, ctypes, datetime, argparse, zipfile
REPO=pathlib.Path(subprocess.check_output(['git','rev-parse','--show-toplevel'],cwd=pathlib.Path(__file__).resolve().parent).decode().strip())
COMPILER='cl'
VCVARS=None
BOOTSTRAP=False
COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
BASE=pathlib.Path(__file__).resolve().parent
def sha(b): return hashlib.sha256(b).hexdigest()
def write(p,b):
    p.parent.mkdir(parents=True,exist_ok=True)
    if p.exists(): raise RuntimeError('refuse overwrite '+str(p))
    p.write_bytes(b if isinstance(b,bytes) else b.encode('utf-8'))
def validate(p):
    p=p.absolute()
    for a in [p,*p.parents]:
        assert a.name.lower()!='artifacts'
        assert not a.is_symlink() and not a.is_junction()
    assert not p.resolve().is_relative_to(REPO.resolve()) and not p.resolve().is_relative_to(pathlib.Path.home()/'.codex')
    if os.environ.get('CODEX_HOME'):
        assert not p.resolve().is_relative_to(pathlib.Path(os.environ['CODEX_HOME']).resolve())
    assert p.resolve().is_relative_to(pathlib.Path(os.environ['TEMP']).resolve()), 'use an external Temp task directory'
    return p
def block(data,marker,start=None):
    a=data.index(marker.encode()) if start is None else start
    brace=data.index(b'{',a); level=0; i=brace; state='code'
    while i<len(data):
        c=data[i:i+1]; n=data[i:i+2]
        if state=='code':
            if n==b'//':state='line';i+=1
            elif n==b'/*':state='comment';i+=1
            elif c==b'"':state='string'
            elif c==b"'":state='char'
            elif c==b'{':level+=1
            elif c==b'}':
                level-=1
                if level==0:return a,i+1,data[a:i+1]
        elif state=='line':
            if c==b'\n':state='code'
        elif state=='comment':
            if n==b'*/':state='code';i+=1
        elif c==b'\\':i+=1
        elif (state=='string' and c==b'"') or (state=='char' and c==b"'"):state='code'
        i+=1
    raise RuntimeError(marker)
def lit(s):return 'bytes({'+','.join(str(x) for x in s.encode('utf-8'))+'})'
def node(n):
    t=n['type']; v=n.get('value')
    if t=='string':return 'new StringLiteral('+lit(v)+')'
    if t=='raw-string':return 'new StringLiteral(bytes({'+','.join(str(x) for x in v)+'}))'
    if t=='lazy-regex':return 'new LazyRegex('+lit(v)+')'
    if t=='integer':return 'new IntLiteral(int64_t('+str(v)+'))'
    if t=='double':return 'new DoubleLiteral('+str(v)+')'
    if t=='bool':return 'new BoolLiteral('+str(v).lower()+')'
    if t=='none':return 'new NoneLiteral()'
    if t=='throw':return 'new ThrowNode('+lit(v)+')'
    if t=='regex':return 'new RegularExpressionLiteral('+lit(v)+','+str(n.get('caseSensitive',True)).lower()+')'
    if t=='array':return 'array({'+','.join(node(x) for x in v)+'})'
    if t=='dynamic':return 'new DynamicNode('+node(v)+')'
    raise ValueError(t)
def build(output):
    if os.name != 'nt': raise RuntimeError('Native capture currently verified only on Windows/MSVC; macOS compiler capture is unverified')
    output=validate(pathlib.Path(output));assert not output.exists(), 'output must be fresh';output.mkdir(parents=True)
    sources=[]; paths=['Src/FilterEngine/FilterExpressionNodes.cpp','Src/FilterEngine/FilterExpressionNodes.h','Externals/poco/Foundation/src/RegularExpression.cpp','Externals/poco/Foundation/include/Poco/RegularExpression.h','LICENSE.md','Externals/poco/LICENSE','Externals/poco/dependencies/pcre2/CMakeLists.txt','Externals/poco/Foundation/include/Poco/Bugcheck.h','Externals/poco/Foundation/src/Bugcheck.cpp','Src/FilterEngine/FilterParser.y','Src/FilterEngine/FilterExpression.cpp']
    pcre_paths=subprocess.check_output(['git','ls-tree','-r','--name-only',COMMIT,'Externals/poco/dependencies/pcre2/src'],cwd=REPO).decode().splitlines()
    paths+=pcre_paths
    data={}
    archive=BASE/'reference-source.zip'
    if archive.exists():
        with zipfile.ZipFile(archive) as z:
            data={n[len('original/'):]:z.read(n) for n in z.namelist() if n.startswith('original/')}
        assert set(paths)==set(data), 'fixed archive source set differs'
    elif not BOOTSTRAP:
        raise RuntimeError('missing fixed reference-source.zip')
    for path in paths:
        blob=subprocess.check_output(['git','rev-parse',COMMIT+':'+path],cwd=REPO).decode().strip()
        b=subprocess.check_output(['git','cat-file','blob',blob],cwd=REPO)
        if path in data: assert data[path]==b, path
        data[path]=b
        write(output/'original'/path,b)
        sources.append(dict(path=path,blob=blob,size=len(b),sha256=sha(b)))
    f=data[paths[0]];h=data[paths[1]];p=data[paths[2]];ph=data[paths[3]];ex=[]
    def extract(source,name,marker):
        b=data[source];a,z,part=block(b,marker)
        if marker.startswith('struct ') or marker=='enum Options':
            assert b[z:z+1]==b';', name
            z+=1
            part=b[a:z]
        write(output/name,part+b'\n');ex.append(dict(file=name,path=source,startByte=a,endByte=z,startLine=b[:a].count(b'\n')+1,endLine=b[:z].count(b'\n')+1,sha256=sha(part),bodyUnchanged=True))
    for name,marker in [('string.inc','std::string ToStringValue('),('get.inc','static std::optional<std::string> getAsString('),('escape.inc','static std::string escapeRegex('),('flags.inc','static inline int getRegexFlags('),('create.inc','static std::shared_ptr<Poco::RegularExpression> createRegex('),('find.inc','static auto RegexFindFunc('),('count.inc','static auto RegexCountFunc('),('extract.inc','static auto RegexExtractFunc('),('replace.inc','static auto RegexReplaceFunc('),('subst-string.inc','static bool regexSubst(std::string& str, const std::string& pattern'),('subst-regex.inc','static bool regexSubst(std::string& str, const Poco::RegularExpression& regex'),('optimize-arg.inc','static bool OptimizeRegexArg('),('regex-literal-ctor.inc','RegularExpressionLiteral::RegularExpressionLiteral(')]:extract(paths[0],name,marker)
    extract(paths[0],'lifting.inc','template <typename Func>')
    extract(paths[0],'literal-result.inc','template<typename T>')
    for t in ['ExprNode','BoolLiteral','NoneLiteral','DoubleLiteral','IntLiteral','StringLiteral','DateTimeLiteral','RegularExpressionLiteral','ArrayLiteral']:extract(paths[1],t+'.inc','struct '+t+('\n' if t=='ExprNode' else ' : public ExprNode'))
    extract(paths[3],'options.inc','enum Options')
    extract(paths[3],'poco-subst-inline.inc','inline int RegularExpression::subst(')
    extract(paths[3],'match-type.inc','struct Match')
    extract(paths[2],'match-data.inc','\tclass MatchData')
    for name,marker in [('poco-ctor.inc','RegularExpression::RegularExpression('),('poco-dtor.inc','RegularExpression::~RegularExpression('),('poco-subst.inc','int RegularExpression::subst(std::string& subject, std::string::size_type offset'),('poco-subst-one.inc','std::string::size_type RegularExpression::substOne('),('poco-match.inc','int RegularExpression::match(const std::string& subject, std::string::size_type offset, Match& mtch'),('poco-match-vector.inc','int RegularExpression::match(const std::string& subject, std::string::size_type offset, MatchVec& matches'),('poco-compile-options.inc','int RegularExpression::compileOptions('),('poco-match-options.inc','int RegularExpression::matchOptions(')]:extract(paths[2],name,marker)
    for func in ['regexcount','regexfind','regexextract','regexreplace']:
        extract(paths[0],'optimizer-'+func+'.inc','if (functionName == "'+func+'" && args && args->size() >= '+('3' if func=='regexreplace' else '2')+')')
    # Original selected branches execute with adapter-supplied outer AST/context.
    write(output/'provenance.json',json.dumps(dict(commit=COMMIT,sources=sources,extractions=ex),indent=2)+'\n')
    cases=json.loads((BASE/'cases.json').read_text(encoding='utf-8'))
    lines=[]
    for c in cases:lines.append('run('+json.dumps(c['id'])+','+json.dumps(c['mode'])+','+json.dumps(c['function'])+','+str(c['caseSensitive']).lower()+','+str(c.get('nullArgs',False)).lower()+',{'+','.join(node(n) for n in c['args'])+'});')
    write(output/'cases.inc','\n'.join(lines)+'\n')
    write(output/'adapter.cpp',(BASE/'adapter.cpp').read_bytes())
    cdir=output/'original/Externals/poco/dependencies/pcre2/src'
    # The pinned project provides its actual amalgamation entry, including all components.
    cs=[cdir/'pcre2.c']
    args=['/nologo','/c','/O2','/DHAVE_CONFIG_H','/DPCRE2_STATIC','/I'+str(cdir)]+[str(x) for x in cs]
    write(output/'compile-c.rsp','\n'.join('"'+a+'"' for a in args))
    args=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/DPCRE2_STATIC','/I'+str(cdir),'/Fe:'+str(output/'reference.exe'),str(output/'adapter.cpp')]+[str(output/(x.stem+'.obj')) for x in cs]
    write(output/'compile-cpp.rsp','\n'.join('"'+a+'"' for a in args))
    compiler_path=pathlib.Path(COMPILER)
    if not compiler_path.is_absolute():
        import shutil
        found=shutil.which(COMPILER)
        if not found:raise RuntimeError('Supply --compiler or an initialized MSVC environment')
        compiler_path=pathlib.Path(found)
    if not compiler_path.is_file():raise RuntimeError('Compiler missing')
    def cmdquote(value):
        value=str(value)
        if any(c in value for c in '\r\n"%'):raise ValueError('Unsafe cmd path')
        return '"'+value+'"'
    bat='@echo off\n'
    if VCVARS:
        bat+='call '+cmdquote(VCVARS)+' >nul\nif errorlevel 1 exit /b %errorlevel%\n'
    bat+=cmdquote(compiler_path)+' @compile-c.rsp\nif errorlevel 1 exit /b %errorlevel%\n'+cmdquote(compiler_path)+' @compile-cpp.rsp\nexit /b %errorlevel%\n'
    write(output/'compile.cmd',bat)
    cp=subprocess.run(['cmd','/c',str(output/'compile.cmd')],cwd=output,capture_output=True,timeout=240)
    write(output/'compile.stdout',cp.stdout);write(output/'compile.stderr',cp.stderr)
    if cp.returncode:raise RuntimeError('compile failed '+str(cp.returncode)+' '+cp.stdout.decode(errors='replace')[-7000:])
    command=[str(output/'reference.exe')];started=datetime.datetime.now(datetime.timezone.utc).isoformat()
    proc=subprocess.Popen(command,cwd=output,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    class FT(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
    times=[FT() for _ in range(4)];handle=ctypes.c_void_p(int(proc._handle))
    assert ctypes.windll.kernel32.GetProcessTimes(handle,*[ctypes.byref(x) for x in times])
    birth=(times[0].high<<32)|times[0].low
    timed=False
    try:out,err=proc.communicate(timeout=60)
    except subprocess.TimeoutExpired:
        timed=True;proc.kill();out,err=proc.communicate(timeout=10)
    signaled=ctypes.windll.kernel32.WaitForSingleObject(handle,0)==0
    write(output/'golden.ndjson',out);write(output/'reference.stderr',err)
    compiler=compiler_path
    receipt=dict(command=command,pid=proc.pid,birthFileTime=birth,heldProcessHandleSignaled=signaled,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=proc.returncode,timeout=timed,stdoutEof=True,stderrEof=True,stdoutSha256=sha(out),stderrSha256=sha(err),caseCount=len(cases),sourceCommit=COMMIT,compilerPath=str(compiler),compilerSha256=sha(compiler.read_bytes()),executableSha256=sha((output/'reference.exe').read_bytes()))
    write(output/'receipt.json',json.dumps(receipt,indent=2)+'\n')
    assert proc.returncode==0 and not err and signaled and not timed
    assert [json.loads(x)['id'] for x in out.splitlines()]==[c['id'] for c in cases]
    if not BOOTSTRAP:
        expected=(BASE/'golden.ndjson').read_bytes()
        assert out==expected, 'capture differs from immutable native stdout'
    print(json.dumps(receipt))
if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('--output',required=True);ap.add_argument('--compiler',default='cl');ap.add_argument('--vcvars');ap.add_argument('--bootstrap',action='store_true',help=argparse.SUPPRESS);ap.add_argument('--repo',type=pathlib.Path,default=REPO)
    args=ap.parse_args();REPO=args.repo.resolve();COMPILER=args.compiler;VCVARS=args.vcvars;BOOTSTRAP=args.bootstrap
    build(args.output)
