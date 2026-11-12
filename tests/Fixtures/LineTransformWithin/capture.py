import sys
sys.dont_write_bytecode=True
import pathlib, json, hashlib, zipfile, subprocess, ctypes, datetime, argparse, shutil, os
COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
ZIP_SHA='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def write(p,b):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():raise RuntimeError('fresh-only '+str(p))
 p.write_bytes(b if isinstance(b,bytes) else b.encode('utf-8'))
def validate(p,repo):
 p=p.absolute()
 for a in [p,*p.parents]:
  assert not a.name.startswith('.') and a.name.lower()!='artifacts' and not a.is_symlink() and not a.is_junction()
  if a.exists():assert not (getattr(a.stat(),'st_file_attributes',0)&0x400)
 p=p.resolve()
 assert not p.is_relative_to(repo) and not p.is_relative_to(BASE) and not p.is_relative_to(pathlib.Path.home()/'.codex')
 import os
 if os.environ.get('CODEX_HOME'):assert not p.is_relative_to(pathlib.Path(os.environ['CODEX_HOME']).resolve())
 return p
def block(b,marker):
 a=b.index(marker.encode()); brace=b.index(b'{',a);i=brace;level=0;state='code'
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
  elif (state=='string' and c==b'"') or (state=='char' and c==b"'"):state='code'
  i+=1
 raise RuntimeError(marker)
def lit(s):return 'bytes({'+','.join(str(x) for x in s.encode('utf-8'))+'})'
def node(n):
 t=n['type'];v=n.get('value')
 if t=='integer':return 'new IntLiteral('+('INT64_MIN' if v==-(2**63) else str(v)+'LL')+')'
 if t=='double':return 'new DoubleLiteral('+str(v)+')'
 if t=='string':return 'new StringLiteral('+lit(v)+')'
 if t=='raw-string':return 'new StringLiteral(bytes({'+','.join(map(str,v))+'}))'
 if t=='bool':return 'new BoolLiteral('+str(v).lower()+')'
 if t=='none':return 'new NoneLiteral()'
 if t=='array':return 'array({'+','.join(node(x) for x in v)+'})'
 if t=='timestamp':return 'new DateTimeLiteral(Poco::Timestamp('+str(v)+'LL))'
 if t=='throw':return 'new ThrowNode('+lit(v)+')'
 raise ValueError(t)
def public(n):
 t=n['type'];v=n.get('value')
 if t=='integer':return str(v)
 if t=='double':return str(v)
 if t=='string':
  if any(c in v for c in ['\0','\r','\n']):return None
  return json.dumps(v,ensure_ascii=False)
 if t=='bool':return str(v).lower()
 if t=='none':return 'none'
 if t=='array':
  items=[public(x) for x in v]
  return 'Array('+', '.join(items)+')' if all(x is not None for x in items) else None
 return None
