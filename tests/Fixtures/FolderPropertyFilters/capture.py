"""固定commitの無変更spanを抽出し、外部fresh TempだけでMSVC採取する。"""
import sys
sys.dont_write_bytecode = True
import argparse, ctypes, datetime, hashlib, json, os, pathlib, subprocess, zipfile
BASE = pathlib.Path(__file__).resolve().parent
COMMIT = '6780924ba35f7f55af0f6f14dd302561175830a6'
SOURCES = ['Src/FilterEngine/FunctionNodeProperty.cpp', 'Src/FilterEngine/FilterExpressionNodes.cpp',
           'Src/FilterEngine/FilterExpressionNodes.h', 'Src/FilterEngine/FilterExpression.cpp',
           'Src/PropertySystem.cpp', 'Src/PropertySystem.h', 'Src/HashCalc.cpp', 'Src/HashCalc.h',
           'Externals/poco/Foundation/src/Timestamp.cpp', 'Externals/poco/Foundation/include/Poco/Timestamp.h',
           'LICENSE.md', 'Externals/poco/LICENSE']
def sha(b): return hashlib.sha256(b).hexdigest()
def write(p, b):
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('xb') as f: f.write(b if isinstance(b, bytes) else b.encode('utf8'))
def js(p, value): write(p, json.dumps(value, ensure_ascii=False, indent=2)+'\n')
def validate(p):
    p = p.absolute()
    for x in [p, *p.parents]:
        assert not x.is_symlink() and not x.is_junction(), str(x)
        if x.exists(): assert not (getattr(x.stat(), 'st_file_attributes', 0) & 0x400), str(x)
    p = p.resolve()
    for root in [BASE.parents[2], pathlib.Path.home()/'.codex'] + ([pathlib.Path(os.environ['CODEX_HOME'])] if os.environ.get('CODEX_HOME') else []):
        assert not p.is_relative_to(root.resolve()), str(p)
    assert p.is_relative_to(pathlib.Path(os.environ['TEMP']).resolve()), str(p)
    return p
def block(b, marker):
    a = b.index(marker.encode()); i = b.index(b'{', a); level = 0; state = 'code'
    while i < len(b):
        c = b[i:i+1]; n = b[i:i+2]
        if state == 'code':
            if n == b'//': state = 'line'; i += 1
            elif n == b'/*': state = 'comment'; i += 1
            elif c == b'"': state = 'string'
            elif c == b"'": state = 'char'
            elif c == b'{': level += 1
            elif c == b'}':
                level -= 1
                if level == 0: return a, i+1
        elif state == 'line':
            if c == b'\n': state = 'code'
        elif state == 'comment':
            if n == b'*/': state = 'code'; i += 1
        elif c == b'\\': i += 1
        elif (state == 'string' and c == b'"') or (state == 'char' and c == b"'"): state = 'code'
        i += 1
    raise ValueError(marker)
def spans(data):
    results = []
    def add(output, path, marker=None, bounds=None):
        b = data[path]; start, end = bounds if bounds else block(b, marker)
        results.append(dict(output=output, path=path, startByte=start, endByte=end,
                            startLine=b[:start].count(b'\n')+1, endLine=b[:end].count(b'\n')+1,
                            sha256=sha(b[start:end])))
    p = 'Src/FilterEngine/FunctionNodeProperty.cpp'
    for o, m in [('conversion.inc','static ValueType ConvertPROPVARIANTToValueType('), ('prop.inc','static auto prop('),
                 ('propary.inc','static auto propary('), ('set.inc','void FunctionNode::SetPropFunc(')]: add(o,p,m)
    p = 'Src/FilterEngine/FilterExpressionNodes.cpp'
    add('prefix.inc',p,'AttrParseResult ParseAttributeName('); add('bool.inc',p,'static std::optional<bool> evalAsBool(')
    add('contains.inc','Src/FilterEngine/FilterExpression.cpp','static bool ContainsTrue(')
    p='Src/PropertySystem.h'; b=data[p]; add('property-header.inc',p,bounds=(b.index(b'class PropertyValues'),len(b)))
    p='Src/HashCalc.cpp'; b=data[p]; add('hash.inc',p,bounds=(b.index(b'static NTSTATUS CalculateHashValue('), b.rindex(b'#endif')))
    p='Src/PropertySystem.cpp'; b=data[p]
    add('property-system.inc',p,bounds=(b.index(b'static const PROPERTYKEY'), b.index(b'PropertyValues::PropertyValues()')))
    a=b.index(b'PropertyValues::PropertyValues()');add('property-system.inc',p,bounds=(a,b.index(b';',a)+1))
    for m in ['PropertyValues::~PropertyValues()', 'PropertySystem::PropertySystem(const std::vector<String>& canonicalNames)',
              'bool PropertySystem::AddProperty(', 'void PropertySystem::AddProperties(', 'bool PropertySystem::GetPropertyValues(', 'int PropertySystem::GetPropertyIndex(']: add('property-system.inc',p,m)
    p='Externals/poco/Foundation/src/Timestamp.cpp'
    for m in ['Timestamp::Timestamp(TimeVal tv)','Timestamp::Timestamp(const Timestamp& other)','Timestamp::~Timestamp()', 'Timestamp Timestamp::fromFileTimeNP(']: add('timestamp.inc',p,m)
    return results
