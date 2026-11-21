"""Native process evidence + independent typed expectation reader (no product imports)."""
import argparse,datetime,hashlib,json,pathlib
ROOT=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def expected(c):
 result={'type':'none'};error='';trace=[]
 count=0 if c['nullArgs'] else len(c['args'])
 if count!=1:error='abs function requires at '+('least' if count<1 else 'most')+' 1 argument'
 else:
  trace=[0]
  if 0 in c['throws']:error='probe-0'
  else:
   def scalar(v):
    if v['type']=='int64':
     x=v['value'];return {'type':'int64','value':x if x==-(2**63) else abs(x)}
    if v['type']=='double':return {'type':'double','value':format(int(v['value'],16)&0x7fffffffffffffff,'016x')}
    return {'type':'none'}
   v=c['args'][0]
   result={'type':'array','items':[scalar(x) for x in v['items']]} if v['type']=='array' else scalar(v)
 return dict(id=c['id'],status='error' if error else 'ok',error=error,result=result,trace=trace)
def verify(evidence):
 manifest=json.loads((ROOT/'manifest.json').read_text())
 actual_files={p.relative_to(ROOT).as_posix() for p in ROOT.rglob('*') if p.is_file() and p.name!='manifest.json'}
 assert actual_files==set(manifest['files']),('file inventory',actual_files^set(manifest['files']))
 for name,h in manifest['files'].items():assert sha((ROOT/name).read_bytes())==h,name
 provenance=json.loads((ROOT/'provenance.json').read_text())
 for s in provenance['sources']:
  b=(ROOT/s['file']).read_bytes();assert len(b)==s['size'] and sha(b)==s['sha256']
  assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob']
 cases=json.loads((ROOT/'cases.json').read_text());gold=(ROOT/'golden.ndjson').read_bytes()
 rows=[json.loads(x) for x in gold.splitlines()]
 assert len(cases)==len(rows)==manifest['caseCount']==41
 assert len({c['id'] for c in cases})==len(cases)
 assert rows==[expected(c) for c in cases]
 index=json.loads((ROOT/'capture-index.json').read_text())
 assert len(index['runs'])==2 and len({x['runPid'] for x in index['runs']})==2
 if evidence:
  evidence=pathlib.Path(evidence)
  for item in index['runs']:
   run=evidence/item['name'];receipt_bytes=(run/'receipt.json').read_bytes();assert sha(receipt_bytes)==item['receiptSha256']
   receipt=json.loads(receipt_bytes)
   assert receipt['sourceCommit']==provenance['commit'] and receipt['caseCount']==len(cases)
   assert receipt['adapterSha256']==sha((ROOT/'adapter.cpp').read_bytes())
   assert receipt['casesSha256']==sha((ROOT/'cases.json').read_bytes())
   assert receipt['executableSha256']==sha((run/'reference.exe').read_bytes())
   assert receipt['compilerSha256']==sha(pathlib.Path(receipt['compile']['command'][0]).read_bytes())
   for name,h in receipt['includeHashes'].items():assert sha((run/name).read_bytes())==h
   for name,(source,ranges) in receipt['extraction'].items():
    lines=(ROOT/'original'/source).read_bytes().splitlines(keepends=True)
    assert (run/name).read_bytes()==b''.join(b''.join(lines[a-1:b]) for a,b in ranges)
   for key,prefix in [('compile','compile'),('run','reference')]:
    r=receipt[key]
    assert r['exitCode']==0 and r['pid']>0 and r['birthFileTime']>0
    assert all(r[x] for x in ['stdoutEof','stderrEof','heldProcessHandleSignaled'])
    assert datetime.datetime.fromisoformat(r['endedUtc'])>=datetime.datetime.fromisoformat(r['startedUtc'])
    for stream in ['stdout','stderr']:assert sha((run/(prefix+'.'+stream)).read_bytes())==r[stream+'Sha256']
   assert receipt['compile']['pid']==item['compilePid'] and receipt['run']['pid']==item['runPid']
   assert (run/'reference.stdout').read_bytes()==gold
   assert not (run/'reference.stderr').read_bytes()
   assert sha(gold)==item['stdoutSha256']
 print(json.dumps(dict(status='passed',cases=len(cases),rawEvidence=bool(evidence),runs=2 if evidence else 0,goldenSha256=sha(gold))))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--evidence');verify(p.parse_args().evidence)
