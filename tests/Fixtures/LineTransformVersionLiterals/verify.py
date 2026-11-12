"""Self-contained immutable Version Native/source reader and optional serialized public diagnostics."""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,json,hashlib,subprocess,re
BASE=pathlib.Path(__file__).resolve().parent
PINNED={'raw60':'91afb79da8e937bce12aa4ce7d43c3c2f13c1f51e27de2c167ae9a8892b0898b','supplement128':'2ed53b7313fd60471dc1b458047aa84d8ac0a053e2c12c9c8007b75ef01380e5','cpp17-edge':'6385d2b0eeb37b447d7ba8408096fe283eb03d8ae29d0bf9919a8a6b4efde8a5'}
REFSHA='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
FALSE_FLAGS=['nativeFullParserExecuted','nativeTypedContextQualified','nativeFullAstQualified']
def sha(b):return hashlib.sha256(b).hexdigest()
def js(p):return json.loads(p.read_bytes())
def files(directory,manifest):
 entries=js(manifest);assert {p.relative_to(directory).as_posix() for p in directory.rglob('*') if p.is_file()}=={r['path'] for r in entries}|{'fixed-manifest.json'}
 for r in entries:
  b=(directory/r['path']).read_bytes();assert len(b)==r['size'] and sha(b)==r['sha256'],str(directory/r['path'])
def subprocess_reader(stage,extra):
 cmd=[sys.executable,'-B',str(BASE/stage/'verify.py'),'--reference-zip',str(BASE/'reference-source.zip')]+extra
 r=subprocess.run(cmd,cwd=BASE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=False)
 assert r.returncode==0,(stage,r.stderr.decode('utf-8','replace'));assert not r.stderr
 return json.loads(r.stdout)
def actual_base(row,plan):
 assert row['id']==plan['id'] and row['expressionUtf8Hex']==plan['expressionUtf8Hex']
 assert row['publicExecuted'] is True and isinstance(row['compileSucceeded'],bool)
 assert row['actualValueKind'] is None and row['actualInt64'] is None and row['constructorOrigin'] is None and row['publicTypedObserved'] is False
 for flag in FALSE_FLAGS:assert row[flag] is False
 assert row['nativeIndex']==plan['nativeIndex'] and row['provenance']==plan['provenance']
 for field in ['expectedNativeKind','expectedConstructorOrigin','expectedConstructorOriginEvidence']:assert row[field]==plan[field]
def actual_value(row,compile_expected,serialized):
 assert row['compileSucceeded'] is compile_expected
 if compile_expected:
  assert row['actualSerializedValue']==serialized and row['serializedByteHex']==serialized.encode('utf-8').hex()
  for key in ['errorCode','errorType','errorMessage']:assert row.get(key) is None
 else:
  assert row['actualSerializedValue'] is None and row['serializedByteHex'] is None and row['errorCode']=='compile-rejected'
  assert isinstance(row['errorType'],str) and row['errorType'] and isinstance(row['errorMessage'],str) and row['errorMessage']
def verify_public(path,plans):
 doc=js(path);assert doc['schema']=='diffbeacon.version-public-observations.v1' and doc['producerExecuted'] is True
 rows=doc['records'];assert len(rows)==156 and len({r['id'] for r in rows})==156
 expected={p['id']:p for p in plans};assert {r['id'] for r in rows}==set(expected)
 for row in rows:
  plan=expected[row['id']];actual_base(row,plan)
  actual_value(row,plan['classification']=='public-success-proposal',bytes.fromhex(plan['serializedByteHex']).decode('utf-8'))
 return {'executedRecords':len(rows),'serializedSuccesses':154,'compileRejections':2,'publicTypedObserved':False,'actualConstructorOriginObserved':False}
def verify_separate(path,kind):
 plan_doc=js(BASE/(kind+'-case-plan.json'));plans=plan_doc['records'];expected={p['id']:p for p in plans};assert len(expected)==len(plans) and plans
 doc=js(path);assert doc['schema']=='diffbeacon.version-'+kind+'-observations.v1' and doc['producerExecuted'] is True
 rows=doc['records'];assert len(rows)==len(plans) and {r['id'] for r in rows}==set(expected) and len({r['id'] for r in rows})==len(rows)
 for row in rows:
  plan=expected[row['id']]
  assert row['id']==plan['id'] and row['expressionUtf8Hex']==plan['expressionUtf8Hex'] and row['publicExecuted'] is True and isinstance(row['compileSucceeded'],bool)
  assert row['actualValueKind'] is None and row['actualInt64'] is None and row['constructorOrigin'] is None and row['publicTypedObserved'] is False
  for flag in FALSE_FLAGS:assert row[flag] is False
  assert row['nativeIndex']==plan['nativeIndex'] and row['provenance']==plan['provenance']
  if 'expressionUtf16Hex' in plan:assert row['expressionUtf16Hex']==plan['expressionUtf16Hex']
  for field in ['expectedNativeKind','expectedConstructorOrigin','expectedConstructorOriginEvidence']:assert field not in row, 'engineering/probes have no qualified Native expectation'
  if kind=='probes':
   assert row['publicCompileExecuted'] is True and row['executed'] is False and row['actualNativelexerExecuted'] is False
   assert row['compileSucceeded'] is plan['expectCompile'] and row['actualSerializedValue'] is None and row['serializedByteHex'] is None
   if not plan['expectCompile']:
    assert row['errorCode']=='compile-rejected' and isinstance(row['errorType'],str) and row['errorType'] and isinstance(row['errorMessage'],str) and row['errorMessage']
   else:
    for key in ['errorCode','errorType','errorMessage']:assert row.get(key) is None
  else:actual_value(row,plan['expectCompile'],plan['expectedSerializedValue'])
 return {'executedRecords':len(rows),'scope':plan_doc.get('scope',plan_doc.get('qualification')),'nativeFullAstQualified':False}
