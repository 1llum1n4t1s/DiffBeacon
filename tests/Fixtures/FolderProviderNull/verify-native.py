"""固定native本体原本の独立reader。full constructor/Optimize/parserは未実行。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,io,binascii,argparse,re
R=pathlib.Path(__file__).resolve().parent;sha=lambda b:hashlib.sha256(b).hexdigest()
def req(v,m):
 if not v:raise ValueError(m)
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--repo-root',type=pathlib.Path,required=True);a=ap.parse_args();repo=a.repo_root.resolve();receipt=json.loads((R/'proof-receipt.json').read_bytes());raw=(R/'native-folder-null-proof.zip').read_bytes();req(sha(raw)==receipt['sha256'] and len(raw)==receipt['size'],'proof SHA/size')
 with zipfile.ZipFile(io.BytesIO(raw)) as z:
  names=z.namelist();index=json.loads(z.read('entry-index.json'));req(len(names)==len(set(names)) and set(names)==set(index)|{'entry-index.json'} and z.testzip() is None,'proof exactset/CRC');entries={n:z.read(n) for n in index}
  for n,m in index.items():
   b=entries[n];req(len(b)==m['size'] and sha(b)==m['sha256'] and z.getinfo(n).CRC==m['crc32']==binascii.crc32(b)&0xffffffff,'proof entry '+n)
 prov=json.loads(entries['provenance.json']);sm=prov['sourceManifest'];base=repo/'tests/Fixtures/LineTransformAggregates';req(sm==json.loads((base/'source-manifest.json').read_bytes()),'source manifest');archive=base.parent/'LineTransformReplace/reference-source.zip';req(sha(archive.read_bytes())==sm['reusedArchiveSha256']==prov['sourceArchiveSha256'],'source archive SHA');data={}
 with zipfile.ZipFile(archive) as z:
  req(z.testzip() is None,'source archive CRC')
  for s in sm['sources']:
   p=s['path'];b=z.read('original/'+p) if s['origin']=='sibling-fixed-reference-zip' else (base/'additional-original'/p).read_bytes();req(sha(b)==s['sha256'] and len(b)==s['size'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob'],'fixed source '+p);data[p]=b
 cpp=data['Src/FilterEngine/FilterExpressionNodes.cpp'];req(sha(cpp)==prov['sourceCppSha256']=='a2fcca950bd5e5cfa48f3ff43e4436ae0f5fd55eff9661da1d5a979cc9051135','cpp SHA')
 for s in prov['extractions']:
  raw=data[s['path']];b=raw[s['startByte']:s['endByte']];req(sha(b)==s['sha256'] and raw[:s['startByte']].count(b'\n')+1==s['startLine'] and raw[:s['endByte']].count(b'\n')+1==s['endLine'],'native span SHA/lines');req(entries[s['file']]==b+(b'\n' if s.get('outerLf',False) else b''),'unchanged native span '+s['file'])
 for p in prov['reusedProofs']:
  root=repo/'tests/Fixtures'/p['path'];req(sha(root.read_bytes())==p['sha256'] and sha((root.parent/'provenance.json').read_bytes())==p['provenanceSha256'],'existing proof identities')
 funcs=json.loads(entries['functions.json']);req(len(funcs)==15 and len({f['name'] for f in funcs})==15,'15 registrations');req('matchdistancebefore' in {f['name'] for f in funcs} and 'matchdistanceafter' in {f['name'] for f in funcs},'canonical distance names')
 for f in funcs:
  s=f['registration'];b=cpp[s['startByte']:s['endByte']];expected=('{"'+f['name']+'", '+f['function']+', '+str(f['minArgs'])+', '+str(f['maxArgs'])+'}').encode();req(b==expected and sha(b)==s['sha256'] and cpp[:s['startByte']].count(b'\n')+1==s['startLine'],'native registration '+f['name'])
 cases=json.loads(entries['cases.json']);req(len(cases)==159 and len({c['id'] for c in cases})==159,'159 unique cases');byname={f['name']:f for f in funcs}
 for c in cases:
  f=byname[c['function']];req(c['nativeFunction']==f['function'] and c['arity'] in {f['minArgs'],f['maxArgs']} and c['context']==dict(provider=None,sharedContext=None,lineIndex=-1),'valid-arity context input')
 matrix=json.loads(entries['failure-matrix.json']);req(matrix['nativeCases']==159 and [x['nativeExecuted'] for x in matrix['failureMatrix']]==[True,False,False],'phase qualification')
 req(prov['originalEvaluateExecuted'] and prov['constructorAndOptimizeExecuted'] is False,'source-only phases')
 outputs=[];children=[]
 for run in ['run1','run2']:
  cp=json.loads(entries[run+'-compile-receipt.json']);r=json.loads(entries[run+'-receipt.json']);out=entries[run+'-golden.ndjson'];rows=[json.loads(l) for l in out.splitlines()]
  for p in [cp,r]:req(p['exitCode']==0 and not p['timeout'] and p['stdoutEof'] and p['stderrEof'],run+' completion')
  req(r['pid']>0 and r['birthFileTime']>0 and r['heldProcessHandleSignaled'] and r['stdoutSha256']==sha(out) and r['stderrSha256']==sha(b'') and entries[run+'-reference.stderr']==b'' and r['casesSha256']==sha(entries['cases.json']),run+' retained child/bytes')
  for n,d in cp['inputSha256'].items():req(sha(entries[n])==d,run+' compiler input '+n)
  req([g['id'] for g in rows]==[c['id'] for c in cases],run+' IDs/count')
  for g in rows:req(g['phase']=='nativeFunction+originalEvaluate' and g['context']==dict(providerNull=True,sharedContextNull=True,lineIndex=-1) and g['value']==dict(kind='none') and g['trace']==[] and g['optimizeTrace']==[] and not g['error'],run+' null guard observation')
  outputs.append(out);children.append((r['pid'],r['birthFileTime']))
 req(outputs[0]==outputs[1] and children[0]!=children[1],'independent children/exact stdout')
 projection=json.loads(entries['public-projection.json']);req(len(projection)==159 and [p['nativeCaseId'] for p in projection]==[c['id'] for c in cases] and all(not p['executed'] and not p['nativeFullAstObserved'] for p in projection),'unexecuted public projection')
 print(json.dumps(dict(passed=True,indexedProofEntries=len(index),sourceBlobs=len(data),unchangedSourceSpans=len(prov['extractions']),functions=15,nativeCases=159,independentCompiles=2,independentChildren=2,contextTriadObserved=True,allArgumentsUnevaluated=True,allNativeValuesNone=True,constructorArityExecuted=False,childOptimizeFailureExecuted=False,publicProjectionExecuted=False,productExecuted=False)))
if __name__=='__main__':main()