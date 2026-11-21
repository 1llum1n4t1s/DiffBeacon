"""Independent raw-byte observer reader; never imports collector or managed code."""
import argparse,datetime,hashlib,json,pathlib,math,re,struct,zipfile
ROOT=pathlib.Path(__file__).resolve().parent
SPECS=json.loads((ROOT/'include-specs.json').read_text('utf-8'))
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
 def mapped(v,fn):return {'type':'array','items':[fn(i) for i in v['items']]} if v['type']=='array' else fn(v)
 def lower(data):return bytes(x+32 if 65<=x<=90 else x for x in data)
 def order(raw):return raw if sensitive else tuple(x if x<128 else x-256 for x in lower(raw))
 try:
  if op in ['ToInt','ToDouble','ToNumber']:
   def convert(v):
    if v['type']!='string':return typed('none')
    raw=text(v).replace(b',',b'')
    try:value=raw.decode('ascii')
    except UnicodeDecodeError:return typed('none')
    asdouble=op=='ToDouble' or op=='ToNumber' and any(ch in value for ch in '.eE')
    if not asdouble:
     if not re.fullmatch(r'[ \t\n\r\v\f]*[+-]?[0-9]+',value):return typed('none')
     number=int(value)
     return typed('int64',number) if -(2**63)<=number<2**63 else typed('none')
    if re.fullmatch(r'[ \t\n\r\v\f]*[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?',value):number=float(value)
    elif re.fullmatch(r'[ \t\n\r\v\f]*[+-]?0[xX][0-9a-fA-F]+(?:[pP][+-]?[0-9]+)?',value):number=float.fromhex(value.strip())
    else:return typed('none')
    if not math.isfinite(number) or number==0 and re.search(r'[1-9]',value.split('e')[0].split('E')[0]):return typed('none')
    return typed('double',struct.pack('>d',number).hex())
   result=mapped(evaluate(0),convert)
  elif op in ['Trim','TrimLeft','TrimRight']:
   def trim(v):
    if v['type']=='none':return typed('none')
    raw=text(v);raw=raw.lstrip(b' \t\r\n') if op!='TrimRight' else raw;raw=raw.rstrip(b' \t\r\n') if op!='TrimLeft' else raw
    return typed('string',raw.hex())
   result=mapped(evaluate(0),trim)
  elif op in ['InRange','IsWithin']:
   lo=evaluate(1);hi=evaluate(2);source=evaluate(0)
   def inside(v):
    if any(x['type']!='string' for x in [v,lo,hi]):return typed('none')
    a,b,z=map(lambda x:order(text(x)),[v,lo,hi]);return typed('bool',a>=b and (a<=z if op=='IsWithin' else a<z))
   result=mapped(source,inside)
  elif op in ['Contains','Matches','Recontains']:
   left=evaluate(0);right=evaluate(1);assert left['type']==right['type']=='string';raw=text(left);key=text(right)
   if op=='Contains':
    if not sensitive:raw=lower(raw);key=lower(key)
    answer=bool(key) and key in raw
   else:
    try:
     subject=raw.decode('utf-8',errors='strict');pattern=key.decode('utf-8',errors='strict');flags=0 if sensitive else re.IGNORECASE
     answer=bool(subject) and re.fullmatch(pattern,subject,flags) is not None if op=='Matches' else re.search(pattern,subject,flags) is not None
    except (UnicodeDecodeError,re.error):answer=False
   result=typed('bool',answer)
  else:raise AssertionError(op)
 except RuntimeError as ex:error=str(ex)
 return dict(id=c['id'],status='error' if error else 'ok',error=error,result=result,trace=trace)
def actual(raw,cases):
 rows=[json.loads(line) for line in raw.splitlines()];assert rows.pop(0)=={'environment':{'crtLocale':'C','signedChar':True}};assert len(rows)==len(cases)
 for c,r in zip(cases,rows):assert r==model(c),(c['id'],r,model(c))
