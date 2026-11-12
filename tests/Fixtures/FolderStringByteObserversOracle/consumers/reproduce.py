"""固定原典関数の独立native採取。出力は未使用の外部Dディレクトリへ。"""
import argparse,zipfile,ctypes,datetime,hashlib,json,os,pathlib,shutil,subprocess
ROOT=pathlib.Path(__file__).resolve().parent
SPECS=json.loads((ROOT/'include-specs.json').read_text('utf-8'))
def sha(b):return hashlib.sha256(b).hexdigest()
def literal(v):
 t=v['type']; x=v.get('value')
 if t=='none':return 'std::monostate{}'
 if t=='string':return 'bytes({'+','.join(str(b) for b in bytes.fromhex(x))+'})'
 if t=='int64':return 'int64_t('+('-9223372036854775807LL-1' if x==-(2**63) else str(x)+'LL')+')'
 if t=='double':return 'double('+repr(x)+')'
 if t=='bool':return 'bool('+str(x).lower()+')'
 if t=='array':return 'array({'+','.join(literal(n) for n in v['items'])+'})'
 raise ValueError(t)
def generated(cases):
 return '\n'.join('run('+json.dumps(c['id'])+','+json.dumps(c['operation'])+','+str(c['caseSensitive']).lower()+',{'+','.join('Literal('+literal(a)+','+str(i)+','+str(i in c['throws']).lower()+')' for i,a in enumerate(c['args']))+'});' for c in cases)+'\n'

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
 output=pathlib.Path(os.path.abspath(output)); repo=ROOT.parents[3]; codex=pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex'))).resolve()
 assert output.drive.lower()=='d:' and not output.exists()
 assert not output.is_relative_to(repo) and not output.is_relative_to(codex)
 for p in [output,*output.parents]:assert not p.is_symlink() and not p.is_junction()
 assert shutil.disk_usage(output.parent).free>100*1024*1024
 output.mkdir(parents=True);os.environ['TEMP']=str(output);os.environ['TMP']=str(output)
 archive=zipfile.ZipFile(ROOT/'reference-source.zip');provenance=json.loads((ROOT/'archive-provenance.json').read_text('utf-8'));assert sha((ROOT/'reference-source.zip').read_bytes())==provenance['archiveSha256']
 for entry in json.loads((ROOT/'source-archive-manifest.json').read_text('utf-8')):
  data=archive.read(entry['path']);assert len(data)==entry['size'] and sha(data)==entry['sha256']
 source_hashes={}
 for s in provenance['sources']:
  key='original/'+s['path'];data=archive.read(key);assert len(data)==s['size'] and sha(data)==s['sha256'];assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==s['blob'];source_hashes[key]=sha(data)
  if s['path'].startswith('Externals/poco/dependencies/pcre2/src/'):
   dest=output/key;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data)
 for name,entry in SPECS.items():
  data=archive.read(entry['source'][4:]) if entry['source'].startswith('zip:') else (ROOT/entry['source']).read_bytes();body=b''.join(data[a:b] for a,b in entry['ranges']);assert sha(body)==entry['sha256'];(output/name).write_bytes(body+b'\n')
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'));(output/'cases.inc').write_text(generated(cases),encoding='utf-8',newline='\n')
 compiler=pathlib.Path(shutil.which(compiler) or compiler).resolve(strict=True);exe=output/'reference.exe'
 cdir=output/'original/Externals/poco/dependencies/pcre2/src'
 c_command=[str(compiler),'/nologo','/c','/O2','/DHAVE_CONFIG_H','/DPCRE2_STATIC','/I'+str(cdir),'/Fo'+str(output/'pcre2.obj'),str(cdir/'pcre2.c')]
 c_receipt=process(c_command,output,'compile-c')
 command=[str(compiler),'/nologo','/std:c++20','/EHsc','/utf-8','/DPCRE2_STATIC','/I'+str(cdir),'/I'+str(output),str(ROOT/'adapter.cpp'),str(output/'pcre2.obj'),'/Fo'+str(output/'adapter.obj'),'/Fe'+str(exe)]
 compile_receipt=process(command,output,'compile');run_receipt=process([str(exe)],output,'reference');assert not (output/'reference.stderr').read_bytes()
 receipt=dict(compileC=c_receipt,sourceHashes=source_hashes,archiveSha256=provenance['archiveSha256'],sourceCommit=provenance['commit'],compilerSha256=sha(compiler.read_bytes()),executableSha256=sha(exe.read_bytes()),adapterSha256=sha((ROOT/'adapter.cpp').read_bytes()),casesSha256=sha((ROOT/'cases.json').read_bytes()),extraction=SPECS,includeHashes={n:sha((output/n).read_bytes()) for n in [*SPECS,'cases.inc']},compile=compile_receipt,run=run_receipt,caseCount=len(cases))
 (output/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8');print(json.dumps({'output':str(output),'cases':len(cases),'pid':run_receipt['pid']}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--compiler',required=True);a=p.parse_args();build(a.output,a.compiler)
