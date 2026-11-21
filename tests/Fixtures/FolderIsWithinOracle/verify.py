"""Native process evidence + independent typed expectation reader (no product imports)."""
import argparse,datetime,hashlib,json,pathlib,struct
ROOT=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def expected(c):
 result={'type':'none'};error='';trace=[]
 count=0 if c['nullArgs'] else len(c['args'])
 if count!=3:error='iswithin function requires at '+('least' if count<3 else 'most')+' 3 arguments'
 else:
  for index in [1,2,0]:
   trace.append(index)
   if index in c['throws']:
    error='probe-'+str(index);break
  if not error:
   lo,hi=c['args'][1:]
   def binary64(v):return struct.unpack('>d',bytes.fromhex(v['value']))[0] if v['type']=='double' else float(v['value'])
   def scalar(v):
    t=v['type'];a,b=lo['type'],hi['type']
    valid=False;inside=False
    if t=='int64' and a in ('int64','double') and b in ('int64','double'):
     valid=True
     left=v['value']>=lo['value'] if a=='int64' else float(v['value'])>=binary64(lo)
     right=v['value']<=hi['value'] if b=='int64' else float(v['value'])<=binary64(hi)
     inside=left and right
    elif t=='double' and a in ('int64','double') and b in ('int64','double'):
     valid=True;inside=binary64(v)>=binary64(lo) and binary64(v)<=binary64(hi)
    elif t=='timestamp' and a==b=='timestamp':
     valid=True;inside=lo['value']<=v['value']<=hi['value']
    elif t=='string' and a==b=='string':
     def key(s):
      raw=s.encode('utf-8')
      if c['caseSensitive']:return tuple(raw)
      # Pinned Poco icompare casts ASCII-folded bytes back to char.
      # Captured MSVC x64 ABI has signed char; this is not Unicode folding.
      folded=[n+32 if 65<=n<=90 else n for n in raw]
      return tuple(n-256 if n>=128 else n for n in folded)
     valid=True;inside=key(lo['value'])<=key(v['value'])<=key(hi['value'])
    return {'type':'bool','value':inside} if valid else {'type':'none'}
   v=c['args'][0]
   result={'type':'array','items':[scalar(x) for x in v['items']]} if v['type']=='array' else scalar(v)
 return dict(id=c['id'],status='error' if error else 'ok',error=error,result=result,trace=trace)

def verify(evidence):
 manifest=json.loads((ROOT/'manifest.json').read_text(encoding='utf-8'))
 actual_files={p.relative_to(ROOT).as_posix() for p in ROOT.rglob('*') if p.is_file() and p.name!='manifest.json'}
 assert actual_files==set(manifest['files']),('file inventory',actual_files^set(manifest['files']))
 for name,h in manifest['files'].items():assert sha((ROOT/name).read_bytes())==h,name
 provenance=json.loads((ROOT/'provenance.json').read_text(encoding='utf-8'))
 for s in provenance['sources']:
  b=(ROOT/s['file']).read_bytes();assert len(b)==s['size'] and sha(b)==s['sha256']
  assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob']
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'));gold=(ROOT/'golden.ndjson').read_bytes()
 rows=[json.loads(x) for x in gold.splitlines()]
 assert len(cases)==len(rows)==manifest['caseCount']==159
 assert len({c['id'] for c in cases})==len(cases)
 assert rows==[expected(c) for c in cases]
 by_id={r['id']:r for r in rows}
 for name in ['int-upper','int-equal','timestamp-upper','double-positive-inf','string-upper-cs','string-upper-ci']:
  assert by_id[name]['result']=={'type':'bool','value':True},name
 for row in rows:
  if row['id'].startswith('singleton-'):assert row['result']=={'type':'bool','value':True},row['id']
 assert by_id['string-highbit-order-cs']['result']=={'type':'bool','value':False}
 assert by_id['string-highbit-order-ci']['result']=={'type':'bool','value':True}
 index=json.loads((ROOT/'capture-index.json').read_text(encoding='utf-8'))
 assert len(index['runs'])==2 and len({x['runPid'] for x in index['runs']})==2
 if evidence:
  evidence=pathlib.Path(evidence)
  for item in index['runs']:
   run=evidence/item['name'];receipt_bytes=(run/'receipt.json').read_bytes();assert sha(receipt_bytes)==item['receiptSha256']
   receipt=json.loads(receipt_bytes)
   assert receipt['sourceCommit']==provenance['commit'] and receipt['caseCount']==len(cases)
   assert receipt['adapterSha256']==sha((ROOT/'adapter.cpp').read_bytes())
   for relative,h in receipt['sourceSnapshots'].items():
    assert sha((run/'source'/relative).read_bytes())==h==sha((ROOT/relative).read_bytes())
   assert receipt['casesSha256']==sha((ROOT/'cases.json').read_bytes())
   assert receipt['executableSha256']==sha((run/'reference.exe').read_bytes())
   assert receipt['compilerSha256']==sha(pathlib.Path(receipt['compile']['command'][0]).read_bytes())
   for name,h in receipt['includeHashes'].items():assert sha((run/name).read_bytes())==h
   for name,(source,ranges) in receipt['extraction'].items():
    lines=(ROOT/'original'/source).read_bytes().splitlines(keepends=True)
    assert (run/name).read_bytes()==b''.join(b''.join(lines[a-1:b]) for a,b in ranges)
   for key,prefix in [('compile','compile'),('run','reference')]:
    r=receipt[key]
    assert json.loads((run/(prefix+'.process.json')).read_bytes())==r
    assert r['exitCode']==0 and r['pid']>0 and r['birthFileTime']>0
    assert all(r[x] for x in ['stdoutEof','stderrEof','heldProcessHandleSignaled'])
    assert datetime.datetime.fromisoformat(r['endedUtc'])>=datetime.datetime.fromisoformat(r['startedUtc'])
    for stream in ['stdout','stderr']:assert sha((run/(prefix+'.'+stream)).read_bytes())==r[stream+'Sha256']
   assert receipt['compile']['pid']==item['compilePid'] and receipt['run']['pid']==item['runPid']
   assert (run/'reference.stdout').read_bytes()==gold
   abi=json.loads((run/'reference.stderr').read_bytes());assert abi==receipt['abi']==item['abi']
   assert abi['charSigned'] is True and abi['pointerBytes']==8 and abi['x64'] is True and abi['msvcVersion']>=1900
   assert '/hostx64/x64/' in receipt['compile']['command'][0].replace('\\','/').lower()
   assert sha(gold)==item['stdoutSha256']
 print(json.dumps(dict(status='passed',cases=len(cases),rawEvidence=bool(evidence),runs=2 if evidence else 0,goldenSha256=sha(gold))))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--evidence');verify(p.parse_args().evidence)
