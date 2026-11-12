"""Independent raw-byte observer reader; never imports collector or managed code."""
import argparse,datetime,hashlib,json,pathlib
ROOT=pathlib.Path(__file__).resolve().parent
SPECS={'to-string.inc': ('FilterExpressionNodes.cpp', [(2315, 2357)]), 'get-string.inc': ('FilterExpressionNodes.cpp', [(51, 58)]), 'lifting.inc': ('FilterExpressionNodes.cpp', [(1693, 1704)]), 'equal.inc': ('FilterExpressionNodes.cpp', [(1752, 1801)]), 'lengths.inc': ('FilterExpressionNodes.cpp', [(1836, 1870)]), 'substr.inc': ('FilterExpressionNodes.cpp', [(1871, 1911)]), 'search.inc': ('FilterExpressionNodes.cpp', [(2923, 3031)]), 'compute.inc': ('FilterExpressionNodes.cpp', [(581, 845)]), 'binary-evaluate.inc': ('FilterExpressionNodes.cpp', [(847, 852)]), 'poco-ascii.inc': ('Poco-Ascii.h', [(24, 226)]), 'poco-table.inc': ('Poco-Ascii.cpp', [(18, 154)]), 'poco-string.inc': ('Poco-String.h', [(205, 226), (286, 309)]), 'utf8-length.inc': ('unicoder.cpp', [(112, 122), (145, 156)]), 'tokens.inc': ('FilterParser.h', [(1, 32)])}
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
def typed(t,value=None):return {'type':t,**({'value':value} if t!='none' else {})}
def byte_length(data):
 i=count=0
 while i<len(data):
  b=data[i];width=1 if b<0xc0 or b>=0xfe else 2 if b<0xe0 else 3 if b<0xf0 else 4 if b<0xf8 else 5 if b<0xfc else 6
  i+=width;count+=1
 return count
