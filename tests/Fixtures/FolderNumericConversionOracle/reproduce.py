"""固定原典をMSVCでコンパイルし、異なるprocessのstdout/終了/EOFを採取する。"""
import argparse,datetime,hashlib,json,os,pathlib,subprocess,sys,ctypes
ROOT=pathlib.Path(__file__).resolve().parent

def sha(b):return hashlib.sha256(b).hexdigest()
def literal(v):
 t=v['type']; x=v.get('value')
 if t in ('none','throw'):return 'ValueType(std::monostate{})'
 if t=='string':return 'ValueType(bytes({'+','.join(str(b) for b in x.encode('utf-8'))+'}))'
 if t=='int':return 'ValueType(int64_t('+('(-9223372036854775807LL-1)' if x=='-9223372036854775808' else x+'LL')+'))'
 if t=='double':return 'ValueType('+('std::numeric_limits<double>::quiet_NaN()' if x=='nan' else x)+')'
 if t=='bool':return 'ValueType('+str(x).lower()+')'
 if t=='array':return 'arr({'+','.join(literal(a) for a in x)+'})'
 raise ValueError(t)
def validate_sources():
 p=json.loads((ROOT/'provenance.json').read_text(encoding='utf-8-sig'))
 for e in p['sources']:
  b=(ROOT/e['file']).read_bytes()
  assert len(b)==e['size'] and sha(b)==e['sha256'],e['file']
  assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==e['blob'],e['file']
 return p

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--output',required=True);ap.add_argument('--vcvars',required=True);a=ap.parse_args()
 out=pathlib.Path(os.path.abspath(a.output)); repo=ROOT.parents[2]
 assert out.drive.lower()=='d:' and not out.is_relative_to(repo) and not out.exists(),'fresh D output required'
 for p in [out,*out.parents]:
  assert not p.is_symlink() and not p.is_junction(),'linked ancestor'
 assert out.parent.is_dir()
 out.mkdir();temp=out/'temp';temp.mkdir()
 env=os.environ.copy();env['TEMP']=env['TMP']=str(temp);env['PYTHONDONTWRITEBYTECODE']='1'
 provenance=validate_sources();source=(ROOT/'original/FilterExpressionNodes.cpp').read_bytes().splitlines(keepends=True)
 for name,ranges in provenance['extractions'].items():
  (out/(name+'.inc')).write_bytes(b''.join(b''.join(source[lo-1:hi]) for lo,hi in ranges))
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8-sig'))
 lines=[]
 for c in cases:
  values=','.join('Literal('+literal(v)+','+str(i)+','+str(v['type']=='throw').lower()+')' for i,v in enumerate(c['args']))
  lines.append('run('+json.dumps(c['id'])+','+c['function']+'Func,{'+values+'});')
 (out/'cases.inc').write_text('\n'.join(lines)+'\n',encoding='utf-8')
 exe=out/'reference.exe'
 cmd=out/'build.cmd'
 cmd.write_text('@echo off\ncall "'+a.vcvars+'" > "'+str(out/'vcvars.log')+'" 2>&1\nif errorlevel 1 exit /b %errorlevel%\ncl /nologo /Bv /std:c++20 /EHsc /utf-8 /Od /I"'+str(out)+'" /Fo"'+str(out/'reference.obj')+'" /Fe"'+str(exe)+'" "'+str(ROOT/'adapter.cpp')+'" /link /INCREMENTAL:NO\n',encoding='utf-8')
 build=subprocess.run(['cmd.exe','/d','/c',str(cmd)],cwd=out,env=env,capture_output=True)
 (out/'build.stdout').write_bytes(build.stdout);(out/'build.stderr').write_bytes(build.stderr)
 assert build.returncode==0,(build.stdout+build.stderr).decode(errors='replace')
 receipts=[]
 for i in (1,2):
  started=datetime.datetime.now(datetime.timezone.utc).isoformat();p=subprocess.Popen([str(exe)],cwd=out,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
  pid=p.pid
  # Windows process handle is held by Popen until wait/communicate complete.
  creation=ctypes.c_ulonglong();exit_time=ctypes.c_ulonglong();kernel=ctypes.c_ulonglong();user=ctypes.c_ulonglong()
  ok=ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(p._handle)),ctypes.byref(creation),ctypes.byref(exit_time),ctypes.byref(kernel),ctypes.byref(user));assert ok
  stdout,stderr=p.communicate(timeout=60)
  (out/f'run-{i}.ndjson').write_bytes(stdout);(out/f'run-{i}.stderr').write_bytes(stderr)
  receipt={'run':i,'pid':pid,'processCreationFiletime':creation.value,'startedUtc':started,'finishedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'exitCode':p.returncode,'stdoutEof':True,'stderrEof':True,'heldNativeProcessHandle':True,'stdoutSha256':sha(stdout),'stdoutBytes':len(stdout),'stderrSha256':sha(stderr),'executableSha256':sha(exe.read_bytes()),'command':[str(exe)],'cwd':str(out),'temp':str(temp)}
  receipts.append(receipt);assert p.returncode==0 and not stderr
 assert receipts[0]['pid']!=receipts[1]['pid'] and receipts[0]['stdoutSha256']==receipts[1]['stdoutSha256']
 (out/'receipts.json').write_text(json.dumps({'compilerCommand':cmd.read_text(),'compilerBuildExitCode':build.returncode,'compilerStdoutSha256':sha(build.stdout),'compilerStderrSha256':sha(build.stderr),'inputSha256':{name:sha((ROOT/name).read_bytes()) for name in ['adapter.cpp','reproduce.py','cases.json','provenance.json']},'extractionSha256':{name+'.inc':sha((out/(name+'.inc')).read_bytes()) for name in provenance['extractions']},'runs':receipts},indent=2)+'\n')
 print(json.dumps({'cases':len(cases),'stdoutSha256':receipts[0]['stdoutSha256'],'output':str(out)}))
if __name__=='__main__':main()
