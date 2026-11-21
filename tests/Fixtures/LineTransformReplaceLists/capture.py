"""固定native関数をfresh external Tempへ無変更抽出しMSVC/PCRE2で採取。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,subprocess,ctypes,datetime,argparse,os
BASE=pathlib.Path(__file__).resolve().parent
COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
def sha(b):return hashlib.sha256(b).hexdigest()
def write(p,b):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():raise RuntimeError('fresh-only '+str(p))
 p.write_bytes(b if isinstance(b,bytes) else b.encode('utf8'))
def validate(p):
 p=p.absolute()
 for a in [p,*p.parents]:
  assert not a.is_symlink() and not a.is_junction(),str(a)
  if a.exists():assert not(getattr(a.stat(),'st_file_attributes',0)&0x400),str(a)
 p=p.resolve();repo=BASE.parents[2]
 for r in [repo,pathlib.Path.home()/'.codex']+([pathlib.Path(os.environ['CODEX_HOME'])] if os.environ.get('CODEX_HOME') else []):assert not p.is_relative_to(r.resolve()),str(p)
 assert p.is_relative_to(pathlib.Path(os.environ['TEMP']).resolve()),'external Temp required'
 return p
def block(b,marker):
 a=b.index(marker.encode());i=b.index(b'{',a);level=0;state='code'
 while i<len(b):
  c=b[i:i+1];n=b[i:i+2]
  if state=='code':
   if n==b'//':state='line';i+=1
   elif n==b'/*':state='comment';i+=1
   elif c==b'"':state='string'
   elif c==b"'":state='char'
   elif c==b'{':level+=1
   elif c==b'}':
    level-=1
    if level==0:return a,i+1
  elif state=='line':
   if c==b'\n':state='code'
  elif state=='comment':
   if n==b'*/':state='code';i+=1
  elif c==b'\\':i+=1
  elif (state=='string' and c==b'"') or(state=='char' and c==b"'"):state='code'
  i+=1
 raise RuntimeError(marker)
def literal(s):return 'bytes({'+','.join(map(str,s.encode('utf8')))+'})'
def node(n,path):
 t=n['type'];v=n.get('value')
 if t=='path':return 'new StringLiteral('+literal(path)+')'
 if t=='string':return 'new StringLiteral('+literal(v)+')'
 if t=='integer':return 'new IntLiteral(int64_t('+str(v)+'))'
 if t=='bool':return 'new BoolLiteral('+str(v).lower()+')'
 if t=='none':return 'new NoneLiteral()'
 if t=='throw':return 'new ThrowNode()'
 if t=='regex':return 'new RegularExpressionLiteral('+literal(v)+','+str(n.get('caseSensitive',True)).lower()+')'
 if t=='array':return 'array({'+','.join(node(x,path) for x in v)+'})'
 raise ValueError(t)
def sources():
 manifest=json.loads((BASE/'source-manifest.json').read_bytes());archive=BASE.parent/'LineTransformRegex/reference-source.zip';assert sha(archive.read_bytes())==manifest['archiveSha256'];data={}
 with zipfile.ZipFile(archive) as z:
  for item in manifest['sources']:
   p=item['path'];b=z.read('original/'+p) if item['origin']!='additional-original' else (BASE/'additional-original'/p).read_bytes()
   assert len(b)==item['size'] and sha(b)==item['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==item['blob'];data[p]=b
 return manifest,data

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--compiler',type=pathlib.Path,required=True);ap.add_argument('--vcvars',type=pathlib.Path,required=True);ap.add_argument('--initial-observation',action='store_true');a=ap.parse_args()
 out=validate(a.output);assert not out.exists();out.mkdir(parents=True);(out/'compiler-temp').mkdir();manifest,data=sources();ext=[]
 def extract(path,name,marker,semi=False):
  raw=data[path];start,end=block(raw,marker)
  if semi:assert raw[end:end+1]==b';';end+=1
  write(out/name,raw[start:end]+b'\n');ext.append(dict(file=name,path=path,startByte=start,endByte=end,startLine=raw[:start].count(b'\n')+1,endLine=raw[:end].count(b'\n')+1,sha256=sha(raw[start:end]),outerLf=True))
 cpp='Src/FilterEngine/FilterExpressionNodes.cpp';h='Src/FilterEngine/FilterExpressionNodes.h';p='Externals/poco/Foundation/src/RegularExpression.cpp';ph='Externals/poco/Foundation/include/Poco/RegularExpression.h';loader='Src/FilterEngine/ReplaceList.cpp'
 for name,marker in [('string.inc','std::string ToStringValue('),('get.inc','static std::optional<std::string> getAsString('),('escape.inc','static std::string escapeRegex('),('flags.inc','static inline int getRegexFlags('),('create.inc','static std::shared_ptr<Poco::RegularExpression> createRegex('),('lifting.inc','template <typename Func>'),('subst-string.inc','static bool regexSubst(std::string& str, const std::string& pattern'),('subst-regex.inc','static bool regexSubst(std::string& str, const Poco::RegularExpression& regex'),('regex-literal-ctor.inc','RegularExpressionLiteral::RegularExpressionLiteral('),('replace-list-func.inc','static auto ReplaceWithListFunc('),('regex-list-func.inc','static auto RegexReplaceWithListFunc(')]:extract(cpp,name,marker)
 for t in ['ExprNode','BoolLiteral','NoneLiteral','DoubleLiteral','IntLiteral','StringLiteral','DateTimeLiteral','RegularExpressionLiteral','ArrayLiteral']:extract(h,t+'.inc','struct '+t+('\n' if t=='ExprNode' else ' : public ExprNode'),True)
 for name,marker in [('options.inc','enum Options'),('poco-subst-inline.inc','inline int RegularExpression::subst('),('match-type.inc','struct Match')]:extract(ph,name,marker,marker in ['enum Options','struct Match'])
 extract(p,'match-data.inc','\tclass MatchData')
 for name,marker in [('poco-ctor.inc','RegularExpression::RegularExpression('),('poco-dtor.inc','RegularExpression::~RegularExpression('),('poco-subst.inc','int RegularExpression::subst(std::string& subject, std::string::size_type offset'),('poco-subst-one.inc','std::string::size_type RegularExpression::substOne('),('poco-match.inc','int RegularExpression::match(const std::string& subject, std::string::size_type offset, Match& mtch'),('poco-match-vector.inc','int RegularExpression::match(const std::string& subject, std::string::size_type offset, MatchVec& matches'),('poco-compile-options.inc','int RegularExpression::compileOptions('),('poco-match-options.inc','int RegularExpression::matchOptions(')]:extract(p,name,marker)
 for name,marker in [('guess-encoding.inc','static void GuessEncoding('),('load-impl.inc','template<typename ProcessLineFn>'),('load-list.inc','std::shared_ptr<std::vector<ValueType2>> LoadList('),('load-regex-list.inc','std::shared_ptr<std::vector<ValueType2>> LoadRegexList(')]:extract(loader,name,marker)
 # Whole optimizer/OS reader are source evidence, not executed. Record exact native spans.
 for path,marker in [(cpp,'ExprNode* FunctionNode::Optimize()'),('Src/Common/UniFile.cpp','bool UniMemFile::ReadBom()'),('Src/Common/UniFile.cpp','bool UniMemFile::ReadString(String & line, String & eol, bool * lossy)')]:
  b=data[path];start,end=block(b,marker);ext.append(dict(file=None,path=path,startByte=start,endByte=end,startLine=b[:start].count(b'\n')+1,endLine=b[:end].count(b'\n')+1,sha256=sha(b[start:end]),outerLf=False))
 for name,fn in [('replacewithlist','ReplaceWithListFunc'),('regexreplacewithlist','RegexReplaceWithListFunc')]:
  b=data[cpp];mark=('{"'+name+'", '+fn+', 2, 2}').encode();start=b.index(mark);end=start+len(mark);ext.append(dict(file=None,path=cpp,startByte=start,endByte=end,startLine=b[:start].count(b'\n')+1,endLine=b[:end].count(b'\n')+1,sha256=sha(mark),outerLf=False))
 write(out/'provenance.json',json.dumps(dict(commit=COMMIT,sourceManifestSha256=sha((BASE/'source-manifest.json').read_bytes()),extractions=ext),indent=2)+'\n')
 cases=json.loads((BASE/'cases.json').read_bytes());lines=[]
 for c in cases:
  filecase=any(n['type']=='path' for n in c['args']);path='inputs/'+c['id']+'.tsv' if filecase else ''
  if c['fileUtf8Hex'] is not None:write(out/path,bytes.fromhex(c['fileUtf8Hex']))
  lines.append('run('+json.dumps(c['id'])+','+str(c['function']=='RegexReplaceWithList').lower()+','+str(c['caseSensitive']).lower()+','+json.dumps(path)+','+str(c['nullArgs']).lower()+',{'+','.join(node(n,path) for n in c['args'])+'});')
 write(out/'cases.inc','\n'.join(lines)+'\n');write(out/'adapter.cpp',(BASE/'adapter.cpp').read_bytes())
 cdir=out/'pcre2'
 for path,b in data.items():
  if path.startswith('Externals/poco/dependencies/pcre2/src/'):write(cdir/path.rsplit('/',1)[1],b)
 compiler=a.compiler.resolve();vcvars=a.vcvars.resolve();assert compiler.is_file() and vcvars.is_file()
 cargs=['/nologo','/c','/O2','/DHAVE_CONFIG_H','/DPCRE2_STATIC','/I'+str(cdir),'/Fo:'+str(out/'pcre2.obj'),str(cdir/'pcre2.c')]
 cppargs=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/DPCRE2_STATIC','/I'+str(cdir),'/Fe:'+str(out/'reference.exe'),'/Fo:'+str(out/'adapter.obj'),str(out/'adapter.cpp'),str(out/'pcre2.obj')]
 for name,args in [('compile-c.rsp',cargs),('compile-cpp.rsp',cppargs)]:write(out/name,'\n'.join('"'+x+'"' for x in args))
 write(out/'compile.cmd','@echo off\ncall "'+str(vcvars)+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+str(compiler)+'" @compile-c.rsp\nif errorlevel 1 exit /b %errorlevel%\n"'+str(compiler)+'" @compile-cpp.rsp\nexit /b %errorlevel%\n')
 env=dict(os.environ,TEMP=str(out/'compiler-temp'),TMP=str(out/'compiler-temp'));started=datetime.datetime.now(datetime.timezone.utc).isoformat();cp=subprocess.run(['cmd','/c',str(out/'compile.cmd')],cwd=out,env=env,capture_output=True,timeout=240)
 write(out/'compile.stdout',cp.stdout);write(out/'compile.stderr',cp.stderr)
 inputs=[p for p in out.iterdir() if p.suffix in ['.inc','.cpp','.rsp']]
 write(out/'compile-receipt.json',json.dumps(dict(exitCode=cp.returncode,timeout=False,stdoutEof=True,stderrEof=True,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),compilerSha256=sha(compiler.read_bytes()),inputSha256={p.name:sha(p.read_bytes()) for p in inputs},pcreSourceSha256={p.name:sha(p.read_bytes()) for p in cdir.iterdir()},TEMP=env['TEMP'],TMP=env['TMP']),indent=2)+'\n')
 if cp.returncode:raise RuntimeError('compile failed: '+cp.stdout.decode(errors='replace')[-5000:])
 started=datetime.datetime.now(datetime.timezone.utc).isoformat();proc=subprocess.Popen([str(out/'reference.exe')],cwd=out,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 class FT(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
 times=[FT() for _ in range(4)];handle=ctypes.c_void_p(int(proc._handle));assert ctypes.windll.kernel32.GetProcessTimes(handle,*[ctypes.byref(t) for t in times]);birth=(times[0].high<<32)|times[0].low;timed=False
 try:stdout,stderr=proc.communicate(timeout=30)
 except subprocess.TimeoutExpired:timed=True;proc.kill();stdout,stderr=proc.communicate(timeout=10)
 signaled=ctypes.windll.kernel32.WaitForSingleObject(handle,0)==0;write(out/'golden.ndjson',stdout);write(out/'reference.stderr',stderr)
 receipt=dict(sourceCommit=COMMIT,pid=proc.pid,birthFileTime=birth,heldProcessHandleSignaled=signaled,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=proc.returncode,timeout=timed,stdoutEof=True,stderrEof=True,stdoutSha256=sha(stdout),stderrSha256=sha(stderr),caseCount=len(cases),casesSha256=sha((BASE/'cases.json').read_bytes()),adapterSha256=sha((BASE/'adapter.cpp').read_bytes()),executableSha256=sha((out/'reference.exe').read_bytes()))
 write(out/'receipt.json',json.dumps(receipt,indent=2)+'\n');assert not timed and not stderr and signaled and proc.returncode==0
 assert [json.loads(l)['id'] for l in stdout.splitlines()]==[c['id'] for c in cases]
 if (BASE/'golden.ndjson').exists() and not a.initial_observation:assert stdout==(BASE/'golden.ndjson').read_bytes()
 print(json.dumps(receipt))
if __name__=='__main__':main()
