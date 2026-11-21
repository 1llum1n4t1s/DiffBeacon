"""固定Native source/data reader。製品assemblyをimport/実行しない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,argparse,importlib.util
BASE=pathlib.Path(__file__).resolve().parent
SHA=lambda b:hashlib.sha256(b).hexdigest()

def verify():
 manifest=json.loads((BASE/'fixed-manifest.json').read_bytes())
 assert set(x['path'] for x in manifest['files'])=={p.name for p in BASE.iterdir() if p.is_file() and p.name!='fixed-manifest.json'}
 for item in manifest['files']:
  raw=(BASE/item['path']).read_bytes();assert len(raw)==item['size'] and SHA(raw)==item['sha256'],item['path']
 raw=(BASE/'golden.ndjson').read_bytes();cases=json.loads((BASE/'cases.json').read_bytes());golden=[json.loads(x) for x in raw.splitlines()]
 assert len(cases)==len(golden)==132 and [c['id'] for c in cases]==[g['id'] for g in golden] and len({c['id'] for c in cases})==132
 source_manifest=json.loads((BASE.parent/'LineTransformAggregates/source-manifest.json').read_bytes())
 prov=json.loads((BASE/'provenance.json').read_bytes());assert prov['commit']==source_manifest['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
 archive=BASE.parent/'LineTransformReplace/reference-source.zip'
 assert SHA(archive.read_bytes())==prov['reusedArchiveSha256']=='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
 originals={}
 with zipfile.ZipFile(archive) as z:
  assert z.testzip() is None
  for item in source_manifest['sources']:
   data=z.read('original/'+item['path']) if item['origin']=='sibling-fixed-reference-zip' else (BASE.parent/'LineTransformAggregates/additional-original'/item['path']).read_bytes()
   assert len(data)==item['size'] and SHA(data)==item['sha256']
   assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==item['blob']
   originals[item['path']]=data
 with zipfile.ZipFile(BASE/'native-proof.zip') as z:
  assert z.testzip() is None
  assert len(z.namelist())==len(set(z.namelist()))
  receipts=[]
  for run in ['run1','run2']:
   receipt=json.loads(z.read(run+'/receipt.json'));comp=json.loads(z.read(run+'/compile-receipt.json'));receipts.append(receipt)
   assert receipt['sourceCommit']==prov['commit'] and receipt['caseCount']==132
   assert receipt['exitCode']==comp['exitCode']==0 and receipt['heldProcessHandleSignaled'] and receipt['birthFileTime']>0 and receipt['pid']>0
   for r in [receipt,comp]:assert not r['timeout'] and r['stdoutEof'] and r['stderrEof']
   assert z.read(run+'/golden.ndjson')==raw and not z.read(run+'/reference.stderr')
   assert receipt['stdoutSha256']==SHA(raw) and receipt['stderrSha256']==SHA(b'')
   assert receipt['inputSha256']==SHA((BASE/'cases.json').read_bytes()) and receipt['adapterSha256']==SHA((BASE/'adapter.cpp').read_bytes())
   assert z.read(run+'/adapter.cpp')==(BASE/'adapter.cpp').read_bytes()
   assert json.loads(z.read(run+'/provenance.json'))==prov
   for name,digest in comp['inputSha256'].items():assert SHA(z.read(run+'/'+name))==digest
   for span in prov['extractions']:
    source=originals[span['path']];data=source[span['startByte']:span['endByte']]
    assert SHA(data)==span['sha256']
    assert source[:span['startByte']].count(b'\n')+1==span['startLine']
    if span['file']:assert z.read(run+'/'+span['file'])==data+(b'\n' if span.get('outerLf') else b'')
  assert receipts[0]['pid']!=receipts[1]['pid'] or receipts[0]['birthFileTime']!=receipts[1]['birthFileTime']
  for name in [n[5:] for n in z.namelist() if n.startswith('run1/') and n.endswith(('.inc','.cpp'))]:assert z.read('run1/'+name)==z.read('run2/'+name)
 for c,g in zip(cases,golden,strict=True):
  steps=g['steps'];assert len(steps)==2*max(1,len(c['rows']))
  for step in steps:
   assert step['outerLineIndexAfter']==step['row']
   if step['errorUtf8Hex']:assert step['value'] is None and step['serializedUtf8Hex'] is None
   else:assert step['value']['kind'] in ['none','integer','bool'] and isinstance(step['serializedUtf8Hex'],str)
  if c['id'].startswith('invalid-before-') and 'throws' not in c['id'] or c['id'].startswith('invalid-after-'):
   for s in steps:assert [t['node'] for t in s['trace']]==['before','after'] and s['cacheAfter']==0
  if c['id'].startswith('invalid-before-after-throws-'):
   for s in steps:assert [t['node'] for t in s['trace']]==['before','after'] and s['errorUtf8Hex'] and s['cacheAfter']==0
  if c['id']=='before-throws':
   for s in steps:assert [t['node'] for t in s['trace']]==['before'] and s['errorUtf8Hex']
  if c['mode']=='throw-scan':assert all(s['cacheAfter']==0 and s['errorUtf8Hex'] for s in steps)
  if c['mode']=='outer-throw-after-inner':assert all(s['cacheAfter']==1 and s['errorUtf8Hex'] for s in steps)
  if c['mode']=='scan' and c['rows'] and c['before']['type']=='integer' and c['after']['type']=='integer':
   for s in steps[1:]:assert not any(t['node']=='predicate' for t in s['trace'])
 spec=importlib.util.spec_from_file_location('match_plan',BASE/'generate-public-plan.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
 plan=json.loads((BASE/'public-case-plan.json').read_bytes());assert plan==module.plan()
 counts={k:sum(x['expectedOutcome']==k for x in plan) for k in ['serialized-key-comparison','runtime-rejection','native-only']}
 assert counts=={'serialized-key-comparison':91,'runtime-rejection':11,'native-only':30}
 return dict(nativeCases=132,sourceSpans=len(prov['extractions']),goldenSha256=SHA(raw),publicPlan=counts,typedPublicEquivalence=False,passed=True)

if __name__=='__main__':
 ap=argparse.ArgumentParser();ap.add_argument('--actual',type=pathlib.Path);a=ap.parse_args();summary=verify()
 if a.actual:assert a.actual.read_bytes()==(BASE/'golden.ndjson').read_bytes()
 print(json.dumps(summary))
