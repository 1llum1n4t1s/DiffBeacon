"""無改変原典spanを外部fresh directoryでMSVC compile/run。"""
import argparse,ctypes,datetime,hashlib,json,os,pathlib,subprocess,sys
sys.dont_write_bytecode=True
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def block(raw,marker):
 start=raw.index(marker.encode());i=raw.index(b'{',start);depth=0;state='code'
 while i<len(raw):
  c=raw[i:i+1];n=raw[i:i+2]
  if state=='code':
   if n==b'//':state='line';i+=1
   elif n==b'/*':state='comment';i+=1
   elif c==b'"':state='string'
   elif c==b"'":state='char'
   elif c==b'{':depth+=1
   elif c==b'}':
    depth-=1
    if depth==0:return start,i+1
  elif state=='line':
   if c==b'\n':state='code'
  elif state=='comment':
   if n==b'*/':state='code';i+=1
  elif c==b'\\':i+=1
  elif (state=='string' and c==b'"')or(state=='char' and c==b"'"):state='code'
  i+=1
 raise ValueError(marker)
def cppstr(s):return 'bytes({'+','.join(map(str,s.encode()))+'})'
def value(v):
 if v is None:return 'ValueType(std::monostate{})'
 if isinstance(v,bool):return 'ValueType('+str(v).lower()+')'
 if isinstance(v,int):return 'ValueType(int64_t('+str(v)+'LL))'
 if isinstance(v,float):return 'ValueType('+repr(v)+')'
 if isinstance(v,str):return 'ValueType('+cppstr(v)+')'
 return 'array({'+','.join(map(value,v))+'})'
def node(n):
 if n['op']=='value':return 'literal('+value(n['value'])+')'
 return 'node("'+n['op']+'",'+str(n.get('level',0))+',children('+','.join(map(node,n.get('args',[])))+'))'
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--compiler',type=pathlib.Path,required=True);ap.add_argument('--vcvars',type=pathlib.Path,required=True);a=ap.parse_args()
 out=a.output.absolute();repo=BASE.parents[2].resolve()
 for p in [out,*out.parents]:
  assert not p.is_symlink() and not(p.exists() and getattr(p.stat(),'st_file_attributes',0)&0x400),str(p)
 assert not out.resolve().is_relative_to(repo) and not out.resolve().is_relative_to(pathlib.Path.home()/'.codex')
 if os.environ.get('CODEX_HOME'):assert not out.resolve().is_relative_to(pathlib.Path(os.environ['CODEX_HOME']).resolve())
 assert not out.exists();out.mkdir(parents=True);(out/'compiler-temp').mkdir()
 def write(name,b):(out/name).write_bytes(b if isinstance(b,bytes)else b.encode())
 manifest=json.loads((BASE/'source-manifest.json').read_bytes());data={}
 for s in manifest['sources']:
  raw=(BASE/'original'/s['path']).read_bytes();assert len(raw)==s['size']and sha(raw)==s['sha256'];assert hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()==s['gitBlob'];data[pathlib.Path(s['path']).name]=raw
 spans=[]
 specs=[('string','FilterExpressionNodes.cpp','std::string ToStringValue('),('eval-bool','FilterExpressionNodes.cpp','static std::optional<bool> evalAsBool('),('log','FilterExpressionNodes.cpp','static auto LogFunc('),('error','FilterExpressionNodes.cpp','static auto LogErrorFunc('),('warn','FilterExpressionNodes.cpp','static auto LogWarnFunc('),('info','FilterExpressionNodes.cpp','static auto LogInfoFunc('),('if','FilterExpressionNodes.cpp','static auto IfFunc('),('directive','FilterExpression.cpp','bool FilterExpression::ParseDirective('),('directives','FilterExpression.cpp','bool FilterExpression::ParseAllDirectives('),('errors','FilterError.h','enum FilterErrorCode\n')]
 for name,path,marker in specs:
  raw=data[path];start,end=block(raw,marker)
  if name=='errors':assert raw[end:end+1]==b';';end+=1
  write(name+'.inc',raw[start:end]+b'\n');spans.append(dict(file=name+'.inc',source=path,startByte=start,endByte=end,startLine=raw[:start].count(b'\n')+1,endLine=raw[:end].count(b'\n')+1,sha256=sha(raw[start:end])))
 write('provenance.json',json.dumps(dict(commit=manifest['commit'],spans=spans),indent=2)+'\n')
 cases=json.loads((BASE/'cases.json').read_bytes());write('cases.inc','\n'.join('run("'+c['id']+'",'+cppstr(c['directives'])+','+node(c['node'])+');'for c in cases)+'\n');write('adapter.cpp',(BASE/'adapter.cpp').read_bytes())
 compiler=a.compiler.resolve();vcvars=a.vcvars.resolve();assert compiler.is_file()and vcvars.is_file()
 rsp=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/Fe:'+str(out/'reference.exe'),'/Fo:'+str(out/'adapter.obj'),str(out/'adapter.cpp')];write('compile.rsp','\n'.join('"'+x+'"' for x in rsp));write('compile.cmd','@echo off\ncall "'+str(vcvars)+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+str(compiler)+'" @compile.rsp\nexit /b %errorlevel%\n')
 env=dict(os.environ,TEMP=str(out/'compiler-temp'),TMP=str(out/'compiler-temp'));started=datetime.datetime.now(datetime.timezone.utc).isoformat();cp=subprocess.run(['cmd','/c',str(out/'compile.cmd')],cwd=out,capture_output=True,timeout=120,env=env)
 write('compile.stdout',cp.stdout);write('compile.stderr',cp.stderr);assert cp.returncode==0,cp.stdout.decode(errors='replace')
 proc=subprocess.Popen([str(out/'reference.exe')],cwd=out,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=env)
 class FT(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
 times=[FT()for _ in range(4)];handle=ctypes.c_void_p(int(proc._handle));assert ctypes.windll.kernel32.GetProcessTimes(handle,*[ctypes.byref(t)for t in times]);birth=(times[0].high<<32)|times[0].low
 stdout,stderr=proc.communicate(timeout=30);signaled=ctypes.windll.kernel32.WaitForSingleObject(handle,0)==0;write('golden.ndjson',stdout);write('reference.stderr',stderr)
 receipt=dict(startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),compileExitCode=cp.returncode,exitCode=proc.returncode,pid=proc.pid,birthFileTime=birth,heldProcessHandleSignaled=signaled,timeout=False,stdoutEof=True,stderrEof=True,stdoutSha256=sha(stdout),stderrSha256=sha(stderr),caseCount=len(cases),compilerSha256=sha(compiler.read_bytes()),executableSha256=sha((out/'reference.exe').read_bytes()),inputs={p.name:sha(p.read_bytes())for p in sorted(out.glob('*.inc'))+[out/'adapter.cpp']},casesSha256=sha((BASE/'cases.json').read_bytes()),adapterSha256=sha((BASE/'adapter.cpp').read_bytes()))
 write('receipt.json',json.dumps(receipt,indent=2)+'\n');assert proc.returncode==0 and not stderr and signaled;assert [json.loads(l)['id']for l in stdout.splitlines()]==[c['id']for c in cases]
 if (BASE/'golden.ndjson').exists():assert stdout==(BASE/'golden.ndjson').read_bytes()
 print(json.dumps(receipt))
if __name__=='__main__':main()
