"""固定原典の関数を改変せず抽出し、別々にビルド・実行する。"""
import argparse,ctypes,datetime,hashlib,json,os,pathlib,shutil,subprocess,platform
ROOT=pathlib.Path(__file__).resolve().parent
SPECS={'native.inc':('FilterExpressionNodes.cpp',[(2808,2821),(2832,2860)]),'get-string.inc':('FilterExpressionNodes.cpp',[(51,58)]),'lifting.inc':('FilterExpressionNodes.cpp',[(1693,1704)]),'stringification.inc':('FilterExpressionNodes.cpp',[(2315,2357)]),'windows.inc':('unicoder.cpp',[(1398,1475)]),'registration.inc':('FilterExpressionNodes.cpp',[(3846,3846),(3845,3845),(3847,3847),(3849,3849),(3852,3853)]),'arity.inc':('FilterExpressionNodes.cpp',[(4269,4280)])}
SPECS.update({'converter.inc':('UnicodeConverter.cpp',[(66,86)]),'converter-template.inc':('UnicodeConverter.h',[(85,89)]),'ucs4.inc':('unicoder.cpp',[(50,106)]),'to-tstring.inc':('unicoder.cpp',[(868,880)]),'to-utf8-buffer.inc':('unicoder.cpp',[(926,955)]),'to-utf8.inc':('unicoder.cpp',[(903,908)])})
POCO=json.loads((ROOT/'poco-specs.json').read_text(encoding='utf-8'))
def sha(b):return hashlib.sha256(b).hexdigest()
def lit(v):
 t=v['type'];x=v.get('value')
 if t=='bytes':return 'bytes({'+','.join(str(b) for b in bytes.fromhex(x))+'})'
 if t=='string':return 'bytes({'+','.join(str(b) for b in x.encode('utf-8'))+'})'
 if t=='none':return 'std::monostate{}'
 if t=='int':return 'int64_t('+x+'LL)'
 if t=='double':return 'double('+x+')'
 if t=='bool':return str(x).lower()
 if t=='array':return 'arr({'+','.join(lit(i) for i in x)+'})'
 if t=='regex':return 'std::make_shared<Poco::RegularExpression>()'
 if t=='file':return 'std::make_shared<FileContentRef>(FileContentRef{L"AbC"})'
 raise ValueError(t)
def process(command,out,prefix,env):
 p=subprocess.Popen(command,cwd=out,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 times=[ctypes.c_ulonglong() for _ in range(4)]
 assert ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(p._handle)),*[ctypes.byref(t) for t in times])
 start=datetime.datetime.now(datetime.timezone.utc).isoformat()
 stdout,stderr=p.communicate(timeout=120)
 signaled=ctypes.windll.kernel32.WaitForSingleObject(ctypes.c_void_p(int(p._handle)),0)==0
 (out/(prefix+'.stdout')).write_bytes(stdout);(out/(prefix+'.stderr')).write_bytes(stderr)
 receipt=dict(command=command,pid=p.pid,birthFileTime=times[0].value,startedUtc=start,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=p.returncode,stdoutEof=True,stderrEof=True,heldProcessHandleSignaled=signaled,stdoutSha256=sha(stdout),stderrSha256=sha(stderr))
 (out/(prefix+'.receipt.json')).write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
 assert p.returncode==0,(prefix,stdout.decode(errors='replace'),stderr.decode(errors='replace'))
 assert signaled
 return receipt