def make_cases():
 cases=[]
 def n(t,v=None):return dict(type=t,**({} if v is None else dict(value=v)))
 I=lambda v:n('integer',v);D=lambda v:n('double',v);S=lambda v:n('string',v);A=lambda *v:n('array',list(v));T=lambda v:n('throw',v)
 def add(id,args,cs=True,opt=False,classification='public-literal'):
  cases.append(dict(id=id,args=args,caseSensitive=cs,mode='optimizer-traversal' if opt else 'function-body-noopt',classification=classification))
 for id,v,lo,hi in [('int-below',0,1,3),('int-lower',1,1,3),('int-inside',2,1,3),('int-upper',3,1,3),('int-above',4,1,3),('int-reversed',2,3,1),('int-empty',2,2,2),('int-negative',-1,-2,0),('int-min',-(2**63),-(2**63),-(2**63)+1),('int-max',2**63-1,2**63-2,2**63-1),('2p53-exact',2**53+1,2**53+1,2**53+2),('2p53-lower-out',2**53,2**53+1,2**53+2)]:add(id,[I(v),I(lo),I(hi)])
 for id,args in [('long-exact-lower-double-upper',[I(2**53),I(2**53+1),D(float(2**53+2))]),('long-double-lower-exact-upper',[I(2**53),D(float(2**53)),I(2**53+1)]),('long-doubleboth-upper',[I(2**53+1),D(float(2**53)),D(float(2**53+1))]),('long-max-exact-upper',[I(2**63-1),D(float(2**63-2)),I(2**63-1)]),('long-max-double-upper',[I(2**63-1),I(2**63-2),D(float(2**63-1))]),('long-min-doublelower',[I(-(2**63)),D(float(-(2**63))),I(-(2**63)+1)]),('double-lower',[D(1.0),I(1),I(3)]),('double-upper',[D(3.0),I(1),I(3)]),('double-fraction',[D(1.5),D(1.0),I(2)]),('double-long-upper-round',[D(float(2**53)),I(2**53),I(2**53+1)]),('double-long-lower-round',[D(float(2**53)),I(2**53+1),I(2**53+2)]),('double-reversed',[D(2.0),D(3.0),D(1.0)])]:add(id,args)
 for cs in [True,False]:
  for id,v,lo,hi in [('ascii-lower','a','a','c'),('ascii-upper','c','a','c'),('ascii-mixed','B','a','c'),('ascii-empty','','','a'),('ascii-reversed','b','c','a'),('utf8-accent','Ä','ä','ö'),('utf8-japanese','い','あ','う'),('utf8-vs-ascii','é','z','zz'),('utf8-prefix','éa','é','éb')]:add(id+('-cs' if cs else '-ci'),[S(v),S(lo),S(hi)],cs)
 for id,args in [('none-value',[n('none'),I(0),I(3)]),('bool-value',[n('bool',True),I(0),I(3)]),('none-lower',[I(1),n('none'),I(3)]),('none-upper',[I(1),I(0),n('none')]),('string-value-numberbounds',[S('1'),I(0),I(3)]),('int-stringbounds',[I(1),S('0'),S('3')]),('double-boollower',[D(1.0),n('bool',False),I(3)]),('array-lower',[I(1),A(I(0)),I(3)]),('array-upper',[I(1),I(0),A(I(3))]),('array-empty',[A(),I(0),I(3)]),('array-mixed',[A(I(0),I(1),I(3),D(2.5),S('x'),n('none'),n('bool',False)),I(1),I(3)]),('array-nested',[A(I(1),A(I(2)),A()),I(0),I(3)]),('array-string',[A(S('a'),S('B'),S('c')),S('a'),S('c')])]:add(id,args,False if id=='array-string' else True)
 for id,args in [('eager-min-throw',[T('value'),T('min'),T('max')]),('eager-max-throw',[T('value'),n('none'),T('max')]),('eager-value-throw',[T('value'),n('none'),I(3)]),('eager-value-none',[n('none'),I(0),I(3)])]:add(id,args,classification='raw-throw-boundary' if any(x['type']=='throw' for x in args) else 'public-literal')
 for id,args in [('arity-0',[]),('arity-1',[I(1)]),('arity-2',[I(1),I(0)]),('arity-4',[I(1),I(0),I(3),T('fourth-not-evaluated')])]:add(id,args,classification='constructor-boundary')
 for id,args in [('timestamp-lower',[n('timestamp',10),n('timestamp',10),n('timestamp',20)]),('timestamp-upper',[n('timestamp',20),n('timestamp',10),n('timestamp',20)]),('timestamp-mismatch',[n('timestamp',10),I(0),I(20)]),('array-timestamp',[A(n('timestamp',10),n('timestamp',20)),n('timestamp',10),n('timestamp',20)])]:add(id,args,classification='typed-timestamp-unrepresentable')
 for id,args in [('opt-int',[I(1),I(0),I(3)]),('opt-array',[A(I(1),n('none')),I(0),I(3)]),('opt-throw-max',[I(1),I(0),T('max')]),('opt-string-ci',[S('B'),S('a'),S('c')])]:add(id,args,False if id=='opt-string-ci' else True,True,'raw-throw-boundary' if id=='opt-throw-max' else 'public-literal')
 add('raw-nul-string',[n('raw-string',[97,0,98]),S('a'),S('b')],classification='raw-byte-string-boundary')
 return cases
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--repo',type=pathlib.Path,default=BASE.parents[2]);ap.add_argument('--reference-zip',type=pathlib.Path,default=BASE.parent/'LineTransformReplace/reference-source.zip');ap.add_argument('--output',required=True,type=pathlib.Path);ap.add_argument('--compiler',required=True,type=pathlib.Path);ap.add_argument('--vcvars',required=True,type=pathlib.Path);args=ap.parse_args()
 if os.name!='nt':raise RuntimeError('MSVC Windows producer only; macOS unverified')
 repo=args.repo.resolve();out=validate(args.output,repo);out.mkdir(parents=True,exist_ok=False)
 zpath=args.reference_zip.resolve();assert sha(zpath.read_bytes())==ZIP_SHA
 archive=zipfile.ZipFile(zpath); data={};sources=[];ext=[]
 def source(path):
  if path in data:return data[path]
  key='original/'+path
  if key in archive.namelist():b=archive.read(key);origin='fixed-reused-replace-zip'
  else:b=(BASE/'additional-original'/path).read_bytes();origin='additional-fixed-git-blob'
  fixed=json.loads((BASE/'source-manifest.json').read_text('utf8'))
  expected=next(s for s in fixed['sources'] if s['path']==path)
  blob=hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()
  assert blob==expected['blob'] and len(b)==expected['size'] and sha(b)==expected['sha256']
  sources.append(expected);data[path]=b
  return b
 def extract(path,name,marker,semicolon=False):
  b=source(path);a,z=block(b,marker)
  if semicolon:assert b[z:z+1]==b';';z+=1
  write(out/name,b[a:z]+b'\n');ext.append(dict(file=name,path=path,startByte=a,endByte=z,startLine=b[:a].count(b'\n')+1,endLine=b[:z].count(b'\n')+1,sha256=sha(b[a:z]),outerLf=True));return b[a:z]
 cpp='Src/FilterEngine/FilterExpressionNodes.cpp';h='Src/FilterEngine/FilterExpressionNodes.h'
 extract(cpp,'iswithin.inc','static auto IsWithinFunc(');extract(cpp,'string.inc','std::string ToStringValue(');extract(cpp,'lifting.inc','template <typename Func>')
 for name in ['ExprNode','BoolLiteral','NoneLiteral','DoubleLiteral','IntLiteral','StringLiteral','ArrayLiteral','DateTimeLiteral']:extract(h,name+'.inc','struct '+name+('\n' if name=='ExprNode' else ' : public ExprNode'),True)
 ah='Externals/poco/Foundation/include/Poco/Ascii.h';sh='Externals/poco/Foundation/include/Poco/String.h';th='Externals/poco/Foundation/include/Poco/Timestamp.h';tc='Externals/poco/Foundation/src/Timestamp.cpp'
 extract(ah,'ascii-class.inc','class Foundation_API Ascii',True)
 for name,marker in [('ascii-properties.inc','inline int Ascii::properties('),('ascii-isascii.inc','inline bool Ascii::isAscii('),('ascii-hasproperties.inc','inline bool Ascii::hasProperties('),('ascii-isupper.inc','inline bool Ascii::isUpper('),('ascii-lower.inc','inline int Ascii::toLower(')]:extract(ah,name,marker)
 extract('Externals/poco/Foundation/src/Ascii.cpp','ascii-table.inc','const int Ascii::CHARACTER_PROPERTIES[128]',True)
 extract(sh,'icompare.inc','template <class S>\nint icompare(const S& str1, const S& str2)')
 extract(th,'timestamp-class.inc','class Foundation_API Timestamp',True)
 for op in ['==','!=','>','>=','<','<=']:
  marker='inline bool Timestamp::operator '+(' ' if op in ['>','<'] else '')+op+' '
  # Original source has a space between the two equality tokens.
  marker={'==':'inline bool Timestamp::operator == ','!=':'inline bool Timestamp::operator != ','>':'inline bool Timestamp::operator >  ','>=':'inline bool Timestamp::operator >= ','<':'inline bool Timestamp::operator <  ','<=':'inline bool Timestamp::operator <= '}[op]
  extract(th,'timestamp-op'+{'==':'eq','!=':'ne','>':'gt','>=':'ge','<':'lt','<=':'le'}[op]+'.inc',marker)
 for name,marker in [('timestamp-value.inc','Timestamp::Timestamp(TimeVal tv)'),('timestamp-copy.inc','Timestamp::Timestamp(const Timestamp& other)'),('timestamp-dtor.inc','Timestamp::~Timestamp()')]:extract(tc,name,marker)
 f=source(cpp);a=f.index(b'ExprNode* FunctionNode::Optimize()');z=f.index(b'\n\t// Constant-time functions',a)
 prefix=f[a:z];prefix=prefix[prefix.index(b'\tif (args)'):]
 write(out/'optimizer-visit.inc',prefix+b'\n');start=a+f[a:z].index(b'\tif (args)');ext.append(dict(file='optimizer-visit.inc',path=cpp,startByte=start,endByte=z,startLine=f[:start].count(b'\n')+1,endLine=f[:z].count(b'\n')+1,sha256=sha(f[start:z]),outerLf=True))
 oa,oz=block(f,'ExprNode* FunctionNode::Optimize()');assert b'iswithin' not in f[oa:oz].lower()
 registration=b'{"iswithin", IsWithinFunc, 3, 3}';ra=f.index(registration);ext.append(dict(file=None,path=cpp,startByte=ra,endByte=ra+len(registration),startLine=f[:ra].count(b'\n')+1,endLine=f[:ra].count(b'\n')+1,sha256=sha(registration),kind='registration'))
 source('LICENSE.md');source('Externals/poco/LICENSE');source('Externals/poco/Foundation/include/Poco/Types.h')
 cases=make_cases()
 assert json.loads((BASE/'cases.json').read_text('utf8'))==cases
 write(out/'cases.inc','\n'.join('run('+json.dumps(c['id'])+','+str(c['caseSensitive']).lower()+','+str(c['mode']=='optimizer-traversal').lower()+',{'+','.join(node(n) for n in c['args'])+'});' for c in cases)+'\n')
 write(out/'adapter.cpp',(BASE/'adapter.cpp').read_bytes())
 compiler=args.compiler.resolve();vcvars=args.vcvars.resolve();assert compiler.is_file() and vcvars.is_file()
 rsp=['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/Fe:'+str(out/'reference.exe'),str(out/'adapter.cpp')]
 write(out/'compile.rsp','\n'.join('"'+x+'"' for x in rsp));write(out/'compile.cmd','@echo off\ncall "'+str(vcvars)+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+str(compiler)+'" @compile.rsp\nexit /b %errorlevel%\n')
 cp=subprocess.run(['cmd','/c',str(out/'compile.cmd')],cwd=out,capture_output=True,timeout=120);write(out/'compile.stdout',cp.stdout);write(out/'compile.stderr',cp.stderr)
 if cp.returncode:raise RuntimeError(cp.stdout.decode(errors='replace')+cp.stderr.decode(errors='replace'))
 command=[str(out/'reference.exe')];started=datetime.datetime.now(datetime.timezone.utc).isoformat();proc=subprocess.Popen(command,cwd=out,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 class FT(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
 times=[FT() for _ in range(4)];handle=ctypes.c_void_p(int(proc._handle));assert ctypes.windll.kernel32.GetProcessTimes(handle,*[ctypes.byref(t) for t in times]);birth=(times[0].high<<32)|times[0].low
 timed=False
 try:stdout,stderr=proc.communicate(timeout=30)
 except subprocess.TimeoutExpired:timed=True;proc.kill();stdout,stderr=proc.communicate(timeout=10)
 signaled=ctypes.windll.kernel32.WaitForSingleObject(handle,0)==0
 write(out/'golden.ndjson',stdout);write(out/'reference.stderr',stderr)
 receipt=dict(sourceCommit=COMMIT,command=command,pid=proc.pid,birthFileTime=birth,heldProcessHandleSignaled=signaled,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=proc.returncode,timeout=timed,stdoutEof=True,stderrEof=True,stdoutSha256=sha(stdout),stderrSha256=sha(stderr),caseCount=len(cases),compilerPath=str(compiler),compilerSha256=sha(compiler.read_bytes()),executableSha256=sha((out/'reference.exe').read_bytes()))
 write(out/'receipt.json',json.dumps(receipt,indent=2)+'\n');assert not timed and not stderr and signaled and proc.returncode==0
 results=[json.loads(l) for l in stdout.splitlines()];assert [r['id'] for r in results]==[c['id'] for c in cases]
 for c,r in zip(cases,results):
  assert r['folded']==False
  if c['mode']=='optimizer-traversal':assert r['optimized']==list(range(len(c['args'])))
 write(out/'provenance.json',json.dumps(dict(commit=COMMIT,reusedArchivePath=str(zpath),reusedArchiveSha256=ZIP_SHA,sources=sources,extractions=ext,fullOptimizer=dict(path=cpp,startByte=oa,endByte=oz,startLine=f[:oa].count(b'\n')+1,endLine=f[:oz].count(b'\n')+1,sha256=sha(f[oa:oz]),isWithinBranchPresent=False),boundary='Only original optimizer child traversal is executed. Whole optimizer and parser are outside capture scope. Timestamp value/copy/dtor/comparison are actual fixed definitions; unsupported date formatting adapter path is never entered.'))+'\n')
 from make_public_plan import build
 public_plan=build(cases,results)
 write(out/'public-case-plan.json',json.dumps(public_plan,ensure_ascii=False,indent=2)+'\n')
 if (BASE/'public-case-plan.json').exists():assert public_plan==json.loads((BASE/'public-case-plan.json').read_text('utf8'))
 if (BASE/'golden.ndjson').exists():assert stdout==(BASE/'golden.ndjson').read_bytes()
 print(json.dumps(receipt))
if __name__=='__main__':main()