def model(c):
 trace=[];result=typed('none');error='';args=c['args'];op=c['operation'];sensitive=c['caseSensitive']
 def evaluate(i):
  trace.append(i)
  if i in c['throws']:raise RuntimeError('probe-'+str(i))
  return args[i]
 def equal(a,b):
  if a['type']!=b['type']:return False
  if a['type']=='array':return len(a['items'])==len(b['items']) and all(equal(x,y) for x,y in zip(a['items'],b['items']))
  return a==b
 def mapped(v,fn):return {'type':'array','items':[fn(i) for i in v['items']]} if v['type']=='array' else fn(v)
 def lowered(data):return bytes(b+32 if 65<=b<=90 else b for b in data)
 try:
  if op=='AllEqual':
   first=evaluate(0)
   if first['type']=='array':
    seq=first['items'];answer=all(equal(seq[0],item) for item in seq[1:]) if seq else True
   else:
    answer=True
    for i in range(1,len(args)):
     if not equal(first,evaluate(i)):answer=False;break
   result=typed('bool',answer)
  elif op in ['Eq','Ne','Lt','Le','Gt','Ge','Plus']:
   left=evaluate(0);right=evaluate(1);assert left['type']==right['type']=='string'
   a=text(left);b=text(right)
   if op=='Plus':result=typed('string',(a+b).hex())
   else:
    if not sensitive:
     a=tuple(x if x<128 else x-256 for x in lowered(a));b=tuple(x if x<128 else x-256 for x in lowered(b))
    answer={'Eq':a==b,'Ne':a!=b,'Lt':a<b,'Le':a<=b,'Gt':a>b,'Ge':a>=b}[op];result=typed('bool',answer)
  elif op=='ToString':result=typed('string',text(evaluate(0)).hex())
  elif op=='Len':
   v=evaluate(0);t=v['type'];size=len(v['items']) if t=='array' else byte_length(text(v)) if t=='string' else 0 if t=='none' else 1
   result=typed('int64',size)
  elif op=='Strlen':result=mapped(evaluate(0),lambda v:typed('none') if v['type']=='none' else typed('int64',byte_length(text(v))))
  elif op=='Substr':
   if 2<=len(args)<=3:
    start=evaluate(1);length=evaluate(2) if len(args)==3 else typed('none')
    if start['type']=='int64':
     source=evaluate(0)
     def slice_value(v):
      if v['type']=='none':return typed('none')
      raw=text(v);position=start['value'];position=position+len(raw) if position<0 else position
      if position<0 or position>=len(raw):out=b''
      else:
       remaining=len(raw)-position;size=remaining if length['type']!='int64' else length['value'];size=max(0,remaining+size) if size<0 else size;out=raw[position:position+size]
      return typed('string',out.hex())
     result=mapped(source,slice_value)
  elif op in ['StrFind','StrCount']:
   if (op=='StrFind' and 2<=len(args)<=3) or (op=='StrCount' and len(args)==2):
    source=evaluate(0);needle=evaluate(1);occurrence=evaluate(2) if len(args)==3 else typed('int64',0)
    if occurrence['type']=='int64' and needle['type']=='string' and needle['value']:
     key=text(needle);key=key if sensitive else lowered(key)
     def search(v):
      if v['type']=='none':return typed('none')
      raw=text(v);raw=raw if sensitive else lowered(raw);position=count=0;found=[]
      while True:
       offset=raw.find(key,position)
       if offset<0:break
       found.append(offset);position=offset+len(key)
      if op=='StrCount':return typed('int64',len(found))
      index=occurrence['value'];return typed('int64',found[index]) if 0<=index<len(found) else typed('none')
     result=mapped(source,search)
  else:raise AssertionError(op)
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
 cases=load(ROOT/'cases.json');assert len(cases)==1226 and len({c['id'] for c in cases})==len(cases)
 for c in cases:
  assert all(type(i) is int and 0<=i<len(c['args']) for i in c['throws'])
  for v in c['args']:validate_input(v)
 malformed=load(ROOT/'malformed-inputs.json');inputs=malformed['inputs'];assert len(inputs)==33 and len({i['hex'] for i in inputs})==26
 source=ROOT.parent/malformed['source'];source_bytes=source.read_bytes();assert sha(source_bytes)==malformed['sourceSha256']
 source_rows={r['id']:r for r in map(json.loads,source_bytes.splitlines())}
 for i in inputs:
  raw=bytes.fromhex(i['hex']);assert source_rows[i['sourceId']]['result']==typed('string',i['hex'])
  try:raw.decode('utf-8',errors='strict')
  except UnicodeDecodeError:pass
  else:raise AssertionError('input is valid UTF-8')
  subset=[c for c in cases if c.get('sourceId')==i['sourceId']]
  assert len(subset)==30 and {c['operation'] for c in subset}=={'AllEqual','Eq','Ne','Lt','Le','Gt','Ge','Len','Strlen','StrFind','StrCount','Substr','ToString','Plus'}
  assert all(any(v.get('value')==i['hex'] for v in c['args']) or c['id'].endswith(('equal-nested','nested-Strlen','nested-stringify')) or c['operation'] in ['StrFind','StrCount'] for c in subset)
 collisions=load(ROOT/'collision-inputs.json');assert collisions
 for index,pair in enumerate(collisions):
  left=bytes.fromhex(pair['left']);right=bytes.fromhex(pair['right']);assert left!=right
  assert left.decode('utf-8',errors='replace')==right.decode('utf-8',errors='replace')==bytes.fromhex(pair['replacementUtf8']).decode('utf-8')
  assert pair['left'] in {i['hex'] for i in inputs} and pair['right'] in {i['hex'] for i in inputs}
  subset=[c for c in cases if c.get('collisionIndex')==index];assert len(subset)==13
  assert all(c['args']==[typed('string',pair['left']),typed('string',pair['right'])] for c in subset)
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
