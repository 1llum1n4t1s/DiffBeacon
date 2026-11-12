"""保存済み候補の読み取り専用独立照合。引数はcanonical再利用ZIPの場所だけ。"""
import sys
sys.dont_write_bytecode=True
import pathlib,zipfile,json,hashlib,argparse
ap=argparse.ArgumentParser();ap.add_argument('--reference-zip',type=pathlib.Path,default=pathlib.Path(__file__).resolve().parent.parent/'LineTransformReplace/reference-source.zip');args=ap.parse_args()
base=pathlib.Path(__file__).resolve().parent;sha=lambda b:hashlib.sha256(b).hexdigest()
p=json.loads((base/'provenance.json').read_text('utf8'));assert sha(args.reference_zip.read_bytes())==p['reusedArchiveSha256']
manifest=json.loads((base/'native-proof-manifest.json').read_text('utf8'));checks=0
with zipfile.ZipFile(args.reference_zip) as original,zipfile.ZipFile(base/'native-proof.zip') as proof:
 assert len(proof.infolist())==len(manifest)
 for e in manifest:
  b=proof.read(e['path']);assert len(b)==e['size'] and sha(b)==e['sha256'];checks+=1
 for s in p['sources']:
  k='original/'+s['path'];b=original.read(k) if k in original.namelist() else (base/'additional-original'/s['path']).read_bytes()
  assert len(b)==s['size'] and sha(b)==s['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob'];checks+=1
  for e in [e for e in p['extractions'] if e['path']==s['path']]:
   raw=b[e['startByte']:e['endByte']];assert sha(raw)==e['sha256'] and b[:e['startByte']].count(b'\n')+1==e['startLine'] and b[:e['endByte']].count(b'\n')+1==e['endLine']
   if e['file']:assert proof.read(e['file'])==raw+b'\n'
   checks+=1
  f=p['fullOptimizer']
  if f['path']==s['path']:
   raw=b[f['startByte']:f['endByte']];assert sha(raw)==f['sha256'] and b'inrange' not in raw.lower();checks+=1
cases=json.loads((base/'cases.json').read_text('utf8'));golden=(base/'golden.ndjson').read_bytes();rows=[json.loads(l) for l in golden.splitlines()];plan=json.loads((base/'public-case-plan.json').read_text('utf8'))
assert len(cases)==len(rows)==len(plan['cases'])==72
from make_public_plan import build
assert build(cases,rows)==plan;checks+=1
for c,r,q in zip(cases,rows,plan['cases']):
 assert c['id']==r['id']==q['id'] and q['nativeOracle']==r and q['originalArgsAST']==c['args']
 assert not r['folded'];checks+=1
for name in ['capture','recompile']:
 r=json.loads((base/(name+'-receipt.json')).read_text('utf8'))
 assert r['stdoutSha256']==sha(golden) and r['caseCount']==72 and r['exitCode']==0 and not r['timeout'] and r['heldProcessHandleSignaled'] and r['stdoutEof'] and r['stderrEof'];checks+=1
first=json.loads((base/'capture-receipt.json').read_text('utf8'));second=json.loads((base/'recompile-receipt.json').read_text('utf8'));assert first['pid']!=second['pid'] and first['birthFileTime']!=second['birthFileTime'];checks+=1
print(json.dumps(dict(passed=True,checks=checks,caseCount=len(rows),nativeAndActualSerializedSha256=sha(golden),publicPlanSha256=sha((base/'public-case-plan.json').read_bytes()),publicSerializedEquivalenceCandidates=sum(q['expectedPublicOutcome']=='serialized-value-equivalence' for q in plan['cases']),publicConstructorRejections=sum(q['expectedPublicOutcome']=='compile-constructor-rejection' for q in plan['cases'])+len(plan['publicConstructorProbes']))))
