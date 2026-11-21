"""Pinned Glob capture; isolate every case in a native process, including native faults."""
import argparse,ctypes,datetime,hashlib,json,os,pathlib,shutil,subprocess,zipfile
ROOT=pathlib.Path(__file__).resolve().parent
SPECS=json.loads((ROOT/'include-specs.json').read_text('utf-8'))
def sha(b):return hashlib.sha256(b).hexdigest()
def process(command,output,prefix):
 start=datetime.datetime.now(datetime.timezone.utc).isoformat(); p=subprocess.Popen(command,cwd=output,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 class FT(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
 times=[FT() for _ in range(4)]; ok=ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(p._handle)),*[ctypes.byref(t) for t in times]); assert ok
 birth=(times[0].high<<32)|times[0].low
 stdout,stderr=p.communicate(timeout=60); signaled=ctypes.windll.kernel32.WaitForSingleObject(ctypes.c_void_p(int(p._handle)),0)==0; assert signaled
 (output/(prefix+'.stdout')).write_bytes(stdout);(output/(prefix+'.stderr')).write_bytes(stderr)
 r=dict(command=command,pid=p.pid,birthFileTime=birth,startedUtc=start,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=p.returncode,stdoutEof=True,stderrEof=True,heldProcessHandleSignaled=signaled,stdoutSha256=sha(stdout),stderrSha256=sha(stderr))
 return r


def build(output,compiler):
 output=pathlib.Path(os.path.abspath(output));repo=ROOT.parents[3];codex=pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex'))).resolve()
 assert output.drive.lower()=='d:' and not output.exists() and not output.is_relative_to(repo) and not output.is_relative_to(codex)
 for p in [output,*output.parents]:assert not p.is_symlink() and not p.is_junction()
 assert shutil.disk_usage(output.parent).free>200*1024*1024
 output.mkdir(parents=True);os.environ['TEMP']=str(output);os.environ['TMP']=str(output)
 provenance=json.loads((ROOT/'provenance.json').read_text('utf-8'))
 for s in provenance['sources']:
  data=(ROOT/s['file']).read_bytes();assert sha(data)==s['sha256'] and len(data)==s['size'];assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==s['blob']
 archive_path=ROOT/provenance['pcreArchive'];assert sha(archive_path.read_bytes())==provenance['pcreArchiveSha256'];archive=zipfile.ZipFile(archive_path);pcre_sources={}
 for name in archive.namelist():
  if name.startswith('original/Externals/poco/dependencies/pcre2/src/'):
   data=archive.read(name);dest=output/name;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data);pcre_sources[name]=sha(data)
 for name,entry in SPECS.items():
  data=(ROOT/entry['source']).read_bytes();body=b''.join(data[a:b] for a,b in entry['ranges']);assert sha(body)==entry['sha256'];(output/name).write_bytes(body+b'\n')
 cases=json.loads((ROOT/'glob-cases.json').read_text('utf-8'))
 def literal(hex):return 'bytes({'+','.join(map(str,bytes.fromhex(hex)))+'})'
 generated='\n'.join('if(index=='+str(i)+'){run('+json.dumps(c['id'])+','+str(c['caseSensitive']).lower()+','+literal(c['subjectHex'])+','+literal(c['patternHex'])+');return 0;}' for i,c in enumerate(cases))+'\n';(output/'glob-cases.inc').write_text(generated,encoding='utf-8',newline='\n')
 compiler=pathlib.Path(shutil.which(compiler) or compiler).resolve(strict=True);cdir=output/'original/Externals/poco/dependencies/pcre2/src';exe=output/'reference.exe'
 c_command=[str(compiler),'/nologo','/c','/O2','/DHAVE_CONFIG_H','/DPCRE2_STATIC','/I'+str(cdir),'/Fo'+str(output/'pcre2.obj'),str(cdir/'pcre2.c')]
 cr=process(c_command,output,'compile-c');assert cr['exitCode']==0
 command=[str(compiler),'/nologo','/std:c++20','/EHsc','/utf-8','/DPCRE2_STATIC','/I'+str(cdir),'/I'+str(output),str(ROOT/'adapter.cpp'),str(output/'pcre2.obj'),'/Fo'+str(output/'adapter.obj'),'/Fe'+str(exe)]
 cpp=process(command,output,'compile');assert cpp['exitCode']==0
 runs=[];raw=[]
 for i,c in enumerate(cases):
  prefix='case-'+str(i).zfill(4);q=process([str(exe),str(i)],output,prefix);runs.append(q)
  raw.append({'id':c['id'],'exitCode':q['exitCode'],'stdoutHex':(output/(prefix+'.stdout')).read_bytes().hex(),'stderrHex':(output/(prefix+'.stderr')).read_bytes().hex()})
 (output/'raw-cases.ndjson').write_text(''.join(json.dumps(row,separators=(',',':'))+'\n' for row in raw),encoding='utf-8',newline='\n')
 receipt=dict(sourceCommit=provenance['commit'],compilerSha256=sha(compiler.read_bytes()),executableSha256=sha(exe.read_bytes()),pcreObjectSha256=sha((output/'pcre2.obj').read_bytes()),adapterSha256=sha((ROOT/'adapter.cpp').read_bytes()),casesSha256=sha((ROOT/'glob-cases.json').read_bytes()),extraction=SPECS,includeHashes={name:sha((output/name).read_bytes()) for name in [*SPECS,'glob-cases.inc']},pcreSources=pcre_sources,compileC=cr,compile=cpp,runs=runs,caseCount=len(cases),rawCasesSha256=sha((output/'raw-cases.ndjson').read_bytes()))
 (output/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8');print(json.dumps({'output':str(output),'cases':len(cases),'nativeFaults':sum(q['exitCode']!=0 for q in runs)}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--compiler',required=True);args=p.parse_args();build(args.output,args.compiler)
