"""Independent bounded Poco iterator/glob model; never imports collector or native code."""
import argparse,datetime,hashlib,json,pathlib,zipfile
ROOT=pathlib.Path(__file__).resolve().parent
SPECS=json.loads((ROOT/'include-specs.json').read_text('utf-8'))
def sha(b):return hashlib.sha256(b).hexdigest()
def load(p):return json.loads(p.read_text('utf-8'))
def tokens(raw):
 output=[];index=0
 while index<len(raw):
  first=raw[index];width=1 if first<0xc0 or first>=0xf8 else 2 if first<0xe0 else 3 if first<0xf0 else 4;part=raw[index:index+width]
  try:
   decoded=part.decode('utf-8',errors='strict');value=ord(decoded) if len(decoded)==1 else -1
  except UnicodeDecodeError:value=-1
  output.append(value);index+=width
 return output
def model(c):
 pattern=tokens(bytes.fromhex(c['patternHex']));subject=tokens(bytes.fromhex(c['subjectHex']));sensitive=c['caseSensitive']
 def lower(ch):
  if sensitive or ch<0:return ch
  value=chr(ch).lower();return ord(value) if len(value)==1 else ch
 def matchset(p,ch):
  ch=lower(ch)
  while p<len(pattern):
   if pattern[p]==93:return False,p+1
   if pattern[p]==92:
    p+=1
    if p==len(pattern):raise RuntimeError('backslash must be followed by character in glob pattern')
   first=last=pattern[p];p+=1
   if p<len(pattern) and pattern[p]==45:
    p+=1
    if p==len(pattern):raise RuntimeError('bad range syntax in glob pattern')
    last=pattern[p];p+=1
   first=lower(first);last=lower(last)
   if first<=ch<=last:
    while p<len(pattern):
     if pattern[p]==93:return True,p+1
     if pattern[p]==92:
      p+=1
      if p==len(pattern):break
     p+=1
    raise RuntimeError('range must be terminated by closing bracket in glob pattern')
  return False,p
 def match(p,s):
  while p<len(pattern):
   if s==len(subject):
    while p<len(pattern) and pattern[p]==42:p+=1
    break
   ch=pattern[p]
   if ch==63:p+=1;s+=1
   elif ch==42:
    p+=1
    if p<len(pattern):
     while s<len(subject) and not match(p,s):s+=1
     return s!=len(subject)
    return True
   elif ch==91:
    p+=1
    if p<len(pattern):
     invert=pattern[p]==33
     if invert:p+=1
     if p<len(pattern):
      found,p=matchset(p,subject[s]);s+=1
      if invert and found or not invert and not found:return False
      continue
    raise RuntimeError('bad range syntax in glob pattern')
   else:
    if ch==92:
     p+=1
     if p==len(pattern):raise RuntimeError('backslash must be followed by character in glob pattern')
    if lower(pattern[p])!=lower(subject[s]):return False
    p+=1;s+=1
  return p==len(pattern) and s==len(subject)
 result=False;error=''
 try:result=match(0,0)
 except RuntimeError as ex:error=str(ex)
 return {'id':c['id'],'status':'error' if error else 'ok','error':error,'result':result}
def actual(raw,cases):
 rows=[json.loads(line) for line in raw.splitlines()];assert len(rows)==len(cases)
 for c,row in zip(cases,rows):
  assert row['id']==c['id'] and row['exitCode']==0 and row['stderrHex']=='',row
  output=bytes.fromhex(row['stdoutHex']);assert len(output.splitlines())==1;observed=json.loads(output);assert observed==model(c),(c['id'],observed,model(c))
def verify(runs):
 files=load(ROOT/'manifest.json')['files'];assert set(files)=={str(p.relative_to(ROOT)).replace('\\','/') for p in ROOT.rglob('*') if p.is_file() and p.name!='manifest.json'}
 for name,m in files.items():
  data=(ROOT/name).read_bytes();assert len(data)==m['size'] and sha(data)==m['sha256'],name
 prov=load(ROOT/'provenance.json');assert prov['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
 for entry in prov['sources']:
  data=(ROOT/entry['file']).read_bytes();assert sha(data)==entry['sha256'] and len(data)==entry['size'];assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==entry['blob']
 archive_path=ROOT/prov['pcreArchive'];assert sha(archive_path.read_bytes())==prov['pcreArchiveSha256'];archive=zipfile.ZipFile(archive_path);pcre={name:sha(archive.read(name)) for name in archive.namelist() if name.startswith('original/Externals/poco/dependencies/pcre2/src/')}
 cases=load(ROOT/'glob-cases.json');assert len(cases)==756 and len({c['id'] for c in cases})==len(cases)
 inputs=load(ROOT.parent/'malformed-inputs.json')['inputs'];assert len(inputs)==33
 for item in inputs:
  subset=[c for c in cases if c.get('sourceId')==item['sourceId']];assert len(subset)==22 and {c['caseSensitive'] for c in subset}=={True,False}
 golden=(ROOT/'glob-golden.ndjson').read_bytes();actual(golden,cases);captures=load(ROOT/'capture-index.json')['runs'];assert not runs or len(runs)==2;seen=[]
 for directory,capture in zip(runs,captures):
  directory=pathlib.Path(directory).resolve();data=(directory/'receipt.json').read_bytes();assert sha(data)==capture['receiptSha256'];r=json.loads(data)
  assert r['sourceCommit']==prov['commit'] and r['caseCount']==len(cases) and r['pcreSources']==pcre and r['extraction']==SPECS
  assert r['adapterSha256']==sha((ROOT/'adapter.cpp').read_bytes()) and r['casesSha256']==sha((ROOT/'glob-cases.json').read_bytes())
  assert set(r['includeHashes'])==set(SPECS)|{'glob-cases.inc'}
  for name,entry in SPECS.items():
   data=(ROOT/entry['source']).read_bytes();body=b''.join(data[a:b] for a,b in entry['ranges']);assert sha(body)==entry['sha256'] and (directory/name).read_bytes()==body+b'\n'
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
 print(json.dumps({'cases':len(cases),'verifiedRuns':len(runs),'nativeCaseProcesses':len(cases)*len(runs),'nativeFaults':0,'scope':'bounded Glob iterator/Unicode paths; not full .flt equivalence'}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--run',action='append',default=[]);args=p.parse_args();verify(args.run)
