"""独立reader: 原典bytes/range、native proof全entry、全35件の意味を照合。compileしない。"""
import argparse,hashlib,json,pathlib,sys,zipfile
sys.dont_write_bytecode=True
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def text(v):
 if v is None:return 'none'
 if isinstance(v,bool):return str(v).lower()
 if isinstance(v,int):return str(v)
 if isinstance(v,float):return f'{v:.6f}'
 if isinstance(v,str):return v
 return '['+', '.join(map(text,v))+']'
def kind(v):
 if v is None:return 'none'
 if isinstance(v,bool):return 'bool'
 if isinstance(v,int):return 'integer'
 if isinstance(v,float):return 'double'
 if isinstance(v,str):return 'string'
 return 'array'
def hx(s):return s.encode().hex()
def expected(c):
 s=c['directives'];name='';cs=False;opt=True;pos=0;code=0;errpos=-1;message='';actual=s
 while True:
  while pos<len(s)and s[pos]in ' \t\n\r\v\f':pos+=1
  if pos==len(s)or s[pos]!='@':break
  end=pos+1;quoted=False
  while end<len(s):
   ch=s[end]
   if ch=='"':quoted=not quoted
   elif not quoted and ch in ' \t\n\r\v\f':break
   end+=1
  token=s[pos:end];key,sep,v=token[1:].partition('=');key=key.lower();valid=False
  if key=='name'and v:
   if v.startswith('"'):
    close=v.find('"',1)
    if close>=0:name=v[1:close];valid=True
   else:name=v;valid=True
  elif key in ['cs','casesensitive','ci','caseinsensitive','optimize','opt','nooptimize','noopt']and not v:
   if key in ['cs','casesensitive']:cs=True
   elif key in ['ci','caseinsensitive']:cs=False
   else:opt=key in ['optimize','opt']
   valid=True
  if not valid:code=14;errpos=len(s[:pos].encode());message='Invalid directive: '+token;break
  pos=end
 if not code:actual=s[pos:]
 logs=[]
 def ev(n):
  op=n['op']
  if op=='value':return n['value']
  if op=='throw':raise RuntimeError('fixture failure')
  if op=='if':
   cond=ev(n['args'][0]);truth=cond if isinstance(cond,bool)else(any(x is True for x in cond)if isinstance(cond,list)else False)
   return ev(n['args'][1 if truth else 2])
  parts=[];v=None
  for a in n['args']:v=ev(a);parts.append(text(v))
  logs.append(dict(level=n['level'],messageHex=hx(('['+name+'] 'if name else '')+' '.join(parts))))
  return v
 success=False;error='';v=None
 if not code:
  try:v=ev(c['node']);success=True
  except RuntimeError as e:error=str(e)
 return dict(id=c['id'],parsed=not bool(code),nameHex=hx(name),actualExpressionHex=hx(actual),caseSensitive=cs,optimize=opt,errorCode=code,errorPosition=errpos,directiveErrorHex=hx(message),success=success,exceptionHex=hx(error),resultKind=kind(v)if success else '',resultHex=hx(text(v))if success else '',logs=logs)
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--fixture',type=pathlib.Path,default=BASE);a=ap.parse_args();base=a.fixture.resolve()
 manifest=json.loads((base/'fixed-manifest.json').read_bytes());found={p.relative_to(base).as_posix()for p in base.rglob('*')if p.is_file()};assert found==set(manifest['files'])|{'fixed-manifest.json'}
 for path,entry in manifest['files'].items():
  b=(base/path).read_bytes();assert len(b)==entry['size']and sha(b)==entry['sha256'],path
 sources=json.loads((base/'source-manifest.json').read_bytes());source={}
 for s in sources['sources']:
  b=(base/'original'/s['path']).read_bytes();assert sha(b)==s['sha256']and len(b)==s['size'];assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['gitBlob'];source[pathlib.Path(s['path']).name]=b
 golden=(base/'golden.ndjson').read_bytes();cases=json.loads((base/'cases.json').read_bytes());actual=[json.loads(l)for l in golden.splitlines()];assert len(actual)==len(cases)==35;assert len({c['id']for c in cases})==35
 for c,r in zip(cases,actual):assert r==expected(c),(c['id'],r,expected(c))
 proof=json.loads((base/'native-proof-manifest.json').read_bytes())
 with zipfile.ZipFile(base/'native-proof.zip')as z:
  names=z.namelist();assert len(names)==len(set(names));assert set(names)==set(proof);assert z.testzip()is None
  for p,entry in proof.items():
   b=z.read(p);assert len(b)==entry['size']and sha(b)==entry['sha256'],p
  receipts=[]
  for run in ['run1','run2']:
   r=json.loads(z.read(run+'/receipt.json'));receipts.append(r)
   assert r['compileExitCode']==r['exitCode']==0 and r['heldProcessHandleSignaled']and not r['timeout']and r['stdoutEof']and r['stderrEof'];assert r['caseCount']==35
   assert z.read(run+'/golden.ndjson')==golden and r['stdoutSha256']==sha(golden);assert z.read(run+'/reference.stderr')==b''
   assert r['casesSha256']==sha((base/'cases.json').read_bytes());assert r['adapterSha256']==sha((base/'adapter.cpp').read_bytes());assert r['executableSha256']==sha(z.read(run+'/reference.exe'))
   for p,h in r['inputs'].items():assert sha(z.read(run+'/'+p))==h
   assert z.read(run+'/adapter.cpp')==(base/'adapter.cpp').read_bytes()
   provenance=json.loads(z.read(run+'/provenance.json'));assert provenance['commit']==sources['commit']
   for span in provenance['spans']:
    raw=source[span['source']];start=span['startByte'];end=span['endByte'];b=raw[start:end]
    assert sha(b)==span['sha256'];assert z.read(run+'/'+span['file'])==b+b'\n';assert raw[:start].count(b'\n')+1==span['startLine'];assert raw[:end].count(b'\n')+1==span['endLine']
  assert receipts[0]['inputs']==receipts[1]['inputs'];assert receipts[0]['pid']!=receipts[1]['pid'];assert receipts[0]['birthFileTime']!=receipts[1]['birthFileTime']
 print(json.dumps(dict(passed=True,cases=len(cases),nativeRuns=2,sourceFiles=len(source),goldenSha256=sha(golden),proofSha256=sha((base/'native-proof.zip').read_bytes()))))
if __name__=='__main__':main()