def load():
    manifest=json.loads((BASE/'source-manifest.json').read_bytes()); data={}
    assert manifest['commit']==COMMIT
    with zipfile.ZipFile(BASE/'reference-source.zip') as z:
        assert set(z.namelist())=={'original/'+p for p in SOURCES}
        for item in manifest['sources']:
            b=z.read('original/'+item['path']);assert sha(b)==item['sha256'] and len(b)==item['size']
            assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==item['blob']
            data[item['path']]=b
    assert spans(data)==json.loads((BASE/'provenance.json').read_bytes())
    if (BASE/'input.zip').exists():
        with zipfile.ZipFile(BASE/'input.zip') as z:
            assert set(z.namelist())==set(inputs())
            for name,b in inputs().items(): assert z.read(name)==b
    return data
def inputs():
    return {'empty.bin':b'', 'unicode-日本語.bin':b'DiffBeacon\x00'+bytes(range(256)),
            'boundary.bin':bytes(range(256))*256+b'boundary\x00', 'cache.bin':b'abc'}
def initialize():
    repo=BASE.parents[2]; data={}; manifest=[]
    for p in SOURCES:
        b=subprocess.run(['git','show',COMMIT+':'+p],cwd=repo,check=True,capture_output=True).stdout
        blob=subprocess.run(['git','rev-parse',COMMIT+':'+p],cwd=repo,check=True,capture_output=True,text=True).stdout.strip()
        assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==blob
        data[p]=b;manifest.append(dict(path=p,size=len(b),sha256=sha(b),blob=blob))
    with zipfile.ZipFile(BASE/'reference-source.zip','x',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
        for p,b in data.items():
            zi=zipfile.ZipInfo('original/'+p,(2026,10,17,0,0,0));zi.compress_type=zipfile.ZIP_DEFLATED;z.writestr(zi,b)
    js(BASE/'source-manifest.json',dict(commit=COMMIT,sources=manifest));js(BASE/'provenance.json',spans(data))
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--initialize-source',action='store_true');ap.add_argument('--output',type=pathlib.Path);ap.add_argument('--compiler',type=pathlib.Path);ap.add_argument('--vcvars',type=pathlib.Path);a=ap.parse_args()
    if a.initialize_source: initialize();return
    assert a.output and a.compiler and a.vcvars
    out=validate(a.output);assert not out.exists(), 'fresh-only '+str(out);out.mkdir(parents=True);(out/'compiler-temp').mkdir()
    data=load(); extracted={}
    for s in spans(data): extracted.setdefault(s['output'],[]).append(data[s['path']][s['startByte']:s['endByte']]+b'\n')
    for name, pieces in extracted.items(): write(out/name,b''.join(pieces))
    write(out/'adapter.cpp',(BASE/'adapter.cpp').read_bytes())
    for name,b in inputs().items():
        write(out/name,b);os.utime(out/name,ns=(1700000000123456700,1700000000123456700))
    compiler=a.compiler.resolve(); vcvars=a.vcvars.resolve();assert compiler.is_file() and vcvars.is_file()
    args=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/D_WIN64','/Fe:reference.exe','/Fo:adapter.obj','adapter.cpp','/link','propsys.lib','ole32.lib','shell32.lib','bcrypt.lib','uuid.lib']
    write(out/'compile.rsp','\n'.join('"'+x+'"' for x in args))
    write(out/'compile.cmd','@echo off\ncall "'+str(vcvars)+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+str(compiler)+'" @compile.rsp\nexit /b %errorlevel%\n')
    env=dict(os.environ,TEMP=str(out/'compiler-temp'),TMP=str(out/'compiler-temp'))
    started=datetime.datetime.now(datetime.timezone.utc).isoformat()
    cp=subprocess.run(['cmd.exe','/d','/c',str(out/'compile.cmd')],cwd=out,env=env,capture_output=True,timeout=180)
    write(out/'compile.stdout',cp.stdout);write(out/'compile.stderr',cp.stderr)
    js(out/'compile-receipt.json',dict(exitCode=cp.returncode,compiler=str(compiler),compilerSha256=sha(compiler.read_bytes()),startedUtc=started,inputSha256={p.name:sha(p.read_bytes()) for p in out.iterdir() if p.suffix in ['.cpp','.inc','.rsp']},temp=env['TEMP']))
    assert cp.returncode==0,cp.stdout.decode(errors='replace')[-7000:]
    proc=subprocess.Popen([str(out/'reference.exe')],cwd=out,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    handle=ctypes.c_void_p(int(proc._handle))
    class FT(ctypes.Structure): _fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
    t=[FT() for _ in range(4)];assert ctypes.windll.kernel32.GetProcessTimes(handle,*[ctypes.byref(x) for x in t])
    try: stdout,stderr=proc.communicate(timeout=60)
    except subprocess.TimeoutExpired:
        proc.kill();stdout,stderr=proc.communicate(timeout=10)
        write(out/'timeout.stdout',stdout);write(out/'timeout.stderr',stderr)
        raise
    write(out/'golden.ndjson',stdout);write(out/'reference.stderr',stderr)
    signaled=ctypes.windll.kernel32.WaitForSingleObject(handle,0)==0
    receipt=dict(exitCode=proc.returncode,pid=proc.pid,birthFileTime=(t[0].high<<32)|t[0].low,heldProcessHandleSignaled=signaled,stdoutEof=True,stderrEof=True,stdoutSha256=sha(stdout),executableSha256=sha((out/'reference.exe').read_bytes()),adapterSha256=sha((BASE/'adapter.cpp').read_bytes()),caseCount=len(stdout.splitlines()),endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    js(out/'receipt.json',receipt);assert proc.returncode==0 and not stderr and signaled
    if (BASE/'golden.ndjson').exists(): assert stdout==(BASE/'golden.ndjson').read_bytes()
    print(json.dumps(receipt))
if __name__=='__main__': main()
