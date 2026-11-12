"""Fresh direct MSVC capture; never synthesizes numeric expected values."""
import sys
sys.dont_write_bytecode=True
import argparse,pathlib,os,json,hashlib,subprocess,ctypes,datetime,locale
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def put(p,b):
 assert not p.exists(),str(p);p.write_bytes(b if isinstance(b,bytes) else b.encode('utf-8'))
def spawn(command,out,name,env):
 p=subprocess.Popen(command,cwd=out,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 k=ctypes.WinDLL('kernel32',use_last_error=True)
 k.GetProcessTimes.argtypes=[ctypes.c_void_p]*5;k.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_ulong]
 k.GetExitCodeProcess.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_ulong)]
 handle=ctypes.c_void_p(int(p._handle));times=[ctypes.c_ulonglong() for _ in range(4)]
 assert k.GetProcessTimes(handle,*[ctypes.byref(t) for t in times]);started=datetime.datetime.now(datetime.timezone.utc).isoformat()
 try:stdout,stderr=p.communicate(timeout=60)
 except subprocess.TimeoutExpired:
  put(out/(name+'-timeout.json'),json.dumps({'pid':p.pid,'birth100ns':times[0].value,'timeout':True,'policy':'no kill or cleanup; parent decision required'}));raise
 code=ctypes.c_ulong();assert k.GetExitCodeProcess(handle,ctypes.byref(code))
 receipt={'command':command,'pid':p.pid,'birth100ns':times[0].value,'heldOsHandle':int(p._handle),'safeHandleType':type(p._handle).__name__,'heldSignaled':k.WaitForSingleObject(handle,0)==0,'exitCode':p.returncode,'osExitCode':code.value,'timeout':False,'stdoutEof':True,'stderrEof':True,'startedUtc':started,'endedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'stdoutSha256':sha(stdout),'stderrSha256':sha(stderr),'executableSha256':sha(pathlib.Path(command[0]).read_bytes()) if pathlib.Path(command[0]).is_file() else None}
 put(out/(name+'.stdout.raw'),stdout);put(out/(name+'.stderr.raw'),stderr);put(out/(name+'-receipt.json'),json.dumps(receipt,indent=2)+'\n')
 assert p.returncode==0 and code.value==0 and receipt['heldSignaled'];return stdout,stderr
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--compiler',type=pathlib.Path,required=True);ap.add_argument('--vcvars',type=pathlib.Path,required=True);a=ap.parse_args();out=a.output.resolve()
 for p in (out,*out.parents):
  assert not p.name.startswith('.') and p.name.lower()!='artifacts' and not p.is_symlink() and not p.is_junction()
  if p.exists():assert not p.stat().st_file_attributes&1024
 for forbidden in [pathlib.Path(r'C:\Users\IMT\dev\DiffBeacon'),pathlib.Path.home()/'.codex',BASE]:assert not out.is_relative_to(forbidden.resolve())
 if os.environ.get('CODEX_HOME'):assert not out.is_relative_to(pathlib.Path(os.environ['CODEX_HOME']).resolve())
 assert out.is_relative_to(pathlib.Path(os.environ['TEMP']).resolve()),'user Temp required'
 assert not out.exists();out.mkdir();(out/'compiler-temp').mkdir()
 for name in ['expr-node.inc','version-node.inc','version-ctor.inc','adapter.cpp','cases.inc']:put(out/name,(BASE/name).read_bytes())
 put(out/'compiler-env.cmd','@echo off\r\ncall "'+str(a.vcvars)+'" >nul\r\nif errorlevel 1 exit /b %errorlevel%\r\nset INCLUDE\r\nset LIB\r\nset PATH\r\n')
 env=dict(os.environ,TEMP=str(out/'compiler-temp'),TMP=str(out/'compiler-temp'))
 setup,error=spawn([os.environ['COMSPEC'],'/c',str(out/'compiler-env.cmd')],out,'compiler-env',env);assert not error
 for line in setup.decode(locale.getencoding()).splitlines():
  key,sep,value=line.partition('=')
  if sep and key.upper() in ('INCLUDE','LIB','PATH'):env[key]=value
 rsp=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/Fe:'+str(out/'reference.exe'),'/Fo:'+str(out/'adapter.obj'),str(out/'adapter.cpp')]
 put(out/'compile.rsp','\n'.join('"'+x+'"' for x in rsp))
 spawn([str(a.compiler),'@'+str(out/'compile.rsp')],out,'compile',env)
 stdout,stderr=spawn([str(out/'reference.exe')],out,'native',env);assert not stderr
 rows=[json.loads(x) for x in stdout.splitlines()];assert [r['id'] for r in rows]==[c['id'] for c in json.loads((BASE/'cases.json').read_bytes())]
 print(json.dumps({'output':str(out),'cases':len(rows),'stdoutSha256':sha(stdout)}))
if __name__=='__main__':main()
