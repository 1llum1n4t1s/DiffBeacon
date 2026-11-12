"""Fresh external capture of unchanged old SizeLiteral/Negate/Array/statistics/serializer blocks."""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,subprocess,zipfile,ctypes,os,datetime,argparse
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def put(p,b):
    if p.exists():raise RuntimeError('fresh-only '+str(p))
    p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b if isinstance(b,bytes) else b.encode('utf-8'))
def spawn(command,out,name,timeout,env):
    start=datetime.datetime.now(datetime.timezone.utc).isoformat();p=subprocess.Popen(command,cwd=out,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    k=ctypes.WinDLL('kernel32',use_last_error=True);k.GetProcessTimes.argtypes=[ctypes.c_void_p]+[ctypes.c_void_p]*4;k.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_ulong]
    handle=ctypes.c_void_p(int(p._handle));times=[ctypes.c_ulonglong() for _ in range(4)];assert k.GetProcessTimes(handle,*[ctypes.byref(t) for t in times])
    timed=False
    try:stdout,stderr=p.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:timed=True;p.kill();stdout,stderr=p.communicate(timeout=10)
    signaled=k.WaitForSingleObject(handle,0)==0
    put(out/(name+'.stdout.raw'),stdout);put(out/(name+'.stderr.raw'),stderr)
    receipt={'command':command,'pid':p.pid,'birth100ns':times[0].value,'heldOsHandle':int(p._handle),'safeHandleType':type(p._handle).__name__,'heldSignaled':signaled,'exitCode':p.returncode,'timeout':timed,'stdoutEof':True,'stderrEof':True,'startedUtc':start,'endedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stdoutSha256':sha(stdout),'stderrSha256':sha(stderr)}
    put(out/(name+'-receipt.json'),json.dumps(receipt,indent=2)+'\n')
    assert p.returncode==0 and not timed and signaled,(command,out)
    return stdout,stderr,receipt
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--reference-zip',type=pathlib.Path,required=True);ap.add_argument('--compiler',type=pathlib.Path,required=True);ap.add_argument('--vcvars',type=pathlib.Path,required=True);a=ap.parse_args()
    out=a.output.absolute()
    for p in (out,*out.parents):
        assert not p.name.startswith('.') and p.name.lower()!='artifacts' and not p.is_symlink() and not p.is_junction()
        if p.exists():assert not p.stat().st_file_attributes&1024
    for forbidden in (BASE,pathlib.Path(r'C:\Users\IMT\dev\DiffBeacon'),pathlib.Path.home()/'.codex'):
        assert not out.is_relative_to(forbidden)
    if os.environ.get('CODEX_HOME'):assert not out.is_relative_to(pathlib.Path(os.environ['CODEX_HOME']))
    assert not out.exists();out.mkdir(parents=True);(out/'compiler-temp').mkdir()
    manifest=json.loads((BASE/'source-manifest.json').read_bytes());assert sha(a.reference_zip.read_bytes())==manifest['referenceArchiveSha256']
    sources={}
    with zipfile.ZipFile(a.reference_zip) as z:
        assert z.testzip() is None
        for s in manifest['sources']:
            raw=z.read('original/'+s['path']) if s['origin']=='sibling-fixed-reference-zip' else (BASE/'additional-original'/s['path']).read_bytes()
            assert len(raw)==s['size'] and sha(raw)==s['sha256'] and hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()==s.get('blob',s.get('gitBlob'))
            sources[s['path']]=raw
    for e in json.loads((BASE/'extractions.json').read_bytes()):
        if not e['file']:continue
        b=sources[e['path']][e['startByte']:e['endByte']];assert sha(b)==e['sha256']
        put(out/e['file'],b+(b'\n' if e.get('outerLf') else b''))
    for name in ('adapter.cpp','cases.inc'):put(out/name,(BASE/name).read_bytes())
    rsp=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/Fe:'+str(out/'reference.exe'),'/Fo:'+str(out/'adapter.obj'),str(out/'adapter.cpp')]
    put(out/'compile.rsp','\n'.join('"'+x+'"' for x in rsp))
    put(out/'compile.cmd','@echo off\ncall "'+str(a.vcvars)+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+str(a.compiler)+'" @compile.rsp\nexit /b %errorlevel%\n')
    env=dict(os.environ,TEMP=str(out/'compiler-temp'),TMP=str(out/'compiler-temp'))
    _,err,compile_receipt=spawn(['cmd','/c',str(out/'compile.cmd')],out,'compile',120,env)
    stdout,stderr,r=spawn([str(out/'reference.exe')],out,'native',30,env)
    assert not stderr
    put(out/'golden.ndjson',stdout)
    inputs={p.name:sha(p.read_bytes()) for p in sorted(out.iterdir()) if p.suffix in ('.inc','.cpp','.rsp','.cmd')}
    proof={'sourceCommit':manifest['commit'],'native':r,'compile':compile_receipt,'inputSha256':inputs,'casePlanSha256':sha((BASE/'cases.json').read_bytes()),'adapterSha256':sha((BASE/'adapter.cpp').read_bytes()),'compilerSha256':sha(a.compiler.read_bytes()),'vcvarsSha256':sha(a.vcvars.read_bytes()),'executableSha256':sha((out/'reference.exe').read_bytes()),'TEMP':env['TEMP'],'TMP':env['TMP'],'sourceBlobs':len(sources),'scope':'direct native node boundaries; real parser and directive not executed'}
    put(out/'proof.json',json.dumps(proof,indent=2)+'\n')
    rows=[json.loads(l) for l in stdout.splitlines()];assert [r['id'] for r in rows]==[c['id'] for c in json.loads((BASE/'cases.json').read_bytes())]
    print(json.dumps({'cases':len(rows),'nativePid':r['pid'],'birth':r['birth100ns'],'safeHandleType':r['safeHandleType'],'heldSignaled':r['heldSignaled'],'compilePid':compile_receipt['pid'],'stdoutSha256':sha(stdout)}))
if __name__=='__main__':main()
