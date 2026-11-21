"""固定原典関数の独立native採取。出力は未使用の外部Dディレクトリへ。"""
import argparse,ctypes,datetime,hashlib,json,os,pathlib,shutil,subprocess
ROOT=pathlib.Path(__file__).resolve().parent
SPECS={'native.inc':('FilterExpressionNodes.cpp',[(1693,1718)]),'registration.inc':('FilterExpressionNodes.cpp',[(3789,3789)]),'arity.inc':('FilterExpressionNodes.cpp',[(4269,4280)])}

def sha(b):return hashlib.sha256(b).hexdigest()
def literal(v):
 t=v['type'];x=v.get('value')
 if t=='none':return 'std::monostate{}'
 if t=='string':return 'std::string('+json.dumps(x)+')'
 if t=='int64':return 'int64_t('+('-9223372036854775807LL-1' if x==-(2**63) else str(x)+'LL')+')'
 if t=='double':return 'bits(0x'+x+'ULL)'
 if t=='bool':return 'bool('+str(x).lower()+')'
 if t=='array':return 'array({'+','.join(literal(n) for n in v['items'])+'})'
 return {'timestamp':'Poco::Timestamp{}','regex':'std::make_shared<Poco::RegularExpression>()','file':'std::make_shared<FileContentRef>()'}[t]

def generated(cases):
 return '\n'.join('run('+json.dumps(c['id'])+','+str(c['nullArgs']).lower()+',{'+','.join('Literal('+literal(a)+','+str(i)+','+str(i in c['throws']).lower()+')' for i,a in enumerate(c['args']))+'});' for c in cases)+'\n'

def process(command,output,prefix):
 start=datetime.datetime.now(datetime.timezone.utc).isoformat(); p=subprocess.Popen(command,cwd=output,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 class FT(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
 times=[FT() for _ in range(4)]; ok=ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(p._handle)),*[ctypes.byref(t) for t in times]); assert ok
 birth=(times[0].high<<32)|times[0].low
 stdout,stderr=p.communicate(timeout=60); signaled=ctypes.windll.kernel32.WaitForSingleObject(ctypes.c_void_p(int(p._handle)),0)==0; assert signaled
 (output/(prefix+'.stdout')).write_bytes(stdout);(output/(prefix+'.stderr')).write_bytes(stderr)
 r=dict(command=command,pid=p.pid,birthFileTime=birth,startedUtc=start,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=p.returncode,stdoutEof=True,stderrEof=True,heldProcessHandleSignaled=signaled,stdoutSha256=sha(stdout),stderrSha256=sha(stderr))
 assert p.returncode==0,(prefix,stdout.decode(errors='replace'),stderr.decode(errors='replace'))
 return r


def build(output,compiler):
 output=pathlib.Path(os.path.abspath(output)); repo=ROOT.parents[2]; codex=pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex'))).resolve()
 assert output.drive.lower()=='d:' and not output.exists()
 assert not output.is_relative_to(repo) and not output.is_relative_to(codex)
 for p in [output,*output.parents]:assert not p.is_symlink() and not p.is_junction()
 output.mkdir(parents=True);os.environ['TEMP']=str(output);os.environ['TMP']=str(output)
 provenance=json.loads((ROOT/'provenance.json').read_text(encoding='utf-8'))
 for s in provenance['sources']:
  b=(ROOT/s['file']).read_bytes();assert len(b)==s['size'] and sha(b)==s['sha256'];assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob']
 for name,(source,ranges) in SPECS.items():
  lines=(ROOT/'original'/source).read_bytes().splitlines(keepends=True);(output/name).write_bytes(b''.join(b''.join(lines[a-1:b]) for a,b in ranges))
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'));(output/'cases.inc').write_text(generated(cases),encoding='utf-8',newline='\n')
 compiler=pathlib.Path(shutil.which(compiler) or compiler).resolve(strict=True);exe=output/'reference.exe'
 command=[str(compiler),'/nologo','/std:c++20','/EHsc','/utf-8','/I'+str(output),str(ROOT/'adapter.cpp'),'/Fo'+str(output/'adapter.obj'),'/Fe'+str(exe)]
 compile_receipt=process(command,output,'compile');run_receipt=process([str(exe)],output,'reference');assert not (output/'reference.stderr').read_bytes()
 receipt=dict(sourceCommit=provenance['commit'],compilerSha256=sha(compiler.read_bytes()),executableSha256=sha(exe.read_bytes()),adapterSha256=sha((ROOT/'adapter.cpp').read_bytes()),casesSha256=sha((ROOT/'cases.json').read_bytes()),extraction=SPECS,includeHashes={n:sha((output/n).read_bytes()) for n in [*SPECS,'cases.inc']},compile=compile_receipt,run=run_receipt,caseCount=len(cases))
 (output/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8');print(json.dumps({'output':str(output),'cases':len(cases),'pid':run_receipt['pid']}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--compiler',required=True);a=p.parse_args();build(a.output,a.compiler)
