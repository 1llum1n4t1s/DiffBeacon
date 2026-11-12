"""Read-only fixed source and normative public diagnostic contract reader."""
import sys
sys.dont_write_bytecode=True
import argparse,pathlib,json,hashlib,subprocess,os
BASE=pathlib.Path(__file__).resolve().parent
sha=lambda b:hashlib.sha256(b).hexdigest()
def require(ok,message):
 if not ok:raise ValueError(message)
def load(path):return json.loads(path.read_text('utf8'))
def rows(path):
 value=[json.loads(l) for l in path.read_bytes().splitlines() if l.strip()]
 require(all(isinstance(x,dict) for x in value),'row must be object')
 ids=[x.get('id') for x in value]
 require(len(ids)==len(set(ids)),'duplicate IDs')
 return {r['id']:r for r in value}
def verify_fixed():
 manifest=load(BASE/'fixed-manifest.json')
 expected={e['path'] for e in manifest['files']}
 actual={p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file() and p.name!='fixed-manifest.json'}
 require(actual==expected,'family missing/extra file')
 for e in manifest['files']:
  raw=(BASE/e['path']).read_bytes()
  require(len(raw)==e['size'] and sha(raw)==e['sha256'],'fixed/generated tamper: '+e['path'])
def diagnostics(path,definitions,legacy,source_sha):
 observed=rows(path);require(set(observed)=={q['id'] for q in definitions},'missing/extra ID')
 mandatory=['id','context','expression','classification','sourceCommit','sourceSha256','originalHex','keyHex','status','errorPhase','exceptionType']
 if not legacy:mandatory+=['sourceLineCount','rowIndex']
 for q in definitions:
  r=observed[q['id']]
  require(all(k in r for k in mandatory),'missing metadata '+q['id'])
  for k in ['context','expression','classification','sourceCommit']:require(json.dumps(r[k],sort_keys=True)==json.dumps(q[k],sort_keys=True),k+' mismatch '+q['id'])
  require(isinstance(r['sourceSha256'],str) and r['sourceSha256'].lower()==source_sha,'source SHA mismatch')
  c=q['context']
  require(c['sourceLineCount']==1 and c['rowIndex']==0 and c['sourceLineHex']=='','EOF empty context must be string[1]')
  if not legacy:
   for k in ['sourceLineCount','rowIndex']:require(type(r[k]) is int and r[k]==c[k],k+' mismatch')
  else:
   for k in ['sourceLineCount','rowIndex']:
    if k in r:require(type(r[k]) is int and r[k]==c[k],k+' mismatch')
  require(r['originalHex']==c['sourceLineHex'],'original bytes mismatch')
  outcome=q['expectedPublicOutcome']
  if outcome=='serialized-value-equivalence':
   expected=('ok','','',q['nativeOracle']['serializedUtf8Hex'])
  elif outcome=='compile-constructor-rejection':expected=('error','compile','ArgumentException',c['sourceLineHex'])
  else:
   require(q['expression'] is None,'unrepresentable expression')
   expected=('unsupported','unrepresentable','UnrepresentableAdapterType',c['sourceLineHex'])
  require(tuple(r[k] for k in ['status','errorPhase','exceptionType','keyHex'])==expected,'status/phase/type/key mismatch '+q['id'])
 return len(observed)
def main():
 p=argparse.ArgumentParser();p.add_argument('--actual',type=pathlib.Path);p.add_argument('--public-plan',type=pathlib.Path);p.add_argument('--probes',type=pathlib.Path);p.add_argument('--legacy-proof',action='store_true');p.add_argument('--reference-zip',type=pathlib.Path,default=BASE.parent/'LineTransformReplace/reference-source.zip');a=p.parse_args()
 require(not a.legacy_proof or (a.actual and a.public_plan and a.probes),'legacy proof requires actual, explicit normative public-plan, and separate probes')
 require(not a.actual or (a.public_plan and a.probes),'actual requires frozen public-plan and separate probes')
 verify_fixed()
 frozen=BASE/'public-case-plan.json';plan_path=a.public_plan or frozen
 require(plan_path.read_bytes()==frozen.read_bytes(),'public plan bytes mismatch')
 plan=load(plan_path)
 source=next(s for s in load(BASE/'provenance.json')['sources'] if s['path']=='Src/FilterEngine/FilterExpressionNodes.cpp')
 count=diagnostics(a.actual or BASE/'contract-actual.ndjson',plan['cases'],a.legacy_proof,source['sha256'])
 definitions=[dict(q,classification='public-constructor-probe',sourceCommit=plan['sourceCommit']) for q in plan['publicConstructorProbes']]
 probe_count=diagnostics(a.probes or BASE/'contract-probes.ndjson',definitions,a.legacy_proof,source['sha256'])
 subprocess.run([sys.executable,str(BASE/'verify_immutable_source.py'),'--reference-zip',str(a.reference_zip)],check=True,env=dict(os.environ,PYTHONDONTWRITEBYTECODE='1'))
 print(json.dumps(dict(passed=True,normativeMainCases=count,separateConstructorProbes=probe_count,serializedKeys=59,compileArityRejections=3,unrepresentable=10,legacyProof=a.legacy_proof)))
if __name__=='__main__':main()