def verify(runs):
 files=load(ROOT/'manifest.json')['files'];assert set(files)=={str(p.relative_to(ROOT)).replace('\\','/') for p in ROOT.rglob('*') if p.is_file() and p.name!='manifest.json'}
 for name,m in files.items():
  data=(ROOT/name).read_bytes();assert len(data)==m['size'] and sha(data)==m['sha256'],name
 provenance=load(ROOT/'archive-provenance.json');archive=zipfile.ZipFile(ROOT/'reference-source.zip');assert sha((ROOT/'reference-source.zip').read_bytes())==provenance['archiveSha256']
 assert provenance['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
 for entry in load(ROOT/'source-archive-manifest.json'):
  data=archive.read(entry['path']);assert len(data)==entry['size'] and sha(data)==entry['sha256']
 expected_sources={}
 for entry in provenance['sources']:
  key='original/'+entry['path'];data=archive.read(key);assert sha(data)==entry['sha256'] and len(data)==entry['size'];assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==entry['blob'];expected_sources[key]=sha(data)
 cases=load(ROOT/'cases.json');assert len(cases)==1042 and len({c['id'] for c in cases})==len(cases)
 for c in cases:
  for v in c['args']:validate_input(v)
 inputs=load(ROOT.parent/'malformed-inputs.json')['inputs'];assert len(inputs)==33
 for i in inputs:
  subset=[c for c in cases if c.get('sourceId')==i['sourceId']];assert len(subset)==27
  assert {c['operation'] for c in subset}=={'ToInt','ToDouble','ToNumber','Trim','TrimLeft','TrimRight','Contains','InRange','IsWithin','Matches','Recontains'}
 golden=(ROOT/'golden.ndjson').read_bytes();actual(golden,cases)
 captures=load(ROOT/'capture-index.json')['runs'];seen=[];assert not runs or len(runs)==2
 for directory,capture in zip(runs,captures):
  directory=pathlib.Path(directory).resolve();raw=(directory/'receipt.json').read_bytes();assert sha(raw)==capture['receiptSha256'];receipt=json.loads(raw)
  assert receipt['sourceCommit']==provenance['commit'] and receipt['archiveSha256']==provenance['archiveSha256'] and receipt['sourceHashes']==expected_sources and receipt['caseCount']==len(cases)
  assert receipt['adapterSha256']==sha((ROOT/'adapter.cpp').read_bytes()) and receipt['casesSha256']==sha((ROOT/'cases.json').read_bytes())
  assert receipt['extraction']==SPECS and set(receipt['includeHashes'])==set(SPECS)|{'cases.inc'}
  for name,entry in SPECS.items():
   data=archive.read(entry['source'][4:]) if entry['source'].startswith('zip:') else (ROOT/entry['source']).read_bytes();body=b''.join(data[a:b] for a,b in entry['ranges']);assert sha(body)==entry['sha256'];assert (directory/name).read_bytes()==body+b'\n'
  for name,h in receipt['includeHashes'].items():assert sha((directory/name).read_bytes())==h
  for name,h in expected_sources.items():
   if name.startswith('original/Externals/poco/dependencies/pcre2/src/'):assert sha((directory/name).read_bytes())==h
  assert sha((directory/'reference.exe').read_bytes())==receipt['executableSha256'];compiler=pathlib.Path(receipt['compile']['command'][0]);assert sha(compiler.read_bytes())==receipt['compilerSha256']
  cdir=directory/'original/Externals/poco/dependencies/pcre2/src'
  assert receipt['compileC']['command']==[str(compiler),'/nologo','/c','/O2','/DHAVE_CONFIG_H','/DPCRE2_STATIC','/I'+str(cdir),'/Fo'+str(directory/'pcre2.obj'),str(cdir/'pcre2.c')]
  assert receipt['compile']['command']==[str(compiler),'/nologo','/std:c++20','/EHsc','/utf-8','/DPCRE2_STATIC','/I'+str(cdir),'/I'+str(directory),str(ROOT/'adapter.cpp'),str(directory/'pcre2.obj'),'/Fo'+str(directory/'adapter.obj'),'/Fe'+str(directory/'reference.exe')]
  assert receipt['run']['command']==[str(directory/'reference.exe')]
  for prefix,key in [('compile-c','compileC'),('compile','compile'),('reference','run')]:
   q=receipt[key];assert type(q['pid']) is int and q['pid']>0 and q['birthFileTime']>0 and q['exitCode']==0
   assert all(q[k] is True for k in ['stdoutEof','stderrEof','heldProcessHandleSignaled']);assert datetime.datetime.fromisoformat(q['endedUtc'])>=datetime.datetime.fromisoformat(q['startedUtc'])
   for stream in ['stdout','stderr']:assert sha((directory/(prefix+'.'+stream)).read_bytes())==q[stream+'Sha256']
  assert (directory/'reference.stdout').read_bytes()==golden and not (directory/'reference.stderr').read_bytes();actual(golden,cases);seen.append((str(directory),receipt['run']['pid'],receipt['run']['birthFileTime']))
 if runs:assert seen[0][0]!=seen[1][0] and seen[0][1:]!=seen[1][1:]
 print(json.dumps({'cases':len(cases),'verifiedRuns':len(runs),'crtLocale':'C','glob':'separate follow-up; not covered here'}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--run',action='append',default=[]);args=p.parse_args();verify(args.run)
