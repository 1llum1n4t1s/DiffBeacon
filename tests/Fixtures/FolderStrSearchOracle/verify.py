"""独立reader。managedコードやreproduce.pyをimportせずbyte検索と型・traceを照合。"""
import argparse,datetime,hashlib,json,pathlib
ROOT=pathlib.Path(__file__).resolve().parent
SPECS={'to-string.inc':('FilterExpressionNodes.cpp',[(2315,2357)]),'get-string.inc':('FilterExpressionNodes.cpp',[(51,58)]),'lifting.inc':('FilterExpressionNodes.cpp',[(1693,1704)]),'search.inc':('FilterExpressionNodes.cpp',[(2923,3031)]),'poco-ascii.inc':('Poco-Ascii.h',[(24,226)]),'poco-table.inc':('Poco-Ascii.cpp',[(18,154)]),'poco-lower.inc':('Poco-String.h',[(205,226)])}
def sha(b):return hashlib.sha256(b).hexdigest()
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def typed(t,v=None):return dict(type=t,**({'value':v} if v is not None else {}))
NONE=typed('none')
def stringify(v):
 t=v['type'];x=v.get('value')
 if t=='string':return bytes.fromhex(x)
 if t=='none':return b'none'
 if t=='bool':return b'true' if x else b'false'
 if t=='int64':return str(x).encode()
 if t=='double':return ('%.6f'%x).encode()
 if t=='array':return b'['+b', '.join(stringify(i) for i in v['items'])+b']'
 raise AssertionError(t)
def validate_input(v):
 t=v['type'];x=v.get('value')
 assert t in ['none','string','bool','int64','double','array']
 if t=='int64':assert type(x) is int and -(2**63)<=x<2**63
 if t=='double':assert type(x) is float
 if t=='bool':assert type(x) is bool
 if t=='string':assert type(x) is str and bytes.fromhex(x).hex()==x
 if t=='array':
  assert set(v)=={'type','items'}
  for item in v['items']:validate_input(item)
 else:assert set(v)==({'type'} if t=='none' else {'type','value'})
def model(c):
 args=c['args'];find=c['function']=='StrFind';trace=[];r=dict(id=c['id'],status='ok',error='',result=NONE,trace=trace)
 if c['nullArgs'] or (len(args) not in ([2,3] if find else [2])):return r
 for i in range(len(args)):
  trace.append(i)
  if i in c['throws']:r.update(status='error',error='probe-'+str(i));return r
 occurrence=args[2] if find and len(args)==3 else typed('int64',0)
 if find and occurrence['type']!='int64':return r
 n=args[1]
 if n['type']!='string' or not n['value']:return r
 needle=bytes.fromhex(n['value']);needle=needle if c['caseSensitive'] else needle.lower()
 def scalar(v):
  if v['type']=='none':return NONE
  text=stringify(v);text=text if c['caseSensitive'] else text.lower()
  positions=[];start=0
  while True:
   pos=text.find(needle,start)
   if pos<0:break
   positions.append(pos);start=pos+len(needle)
  if not find:return typed('int64',len(positions))
  n=occurrence['value'];return typed('int64',positions[n]) if 0<=n<len(positions) else NONE
 source=args[0];r['result']=dict(type='array',items=[scalar(x) for x in source['items']]) if source['type']=='array' else scalar(source)
 return r
def validate_output(v):
 assert isinstance(v,dict)
 if v['type']=='none':assert set(v)=={'type'}
 elif v['type']=='int64':assert set(v)=={'type','value'} and type(v['value']) is int and -(2**63)<=v['value']<2**63
 else:
  assert v['type']=='array' and set(v)=={'type','items'}
  for x in v['items']:validate_output(x)
def actual(data,cases):
 rows=[json.loads(l) for l in data.splitlines()];assert len(rows)==len(cases)
 for c,r in zip(cases,rows):
  assert set(r)=={'id','status','error','result','trace'};validate_output(r['result']);assert all(type(x) is int for x in r['trace']);assert r==model(c),(c['id'],r,model(c))
 return rows
def verify(runs):
 manifest=load(ROOT/'manifest.json')
 for name,s in manifest['files'].items():
  b=(ROOT/name).read_bytes();assert len(b)==s['size'] and sha(b)==s['sha256'],name
 provenance=load(ROOT/'provenance.json')
 for s in provenance['sources']:
  b=(ROOT/s['file']).read_bytes();assert sha(b)==s['sha256'] and len(b)==s['size'];assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob']
 source=(ROOT/'original/FilterExpressionNodes.cpp').read_bytes()
 for evidence in [b'{"strcount", StrCountFunc, 2, 2}',b'{"strfind", StrFindFunc, 2, 3}',b'functionName(Poco::toLower(name))',b'throw InvalidArgumentCountError',b'argCount < static_cast<size_t>(info->minArgs)',b'argCount > static_cast<size_t>(info->maxArgs)']:assert evidence in source
 cases=load(ROOT/'cases.json');assert cases and len({c['id'] for c in cases})==len(cases)
 for c in cases:
  assert c['function'] in ['StrCount','StrFind'] and type(c['caseSensitive']) is bool and type(c['nullArgs']) is bool
  assert all(type(i) is int and 0<=i<len(c['args']) for i in c['throws'])
  for a in c['args']:validate_input(a)
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