def main():
 p=argparse.ArgumentParser();p.add_argument('--public',type=pathlib.Path);p.add_argument('--probes',type=pathlib.Path);p.add_argument('--engineering',type=pathlib.Path);a=p.parse_args()
 files(BASE,BASE/'fixed-manifest.json');assert (BASE/'.gitattributes').read_bytes()==b'* -text\n' and sha((BASE/'reference-source.zip').read_bytes())==REFSHA
 stages=js(BASE/'immutable-stages.json');assert {s['stage'] for s in stages}==set(PINNED)
 for s in stages:
  stage=BASE/s['relativePath'];assert s['relativePath']==s['stage'] and s['fixedManifestSha256']==PINNED[s['stage']] and sha((stage/'fixed-manifest.json').read_bytes())==PINNED[s['stage']]
  files(stage,stage/'fixed-manifest.json');assert sha((stage/'native-proof.zip').read_bytes())==s['nativeProofSha256'] and sha((stage/'source-manifest.json').read_bytes())==s['sourceManifestSha256'] and s['sourceArchiveSha256']==REFSHA
 # Frozen standalone readers receive explicit self-contained paths; forensic actor paths are never followed.
 native=[subprocess_reader('raw60',[]),subprocess_reader('supplement128',['--raw60-dir',str(BASE/'raw60')]),subprocess_reader('cpp17-edge',['--raw60-dir',str(BASE/'raw60'),'--supplement128-dir',str(BASE/'supplement128')])]
 rawbytes=(BASE/'raw60/golden.ndjson').read_bytes();raw=[json.loads(l) for l in rawbytes.splitlines()]
 suppbytes=(BASE/'supplement128/native-observations.ndjson').read_bytes();supp=[json.loads(l) for l in suppbytes.splitlines()]
 edgebytes=(BASE/'cpp17-edge/native-observations.ndjson').read_bytes();edge=[json.loads(l) for l in edgebytes.splitlines()]
 plan_doc=js(BASE/'public-case-plan.json');assert plan_doc['schema']=='diffbeacon.version-public-plan.v1' and plan_doc['publicStatus']=='planned/unexecuted' and plan_doc['counts']=={'successProposals':154,'nulRejectedProposals':2,'nativeInputs':78}
 plans=plan_doc['records'];assert len(plans)==156 and len({r['id'] for r in plans})==156
 for flag in FALSE_FLAGS:assert plan_doc[flag] is False
 seen=[]
 for row in plans:
  assert row['prefix'] in ['v','V'] and row['status']=='planned/unexecuted' and row['expectedNativeKind']=='int64' and row['expectedConstructorOrigin']=='Version' and row['expectedConstructorOriginEvidence']=='static-source' and row['publicTypedObserved'] is False
  for flag in FALSE_FLAGS:assert row[flag] is False
  ix=row['nativeIndex'];prov=row['provenance'];is_base=prov['rawStage']=='raw60';rs=raw if is_base else edge;ss=qs=supp if is_base else edge;stage='supplement128' if is_base else 'cpp17-edge'
  rr=rs[ix['rawRecord']];sr=ss[ix['serializedRecord']];qr=qs[ix['quoteRecord']]
  assert rr['id']==sr['id']==qr['id']==row['nativeId'] and sr['kind']=='serialized' and qr['kind']=='quote-case'
  assert rr['variantIndex']==sr['variantIndex']==3 and row['rawValue']==rr['value']==sr['value'] and row['serializedByteHex']==sr['serializedUtf8Hex'] and bytes.fromhex(row['serializedByteHex']).decode('ascii')==row['rawValue']
  assert row['quotedBodyHex']==qr['bodyHex'] and row['decodedHex']==qr['decodedHex'];input=bytes.fromhex(rr['inputHex']);body=bytes.fromhex(row['quotedBodyHex']);assert body==input.replace(b'"',b'""')+b'"'
  assert row['expressionUtf8Hex']==(row['prefix'].encode()+b'"'+body).hex();eligible=b'\0' not in input
  assert row['classification']==('public-success-proposal' if eligible else 'constructor-only-nul-rejected')
  if eligible:assert qr['errorCode']==0 and row['decodedHex']==input.hex()
  else:assert qr['errorCode']==-2
  assert row['id']==('base' if is_base else 'edge')+':'+row['nativeId']+':'+row['prefix'];seen.append((prov['rawStage'],row['nativeId'],row['prefix']))
  assert prov=={'rawStage':'raw60' if is_base else 'cpp17-edge','quoteStage':stage,'serializedStage':stage,'sourceManifestSha256':sha((BASE/stage/'source-manifest.json').read_bytes()),'observationsSha256':sha(suppbytes if is_base else edgebytes),'rawStdoutSha256':sha(rawbytes if is_base else edgebytes),'rawNativeProofSha256':sha((BASE/prov['rawStage']/'native-proof.zip').read_bytes()),'serializedNativeProofSha256':sha((BASE/stage/'native-proof.zip').read_bytes()),'sourceStd':'c++20' if is_base else 'c++17'}
 assert len(set(seen))==156 and sum(r['classification']=='public-success-proposal' for r in plans)==154
 expected_seen={(stage,c['id'],prefix) for stage,cases in [('raw60',js(BASE/'raw60/cases.json')),('cpp17-edge',js(BASE/'cpp17-edge/cases.json'))] for c in cases for prefix in ['v','V']};assert set(seen)==expected_seen
 separate=js(BASE/'separate-plan-provenance.json')
 for kind,count in [('probes',12),('engineering',34)]:
  path=BASE/(kind+'-case-plan.json');doc=js(path);assert doc['schema']=='diffbeacon.version-'+kind+'-plan.v1' and sha(path.read_bytes())==separate[kind]['sourcePlanSha256'] and separate[kind]['nativeQualified'] is False and separate[kind]['productGeneratedGolden'] is False
  rows=doc['records'];assert len(rows)==count and len({r['id'] for r in rows})==count
  for r in rows:
   assert isinstance(r['expectCompile'],bool) and r['nativeIndex'] is None and r['provenance'] is None
   text=bytes.fromhex(r['expressionUtf16Hex']).decode('utf-16-be','surrogatepass')
   try:encoded=text.encode('utf-8')
   except UnicodeEncodeError:assert r['expressionUtf8Hex'] is None and r['expectCompile'] is False
   else:assert r['expressionUtf8Hex']==encoded.hex()
   if kind=='engineering':assert isinstance(r['expectedSerializedValue'],str) if r['expectCompile'] else r['expectedSerializedValue'] is None
  if kind=='probes':
   assert doc['publicStatus']=='planned/unexecuted' and doc['actualNativelexerExecuted'] is False
   for flag in FALSE_FLAGS:assert doc[flag] is False
   assert {r['path'] for r in doc['sourceReferences']}=={'supplement128/re2c-flags-static.inc','supplement128/version-token-static.inc','supplement128/unescape-quotes.inc'}
   for ref in doc['sourceReferences']:assert sha((BASE/ref['path']).read_bytes())==ref['sha256']
 boundaries=js(BASE/'native-boundaries.json');assert boundaries['undefinedNativeExecuted'] is False and boundaries['localeSensitiveNativeMeasured'] is False and boundaries['ignoredUnicodeTailPolicy']=='not a blanket product rejection reason'
 result={'immutableFormalCandidate':True,'selfContainedNativeSourceChecks':native,'publicPlanRecords':156,'publicPlanStatus':'planned/unexecuted','publicSerializedOnly':True,'publicTypedObserved':False,'nativeFullAstQualified':False,'separateProbesPlanRecords':12,'separateEngineeringPlanRecords':34,'separatePlanStatus':'planned/unexecuted','publicPlanSha256':sha((BASE/'public-case-plan.json').read_bytes()),'fixedManifestSha256':sha((BASE/'fixed-manifest.json').read_bytes()),'immutableStagesSha256':sha((BASE/'immutable-stages.json').read_bytes())}
 if a.public:
  result['actualPublic']=verify_public(a.public,plans);result['actualPublicDiagnosticsSha256']=sha(a.public.read_bytes())
 if a.probes:
  result['actualProbes']=verify_separate(a.probes,'probes');result['actualProbesDiagnosticsSha256']=sha(a.probes.read_bytes())
 if a.engineering:
  result['actualEngineering']=verify_separate(a.engineering,'engineering');result['actualEngineeringDiagnosticsSha256']=sha(a.engineering.read_bytes())
 print(json.dumps(result))
if __name__=='__main__':main()
