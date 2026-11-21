"""Independent byte-slice reader. Does not import collector or managed code."""
import argparse,datetime,hashlib,json,pathlib
ROOT=pathlib.Path(__file__).resolve().parent
SPECS={'to-string.inc':('FilterExpressionNodes.cpp',[(2315,2357)]),'get-string.inc':('FilterExpressionNodes.cpp',[(51,58)]),'lifting.inc':('FilterExpressionNodes.cpp',[(1693,1704)]),'substr.inc':('FilterExpressionNodes.cpp',[(1871,1911)])}
def sha(b):return hashlib.sha256(b).hexdigest()
def load(p):return json.loads(p.read_text('utf-8'))
def validate_input(v):
 t=v['type'];assert t in ['none','int64','double','bool','string','array']
 if t=='int64':assert type(v['value']) is int and -(2**63)<=v['value']<2**63
 elif t=='double':assert type(v['value']) in [float,int]
 elif t=='bool':assert type(v['value']) is bool
 elif t=='string':assert bytes.fromhex(v['value']).hex()==v['value']
 elif t=='array':
  for x in v['items']:validate_input(x)
def text(v):
 t=v['type'];x=v.get('value')
 if t=='string':return bytes.fromhex(x)
 if t=='none':return b'none'
 if t=='bool':return b'true' if x else b'false'
 if t=='int64':return str(x).encode('ascii')
 if t=='double':return format(x,'.6f').encode('ascii')
 if t=='array':return b'['+b', '.join(text(i) for i in v['items'])+b']'
 raise AssertionError(t)
def model(c):
 trace=[];result={'type':'none'};error='';args=c['args']
 def evaluate(i):
  trace.append(i)
  if i in c['throws']:raise RuntimeError('probe-'+str(i))
  return args[i]
 try:
  if 2<=len(args)<=3:
   start=evaluate(1);length=evaluate(2) if len(args)==3 else {'type':'none'}
   if start['type']=='int64':
    source=evaluate(0)
    def scalar(v):
     if v['type']=='none':return {'type':'none'}
     data=text(v);s=start['value'];s=s+len(data) if s<0 else s
     if s<0 or s>=len(data):data=b''
     else:
      remaining=len(data)-s
      size=remaining if length['type']!='int64' else length['value']
      if size<0:size=max(0,remaining+size)
      data=data[s:s+size]
     return {'type':'string','value':data.hex()}
    result={'type':'array','items':[scalar(i) for i in source['items']]} if source['type']=='array' else scalar(source)
 except RuntimeError as ex:error=str(ex)
 return dict(id=c['id'],status='error' if error else 'ok',error=error,result=result,trace=trace)
def actual(raw,cases):
 rows=[json.loads(line) for line in raw.splitlines()];assert len(rows)==len(cases)
 for c,r in zip(cases,rows):assert r==model(c),(c['id'],r,model(c))
def verify(runs):
 manifest=load(ROOT/'manifest.json')['files'];assert set(manifest)=={str(p.relative_to(ROOT)).replace('\\','/') for p in ROOT.rglob('*') if p.is_file() and p.name!='manifest.json'}
 for name,m in manifest.items():
  data=(ROOT/name).read_bytes();assert len(data)==m['size'] and sha(data)==m['sha256'],name
 provenance=load(ROOT/'provenance.json');assert provenance['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
 for s in provenance['sources']:
  data=(ROOT/s['file']).read_bytes();assert sha(data)==s['sha256'] and len(data)==s['size'];assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==s['blob']
 cases=load(ROOT/'cases.json');assert len(cases)==268 and len({c['id'] for c in cases})==len(cases)
 for c in cases:
  assert all(type(i) is int and 0<=i<len(c['args']) for i in c['throws'])
  for v in c['args']:validate_input(v)
 golden=(ROOT/'golden.ndjson').read_bytes();actual(golden,cases)
 captures=load(ROOT/'capture-index.json')['runs'];seen=[]
 assert not runs or len(runs)==2,'supply both isolated run directories'
 for directory,capture in zip(runs,captures):
  directory=pathlib.Path(directory).resolve();receipt_bytes=(directory/'receipt.json').read_bytes();assert sha(receipt_bytes)==capture['receiptSha256'];r=json.loads(receipt_bytes)
  assert r['sourceCommit']==provenance['commit'] and r['caseCount']==len(cases)
  assert r['adapterSha256']==sha((ROOT/'adapter.cpp').read_bytes()) and r['casesSha256']==sha((ROOT/'cases.json').read_bytes())
  assert r['extraction']==json.loads(json.dumps(SPECS))
  for name,(name_source,ranges) in SPECS.items():
   lines=(ROOT/'original'/name_source).read_bytes().splitlines(keepends=True);expected=b''.join(b''.join(lines[a-1:b]) for a,b in ranges);assert (directory/name).read_bytes()==expected
  for name,h in r['includeHashes'].items():assert sha((directory/name).read_bytes())==h
  assert set(r['includeHashes'])==set(SPECS)|{'cases.inc'}
  assert sha((directory/'reference.exe').read_bytes())==r['executableSha256']
  compiler=pathlib.Path(r['compile']['command'][0]);assert sha(compiler.read_bytes())==r['compilerSha256']
  assert r['compile']['command'][1:5]==['/nologo','/std:c++20','/EHsc','/utf-8']
  assert r['compile']['command'][5:]==['/I'+str(directory),str(ROOT/'adapter.cpp'),'/Fo'+str(directory/'adapter.obj'),'/Fe'+str(directory/'reference.exe')]
  assert r['run']['command']==[str(directory/'reference.exe')]
  for prefix,key in [('compile','compile'),('reference','run')]:
   q=r[key];assert type(q['pid']) is int and q['pid']>0 and type(q['birthFileTime']) is int and q['birthFileTime']>0
   assert q['exitCode']==0 and all(q[k] is True for k in ['stdoutEof','stderrEof','heldProcessHandleSignaled'])
   assert datetime.datetime.fromisoformat(q['endedUtc'])>=datetime.datetime.fromisoformat(q['startedUtc'])
   for stream in ['stdout','stderr']:assert sha((directory/(prefix+'.'+stream)).read_bytes())==q[stream+'Sha256']
  out=(directory/'reference.stdout').read_bytes();assert out==golden and not (directory/'reference.stderr').read_bytes();actual(out,cases)
  seen.append((str(directory),r['run']['pid'],r['run']['birthFileTime'],r['compile']['pid'],r['compile']['birthFileTime']))
 if runs:
  assert seen[0][0]!=seen[1][0] and seen[0][1:3]!=seen[1][1:3] and seen[0][3:]!=seen[1][3:]
 print(json.dumps(dict(cases=len(cases),sourceBlobs=len(provenance['sources']),verifiedRuns=len(runs),scope='native function bodies; parser/optimizer/loader/scanner/public flt excluded')))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--run',action='append',default=[]);a=p.parse_args();verify(a.run)