def main():
 p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--vcvars',required=True);p.add_argument('--optimization',choices=['Od','O2'],default='Od');a=p.parse_args()
 out=pathlib.Path(os.path.abspath(a.output));repo=ROOT.parents[2];codex=pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex'))).resolve()
 assert out.drive.lower()=='d:' and not out.exists() and not out.is_relative_to(repo) and not out.is_relative_to(codex)
 for q in [out,*out.parents]:
  if q.exists():assert not q.stat().st_file_attributes&0x400,'reparse ancestor'
 assert shutil.disk_usage(out.parent).free>1024**3
 provenance=json.loads((ROOT/'provenance.json').read_text(encoding='utf-8'))
 for s in provenance['sources']:
  b=(ROOT/s['file']).read_bytes();assert len(b)==s['size'] and sha(b)==s['sha256'];assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob']
 out.mkdir();env=os.environ.copy();env['TEMP']=env['TMP']=str(out);env['PYTHONDONTWRITEBYTECODE']='1'
 for name,(source,ranges) in SPECS.items():
  lines=(ROOT/'original'/source).read_bytes().splitlines(keepends=True);(out/name).write_bytes(b''.join(b''.join(lines[lo-1:hi]) for lo,hi in ranges))
 for name,e in POCO.items():
  b=(ROOT/e['source']).read_bytes();body=b''.join(b[lo:hi] for lo,hi in e['ranges']);assert sha(body)==e['sha256'];(out/name).write_bytes(body+b'\n')
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'))
 generated=['run('+json.dumps(c['id'])+','+str(c['functionIndex'])+','+str(c['nullArgs']).lower()+',{'+','.join('Literal('+lit(v)+','+str(i)+','+str(i in c['throws']).lower()+')' for i,v in enumerate(c['args']))+'});' for c in cases]
 (out/'cases.inc').write_text('\n'.join(generated)+'\n',encoding='utf-8')
 cmd=out/'build.cmd';exe=out/'reference.exe'
 cmd.write_text('@echo off\ncall "'+a.vcvars+'" > "'+str(out/'vcvars.log')+'" 2>&1\nif errorlevel 1 exit /b %errorlevel%\ncl /nologo /Bv /std:c++20 /EHsc /utf-8 /'+a.optimization+' /I"'+str(out)+'" /Fo"'+str(out/'reference.obj')+'" /Fe"'+str(exe)+'" "'+str(ROOT/'adapter.cpp')+'" /link /INCREMENTAL:NO\n',encoding='utf-8')
 build=process(['cmd.exe','/d','/c',str(cmd)],out,'compile',env)
 run=process([str(exe)],out,'reference',env);assert not (out/'reference.stderr').read_bytes()
 assert len((out/'reference.stdout').read_bytes().splitlines())==len(cases)
 # OS/locale record is captured from Windows APIs, not inferred from culture names.
 k=ctypes.windll.kernel32;buf=ctypes.create_unicode_buffer(85);assert k.GetUserDefaultLocaleName(buf,85)
 locale=buf.value;assert k.GetSystemDefaultLocaleName(buf,85)
 api=ctypes.WinDLL('kernel32',use_last_error=True).LCMapStringEx
 api.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_wchar_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_ssize_t]
 ctypes.set_last_error(0);probe=api(None,0xffffffff,'Ａガ',2,None,0,None,None,0);err=ctypes.get_last_error();assert probe==0 and err!=0
 kernel=pathlib.Path(os.environ['SystemRoot'])/'System32/kernel32.dll'
 receipt=dict(LCMapStringEx=dict(locale=None,available=True,invalidFlags=4294967295,invalidFlagsResult=probe,invalidFlagsLastError=err,kernel32Path=str(kernel),kernel32Sha256=sha(kernel.read_bytes())),optimization=a.optimization,extraction=SPECS,sourceCommit=provenance['commit'],compile=build,run=run,caseCount=len(cases),platform=platform.platform(),windowsVersion=list(platform.win32_ver()),userDefaultLocale=locale,systemDefaultLocale=buf.value,threadLocale=k.GetThreadLocale(),ansiCodePage=k.GetACP(),oemCodePage=k.GetOEMCP(),executableSha256=sha(exe.read_bytes()),fixedInputs={n:sha((ROOT/n).read_bytes()) for n in ['adapter.cpp','cases.json','provenance.json','reproduce.py','poco-specs.json']},extractedSha256={n:sha((out/n).read_bytes()) for n in [*SPECS,*POCO,'cases.inc']})
 (out/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8');print(json.dumps({'output':str(out),'cases':len(cases),'sha256':run['stdoutSha256']}))
if __name__=='__main__':main()