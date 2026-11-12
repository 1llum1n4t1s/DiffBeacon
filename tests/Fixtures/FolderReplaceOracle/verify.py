"""Independent bounded Replace reader; no collector/native import and no .NET replacement model."""
import argparse,datetime,hashlib,json,pathlib,re,subprocess,zipfile
ROOT=pathlib.Path(__file__).resolve().parent
SPECS=json.loads((ROOT/'include-specs.json').read_text('utf-8'))
def sha(b):return hashlib.sha256(b).hexdigest()
def load(p):return json.loads(p.read_text('utf-8'))
def none():return {'type':'none'}
def string(raw):return {'type':'string','value':raw.hex()}
def text(v):
 t=v['type'];x=v.get('value')
 if t=='none':return b'none'
 if t=='string':return bytes.fromhex(x)
 if t=='regex':return b'<Regex>'
 if t=='int64':return str(x).encode('ascii')
 if t=='double':return format(x,'.6f').encode('ascii')
 if t=='bool':return b'true' if x else b'false'
 if t=='array':return b'['+b', '.join(text(i) for i in v['items'])+b']'
 raise AssertionError(t)
class RegexError(Exception):pass
class GroupError(Exception):pass
def compile_regex(v):
 raw=bytes.fromhex(v['patternHex']);utf8=v['utf8']
 if utf8:
  try:pattern=raw.decode('utf-8',errors='strict')
  except UnicodeDecodeError:
   assert raw==b'\x80';raise RegexError('UTF-8 error: isolated byte with 0x80 bit set (at offset 0)')
 else:pattern=raw
 if raw==b'(':raise RegexError('missing closing parenthesis (at offset 1)')
 if raw==b'[':raise RegexError('missing terminating ] for character class (at offset 1)')
 pattern=pattern.replace('(?<g>','(?P<g>') if utf8 else pattern.replace(b'(?<g>',b'(?P<g>')
 try:return re.compile(pattern,0 if v['caseSensitive'] else re.IGNORECASE)
 except re.error as ex:raise AssertionError(('unmodeled bounded regex',v,str(ex)))
def replace_bytes(raw,compiled,utf8,replacement):
 offset=0;steps=0
 while offset<len(raw):
  steps+=1;assert steps<4096
  if utf8:
   try:prefix=raw[:offset].decode('utf-8',errors='surrogateescape');suffix=raw[offset:].decode('utf-8',errors='strict');subject=prefix+suffix;position=len(prefix)
   except UnicodeDecodeError:return None
  else:subject=raw;position=offset
  match=compiled.search(subject,position)
  if match is None:return raw
  def span_bytes(index):
   a,b=match.span(index)
   if a<0:return None
   return (len(subject[:a].encode('utf-8',errors='surrogateescape')),len(subject[:b].encode('utf-8',errors='surrogateescape'))) if utf8 else (a,b)
  start,end=span_bytes(0)
  if start>=len(raw):return raw
  participated=[i for i in range(1,compiled.groups+1) if match.span(i)!=(-1,-1)];rc=1+(max(participated) if participated else 0)
  replacement_out=bytearray();p=0
  while p<len(replacement):
   if replacement[p]==36:
    p+=1
    if p<len(replacement):
     char=replacement[p]
     if 48<=char<=57:
      group=char-48
      if group<rc:
       capture=span_bytes(group)
       if capture is None:raise GroupError('invalid string position')
       a,b=capture;replacement_out.extend(raw[a:b])
     else:replacement_out.extend(bytes([36,char]))
     p+=1
    else:replacement_out.append(36)
   else:replacement_out.append(replacement[p]);p+=1
  raw=raw[:start]+bytes(replacement_out)+raw[end if end>start else end+1:];offset=start+len(replacement_out)
 return raw

def model(c):
 trace=[];result=none();error='';error_type='';phase='binding';args=c['args']
 try:
  compiled={}
  for i,v in enumerate(args):
   if v['type']=='regex' and not v['lazy']:compiled[i]=compile_regex(v)
  phase='evaluate'
  if not c['nullArgs'] and len(args)==3:
   for i,v in enumerate(args):
    trace.append(i)
    if i in c['throws']:raise RuntimeError('probe-'+str(i))
    if v['type']=='regex' and v['lazy']:compiled[i]=compile_regex(v)
   source,key,to=args
   if key['type'] in ['string','regex'] and to['type']=='string':
    replacement=text(to)
    def scalar(v):
     if v['type']=='none':return none()
     raw=text(v)
     if key['type']=='string':
      if not key['value']:return none()
      try:
       pattern=text(key).decode('utf-8',errors='strict');native=re.compile(re.escape(pattern),0 if c['caseSensitive'] else re.IGNORECASE)
      except UnicodeDecodeError:return none()
      utf8=True
     else:native=compiled[1];utf8=key['utf8']
     out=replace_bytes(raw,native,utf8,replacement);return none() if out is None else string(out)
    result={'type':'array','items':[scalar(v) for v in source['items']]} if source['type']=='array' else scalar(source)
  phase=''
 except RegexError as ex:error=str(ex);error_type='Poco.RegularExpressionException';result=none()
 except GroupError as ex:error=str(ex);error_type='std.out_of_range';result=none()
 except RuntimeError as ex:error=str(ex);error_type='std.exception';result=none()
 return dict(id=c['id'],status='error' if error else 'ok',errorType=error_type,errorPhase=phase,errorHex=error.encode('utf-8').hex(),result=result,trace=trace)

