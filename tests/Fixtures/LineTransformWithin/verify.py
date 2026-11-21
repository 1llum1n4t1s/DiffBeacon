"""Strict public diagnostic contract; source and fixed family verified read-only."""
import sys
sys.dont_write_bytecode=True
import argparse,pathlib,json,hashlib,subprocess,os,collections
BASE=pathlib.Path(__file__).resolve().parent
BRIDGE_SCOPE='serialized-value/key-only; no native typed-AST/full-parser compatibility'
PLAIN_SCOPE='frozen context public evaluation'
sha=lambda b:hashlib.sha256(b).hexdigest()
def require(ok,message):
 if not ok:raise ValueError(message)
def load(path):return json.loads(path.read_text('utf8'))
def read_rows(path):
 rows=[json.loads(l) for l in path.read_bytes().splitlines() if l.strip()]
 require(all(isinstance(r,dict) for r in rows),'diagnostic must be object')
 require(all('id' in r and type(r['id']) is str for r in rows),'missing id field')
 require(len({r['id'] for r in rows})==len(rows),'duplicate ID')
 return rows
def verify_fixed():
 manifest=load(BASE/'fixed-manifest.json')
 require({e['path'] for e in manifest['files']}=={p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file() and p.name!='fixed-manifest.json'},'family missing/extra file')
 for e in manifest['files']:
  raw=(BASE/e['path']).read_bytes();require(len(raw)==e['size'] and sha(raw)==e['sha256'],'fixed/generated tamper: '+e['path'])
def diagnostic_rows(observed,definitions,golden,source_sha,probe=False):
 require([r['id'] for r in observed]==[q['id'] for q in definitions],'missing/extra/reordered ID')
 counts=collections.Counter()
 for r,q in zip(observed,definitions):
  required=['id','context','expression','classification','sourceCommit','sourceSha256','originalHex','keyHex','sourceLineCount','rowIndex','status','errorPhase','exceptionType','executed','publicExecution','compatibilityScope']
  require(all(k in r for k in required),'missing metadata '+q['id'])
  for k in ['id','context','expression','classification','sourceCommit']:
   require(json.dumps(r[k],sort_keys=True)==json.dumps(q[k],sort_keys=True),k+' mismatch '+q['id'])
  require(type(r['sourceSha256']) is str and r['sourceSha256'].lower()==source_sha,'source SHA mismatch')
  c=q['context'];exact=dict(kind='literal-no-getter',sourceLineHex='',sourceLineCount=1,rowIndex=0,outerEol=0,tableMode=False,pane='Left',targetMask='Left',forbidRetargeting=True)
  require(json.dumps(c,sort_keys=True)==json.dumps(exact,sort_keys=True),'context is not EOF string[1]')
  for k in ['sourceLineCount','rowIndex']:require(type(r[k]) is int and r[k]==c[k],k+' mismatch')
  require(r['originalHex']==c['sourceLineHex'],'original bytes mismatch')
  outcome=q['expectedPublicOutcome']
  if outcome=='serialized-value-equivalence':expected=('ok','','',golden[q['id']]['serializedUtf8Hex']);execution=(True,'executed')
  elif outcome=='compile-constructor-rejection':expected=('error','compile','ArgumentException',c['sourceLineHex']);execution=(True,'executed')
  else:
   require(outcome=='unrepresentable-not-executed' and q['expression'] is None,'unrepresentable expression/outcome')
   expected=('unsupported','unrepresentable','UnrepresentableAdapterType',c['sourceLineHex']);execution=(False,'NONEXECUTED')
  require(tuple(r[k] for k in ['status','errorPhase','exceptionType','keyHex'])==expected,'status/phase/type/key mismatch '+q['id'])
  require(type(r['executed']) is bool and (r['executed'],r['publicExecution'])==execution,'execution claim mismatch '+q['id'])
  bridge=bool(q.get('publicBridges'))
  require(r['compatibilityScope']==(BRIDGE_SCOPE if bridge else PLAIN_SCOPE),'bridge/scope annotation mismatch '+q['id'])
  if 'publicBridges' in r:require(r['publicBridges']==q.get('publicBridges',[]),'bridge metadata mismatch')
  if not probe:
   counts[outcome]+=1
   if bridge:counts['value-only-bridge']+=1
 return counts
def main():
 p=argparse.ArgumentParser();p.add_argument('--actual',required=True,type=pathlib.Path);p.add_argument('--public-plan',required=True,type=pathlib.Path);p.add_argument('--probes',required=True,type=pathlib.Path);p.add_argument('--reference-zip',type=pathlib.Path,default=BASE.parent/'LineTransformReplace/reference-source.zip');a=p.parse_args()
 verify_fixed();require(a.public_plan.read_bytes()==(BASE/'public-case-plan.json').read_bytes(),'frozen public plan bytes mismatch')
 plan=load(a.public_plan);golden={r['id']:r for r in read_rows(BASE/'golden.ndjson')}
 source=next(s['sha256'] for s in load(BASE/'provenance.json')['sources'] if s['path']=='Src/FilterEngine/FilterExpressionNodes.cpp')
 counts=diagnostic_rows(read_rows(a.actual),plan['cases'],golden,source)
 probes=[dict(q,classification='public-constructor-probe',sourceCommit=plan['sourceCommit']) for q in plan['publicConstructorProbes']]
 diagnostic_rows(read_rows(a.probes),probes,golden,source,True)
 require(dict(counts)=={'serialized-value-equivalence':59,'compile-constructor-rejection':3,'unrepresentable-not-executed':10,'value-only-bridge':12} and len(probes)==1,'fixed counts mismatch')
 subprocess.run([sys.executable,str(BASE/'verify_immutable_source.py'),'--reference-zip',str(a.reference_zip)],check=True,env=dict(os.environ,PYTHONDONTWRITEBYTECODE='1'))
 print(json.dumps(dict(passed=True,counts=counts,separateProbeCount=len(probes),actualSha256=sha(a.actual.read_bytes()),publicPlanSha256=sha(a.public_plan.read_bytes()),qualification='Exact public context/value/error contract; producer qualification is verified separately')))
if __name__=='__main__':main()
