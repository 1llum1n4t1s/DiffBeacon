"""固定Native source/data reader。製品assemblyをimport/実行しない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,argparse,importlib.util,subprocess
BASE=pathlib.Path(__file__).resolve().parent
SHA=lambda b:hashlib.sha256(b).hexdigest()

def verify(check_git=False):
 manifest=json.loads((BASE/'fixed-manifest.json').read_bytes())
 assert set(x['path'] for x in manifest['files'])=={p.name for p in BASE.iterdir() if p.is_file() and p.name!='fixed-manifest.json'}
 for item in manifest['files']:
  raw=(BASE/item['path']).read_bytes();assert len(raw)==item['size'] and SHA(raw)==item['sha256'],item['path']
 raw=(BASE/'golden.ndjson').read_bytes();cases=json.loads((BASE/'cases.json').read_bytes());golden=[json.loads(x) for x in raw.splitlines()]
 assert len(cases)==len(golden)==59 and [c['id'] for c in cases]==[g['id'] for g in golden] and len({c['id'] for c in cases})==59
 source_manifest=json.loads((BASE/'source-manifest.json').read_bytes())
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
   if check_git:
    assert subprocess.check_output(['git','cat-file','blob',item['commit']+':'+item['path']],cwd=BASE)==data
 with zipfile.ZipFile(BASE/'native-proof.zip') as z:
  assert z.testzip() is None
  proof=json.loads((BASE/'proof-manifest.json').read_bytes())
  assert set(z.namelist())==set(proof) and len(z.namelist())==len(set(z.namelist()))
  for name,item in proof.items():
   rawentry=z.read(name);assert len(rawentry)==item['size'] and SHA(rawentry)==item['sha256'] and z.getinfo(name).CRC==item['crc32']
  receipts=[]
  for run in ['run1','run2']:
   receipt=json.loads(z.read(run+'/receipt.json'));comp=json.loads(z.read(run+'/compile-receipt.json'));receipts.append(receipt)
   assert receipt['sourceCommit']==prov['commit'] and receipt['caseCount']==59
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
    assert source[:span['endByte']].count(b'\n')+1==span['endLine']
    if span['file']:assert z.read(run+'/'+span['file'])==data+(b'\n' if span.get('outerLf') else b'')
  assert receipts[0]['pid']!=receipts[1]['pid'] or receipts[0]['birthFileTime']!=receipts[1]['birthFileTime']
  for name in [n[5:] for n in z.namelist() if n.startswith('run1/') and n.endswith(('.inc','.cpp'))]:assert z.read('run1/'+name)==z.read('run2/'+name)
 for c,g in zip(cases,golden,strict=True):
  steps=g['steps'];n=max(1,len(c['begin']));assert len(steps)==2*n
  for step in steps:
   index=(-1 if step['row']==0 else len(c['begin']) if step['row']==1 else 2147483647) if c['mode']=='outside-lines' else step['row']
   assert step['outerLineIndexAfter']==index
   if step['errorUtf8Hex']:assert step['value'] is None and step['serializedUtf8Hex'] is None
   else:assert step['value']['kind'] in ['none','bool'] and isinstance(step['serializedUtf8Hex'],str)
  if c['mode']=='null-provider':assert all(s['value']=={'kind':'none'} and s['cacheAfter']==0 and not s['trace'] for s in steps)
  if c['mode']=='scan' and not any(v['type']=='throw' for v in c['begin']+c['end']):
   assert steps[0]['trace']==[dict(node=node,lineIndex=i) for i in range(len(c['begin'])) for node in ['begin','end']]
   assert steps[0]['cacheAfter']==1 and all(not s['trace'] for s in steps[1:])
  if c['mode']=='scan' and any(v['type']=='throw' for v in c['begin']+c['end']):
   side,index=next((side,i) for side in ['begin','end'] for i,v in enumerate(c[side]) if v['type']=='throw')
   expected=[dict(node=node,lineIndex=i) for i in range(index) for node in ['begin','end']]+[dict(node='begin',lineIndex=index)]+([dict(node='end',lineIndex=index)] if side=='end' else [])
   assert all(s['trace']==expected and s['cacheAfter']==0 and s['errorUtf8Hex'] for s in steps)
  if c['mode'] in ['cancel-at-begin','cancel-at-end']:assert all(s['cacheAfter']==0 and s['errorUtf8Hex'] for s in steps)
  if c['mode']=='inner-success-outer-throw':assert all(s['cacheAfter']==1 and s['errorUtf8Hex'] for s in steps)
  if c['mode'] in ['different-begin','different-end','different-pair','swap-pair']:assert steps[n]['cacheBefore']==1 and steps[n]['cacheAfter']==2 and steps[n]['trace']
  if c['mode'] in ['same-pair-new-wrapper','mutate-cached']:assert all(not s['trace'] and s['cacheAfter']==1 for s in steps[n:])
  if c['mode'] in ['clear-cache','new-context']:assert steps[n]['cacheBefore']==0 and steps[n]['cacheAfter']==1 and steps[n]['trace']
 evidence=json.loads((BASE/'source-test-evidence.json').read_bytes())
 if check_git:
  test=subprocess.check_output(['git','cat-file','blob',evidence['commit']+':'+evidence['path']],cwd=BASE);assert len(test)==evidence['size'] and SHA(test)==evidence['sha256']
  span=evidence['span'];assert SHA(test[span['startByte']:span['endByte']])==span['sha256']
  paths=[str(p) for p in BASE.iterdir() if p.is_file()]
  attrs=subprocess.check_output(['git','check-attr','text','--',*paths],cwd=BASE).decode();assert all(line.endswith(': unset') for line in attrs.splitlines())
 spec=importlib.util.spec_from_file_location('inside_plan',BASE/'generate-public-plan.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
 plan=json.loads((BASE/'public-case-plan.json').read_bytes());assert plan==module.plan()
 counts={k:sum(x['expectedOutcome']==k for x in plan) for k in ['serialized-key-comparison','runtime-rejection','native-only']}
 assert counts=={'serialized-key-comparison': 35, 'runtime-rejection': 6, 'native-only': 18}
 return dict(nativeCases=59,sourceSpans=len(prov['extractions']),goldenSha256=SHA(raw),publicPlan=counts,typedPublicEquivalence=False,gitChecked=check_git,passed=True)

if __name__=='__main__':
 ap=argparse.ArgumentParser();ap.add_argument('--actual',type=pathlib.Path);ap.add_argument('--git',action='store_true');ap.add_argument('--verify',action='store_true');a=ap.parse_args();summary=verify(a.git)
 if a.actual:assert a.actual.read_bytes()==(BASE/'golden.ndjson').read_bytes()
 print(json.dumps(summary))