def actual(raw,cases):
 rows=[json.loads(line) for line in raw.splitlines()];assert len(rows)==len(cases)
 for c,row in zip(cases,rows):
  assert row['id']==c['id'] and row['exitCode']==0 and row['stderrHex']=='',row
  output=bytes.fromhex(row['stdoutHex']);assert len(output.splitlines())==1;observed=json.loads(output);assert observed==model(c),(c['id'],observed,model(c))
def verify(runs,candidate=False):
 if not candidate:
  files=load(ROOT/'manifest.json')['files'];assert set(files)=={p.relative_to(ROOT).as_posix() for p in ROOT.rglob('*') if p.is_file() and p.name!='manifest.json'}
  for name,m in files.items():
   data=(ROOT/name).read_bytes();assert len(data)==m['size'] and sha(data)==m['sha256'],name
 prov=load(ROOT/'provenance.json');assert prov['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
 archive_path=ROOT/'reference-source.zip';assert sha(archive_path.read_bytes())==prov['archiveSha256'];archive=zipfile.ZipFile(archive_path);assert archive.testzip() is None
 entries=load(ROOT/'source-archive-manifest.json');assert set(archive.namelist())=={e['path'] for e in entries}
 for entry in entries:
  data=archive.read(entry['path']);assert sha(data)==entry['sha256'] and len(data)==entry['size']
 for entry in prov['sources']:
  data=archive.read('original/'+entry['path']);assert sha(data)==entry['sha256'] and len(data)==entry['size'];assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==entry['blob']
 for entry in prov['copiedSources']:
  data=(ROOT/entry['file']).read_bytes();assert sha(data)==entry['sha256'] and len(data)==entry['size'];assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==entry['blob']
 assert (ROOT/'original/Poco-LICENSE').read_bytes()==archive.read('original/Externals/poco/LICENSE')
 source_requests=''.join(prov['commit']+':'+e['path']+'\n' for e in prov['sources']).encode()
 lines=subprocess.check_output(['git','cat-file','--batch-check=%(objectname) %(objecttype) %(objectsize)'],input=source_requests,cwd=ROOT).decode().splitlines();assert len(lines)==len(prov['sources'])
 for entry,line in zip(prov['sources'],lines):assert line.split()==[entry['blob'],'blob',str(entry['size'])]
 pcre={name:sha(archive.read(name)) for name in archive.namelist() if name.startswith('original/Externals/poco/dependencies/pcre2/src/')}
 cases=load(ROOT/'cases.json');assert len(cases)==269 and len({c['id'] for c in cases})==len(cases)
 assert sum(any(v['type']=='regex' for v in c['args']) for c in cases)==158
 inputs=load(ROOT/'malformed-inputs.json')['inputs'];assert len(inputs)==33
 for item in inputs:
  subset=[c for c in cases if c.get('sourceId')==item['sourceId']];assert len(subset)==4
  assert all(c['args'][0].get('value')==item['hex'] for c in subset if c['id'].startswith('raw-') and not c['id'].startswith('raw-text-pattern-'))
  assert next(c for c in subset if c['id'].startswith('raw-text-pattern-'))['args'][1]['value']==item['hex']
 assert len({c.get('sourceId') for c in cases if c.get('sourceId')})==33
 assert not runs or len(runs)==2
 if candidate:
  assert len(runs)==2;golden=(pathlib.Path(runs[0])/'raw-cases.ndjson').read_bytes();captures=[None,None]
 else:golden=(ROOT/'golden.ndjson').read_bytes();captures=load(ROOT/'capture-index.json')['runs']
 actual(golden,cases);seen=[]
 if not candidate:
  paths=[p for p in ROOT.rglob('*') if p.is_file()];relative=[p.relative_to(ROOT.parents[2]).as_posix() for p in paths]
  rawattrs=subprocess.check_output(['git','check-attr','-z','--stdin','text'],input=b'\0'.join(x.encode() for x in relative)+b'\0',cwd=ROOT).split(b'\0');assert rawattrs.pop()==b''
  assert len(rawattrs)==len(paths)*3
  for i,name in enumerate(relative):assert rawattrs[i*3:i*3+3]==[name.encode(),b'text',b'unset']
  for path,name in zip(paths,relative):
   data=path.read_bytes();blob=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest();assert subprocess.check_output(['git','hash-object','--path',name,'--stdin'],input=data,cwd=ROOT).decode().strip()==blob
 for directory,capture in zip(runs,captures):
  directory=pathlib.Path(directory).resolve();data=(directory/'receipt.json').read_bytes();assert candidate or sha(data)==capture['receiptSha256'];r=json.loads(data)
  assert r['sourceCommit']==prov['commit'] and r['caseCount']==len(cases) and r['pcreSources']==pcre and r['extraction']==SPECS
  assert r['adapterSha256']==sha((ROOT/'adapter.cpp').read_bytes()) and r['casesSha256']==sha((ROOT/'cases.json').read_bytes())
  assert set(r['includeHashes'])==set(SPECS)|{'cases.inc'}
  for name,entry in SPECS.items():
   data=archive.read(entry['source'][4:]) if entry['source'].startswith('zip:') else (ROOT/entry['source']).read_bytes();body=b''.join(data[a:b] for a,b in entry['ranges']);assert sha(body)==entry['sha256'] and (directory/name).read_bytes()==body+b'\n'
  for name,h in r['includeHashes'].items():assert sha((directory/name).read_bytes())==h
  for name,h in pcre.items():assert sha((directory/name).read_bytes())==h
  assert sha((directory/'reference.exe').read_bytes())==r['executableSha256'] and sha((directory/'pcre2.obj').read_bytes())==r['pcreObjectSha256']
  compiler=pathlib.Path(r['compile']['command'][0]);assert sha(compiler.read_bytes())==r['compilerSha256'];cdir=directory/'original/Externals/poco/dependencies/pcre2/src'
  assert r['compileC']['command']==[str(compiler),'/nologo','/c','/O2','/DHAVE_CONFIG_H','/DPCRE2_STATIC','/I'+str(cdir),'/Fo'+str(directory/'pcre2.obj'),str(cdir/'pcre2.c')]
  assert r['compile']['command']==[str(compiler),'/nologo','/std:c++20','/EHsc','/utf-8','/DPCRE2_STATIC','/I'+str(cdir),'/I'+str(directory),str(ROOT/'adapter.cpp'),str(directory/'pcre2.obj'),'/Fo'+str(directory/'adapter.obj'),'/Fe'+str(directory/'reference.exe')]
  assert len(r['runs'])==len(cases);identities=[]
  for prefix,q in [('compile-c',r['compileC']),('compile',r['compile'])]+[('case-'+str(i).zfill(4),q) for i,q in enumerate(r['runs'])]:
   assert q['exitCode']==0 and q['pid']>0 and q['birthFileTime']>0 and all(q[k] is True for k in ['stdoutEof','stderrEof','heldProcessHandleSignaled']);assert datetime.datetime.fromisoformat(q['endedUtc'])>=datetime.datetime.fromisoformat(q['startedUtc'])
   for stream in ['stdout','stderr']:assert sha((directory/(prefix+'.'+stream)).read_bytes())==q[stream+'Sha256']
  for i,q in enumerate(r['runs']):
   assert q['command']==[str(directory/'reference.exe'),str(i)];identities.append((q['pid'],q['birthFileTime']))
  assert len(set(identities))==len(cases)
  raw=(directory/'raw-cases.ndjson').read_bytes();assert sha(raw)==r['rawCasesSha256'] and raw==golden;actual(raw,cases)
  for i,row in enumerate(map(json.loads,raw.splitlines())):
   prefix='case-'+str(i).zfill(4);assert row['stdoutHex']==(directory/(prefix+'.stdout')).read_bytes().hex() and row['stderrHex']==(directory/(prefix+'.stderr')).read_bytes().hex() and row['exitCode']==r['runs'][i]['exitCode']
  seen.append((str(directory),set(identities)))
 if runs:assert seen[0][0]!=seen[1][0] and not seen[0][1].intersection(seen[1][1])
 print(json.dumps({'cases':len(cases),'verifiedRuns':len(runs),'nativeCaseProcesses':len(cases)*len(runs),'nativeFaults':0,'scope':'native Replace body with typed Regex; no public managed syntax claim'}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--run',action='append',default=[]);p.add_argument('--candidate-run',action='append',default=[]);args=p.parse_args();assert not(args.run and args.candidate_run);verify(args.candidate_run or args.run,bool(args.candidate_run))
