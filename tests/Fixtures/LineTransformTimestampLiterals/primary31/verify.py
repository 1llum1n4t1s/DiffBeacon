"""Self-contained read-only fixture verifier. No compiler, old actor, or Native execution."""
from pathlib import Path,PurePosixPath
import json,hashlib,zipfile,re,collections,sys,argparse
BASE=Path(__file__).resolve().parent
def digest(b):return hashlib.sha256(b).hexdigest()
def data(n):return (BASE/n).read_bytes()
def js(n):return json.loads(data(n))
def basename(p):return p.replace('\\','/').rsplit('/',1)[-1]
def read_archive(name,manifest):
 expected={x['path']:x for x in js(manifest)};assert len(expected)==len(js(manifest))
 with zipfile.ZipFile(BASE/name) as z:
  names=z.namelist();assert len(names)==len(set(names)) and set(names)==set(expected) and z.testzip() is None
  assert sum(x.file_size for x in z.infolist())<64*1024*1024
  result={}
  for n in names:
   path=PurePosixPath(n);assert not path.is_absolute() and '..' not in path.parts and '\\' not in n and ':' not in n and not any(x.lower() in ['artifacts','.codex'] for x in path.parts)
   b=z.read(n);e=expected[n];assert len(b)==e['size'] and digest(b)==e['sha256'] and z.getinfo(n).CRC==e['crc32'];assert (z.getinfo(n).external_attr>>16)&0o170000==0o100000;result[n]=b
  return result

def verify():
 fixed=js('fixed-manifest.json');assert fixed['schemaVersion']==1
 for x in fixed['files']:assert len(data(x['path']))==x['size'] and digest(data(x['path']))==x['sha256']
 src=read_archive('reference-source.zip','source-zip-manifest.json');proof=read_archive('native-proof.zip','proof-zip-manifest.json');m=js('source-manifest.json');assert len(src)==394 and len(m['sources'])==375 and len(m['pieces'])==12
 shaSource={}
 for x in m['sources']:
  b=src[x['archivePath']];assert digest(b)==x['sha256'] and len(b)==x['size'];assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==x['gitBlob'];assert x['fixedCommit']==m['fixedCommit'];shaSource[digest(b)]=b
 for x in m['pieces']:
  b=src[x['archivePath']];whole=src['source/'+x['sourcePath']];assert digest(whole)==x['sourceSha256'];span=b''.join(whole.splitlines(keepends=True)[x['firstLine']-1:x['lastLine']]);assert b==span and digest(b)==x['sha256']
 for n,b in src.items():shaSource[digest(b)]=b
 assert sum(n.startswith('licenses/') for n in src)==5 and {'adapter/timestamp_adapter.cpp','adapter/timestamp_original.hpp'}<=set(src)
 p=json.loads(proof['forensic/current/native-execution-plan.authoritative.json']);parent=json.loads(proof['forensic/current/parent-atomic-native-qualified-v4.json']);assert digest(proof['forensic/current/parent-atomic-native-qualified-v4.json'])=='fbd45ca4923f5a2b557a511122e802341404eaa3e1b52ee51c5a3dff2e741677';assert parent['qualified'] and parent['newCompile']==1 and parent['inheritedObjects']==35 and parent['nativeRuns']==2 and parent['casesPerRun']==31 and parent['heldPidBirthOsExit0BothEOFQualified']
 def receipt(prefix,name,expectedExit=0):
  r=json.loads(proof[prefix+name+'.receipt.json']);assert r['osExitCode']==r['pythonExitCode']==expectedExit and r['waitForSingleObjectResult']==0 and r['birthFileTime']>0 and r['osExitFileTime']>r['birthFileTime'] and r['birthValidatedOnHeldHandleAfterExit'] and r['handleHeldThroughOsExitAndBothEof'] and r['heldProcessHandle'] and r['jobAssigned'] and not r['drainErrors']
  for ch in ['stdout','stderr']:assert r[ch]['eof'] and digest(proof[prefix+name+'.'+ch+'.bin'])==r[ch]['sha256'] and len(proof[prefix+name+'.'+ch+'.bin'])==r[ch]['size']
  return r
 inherited=p['inheritedObjects'];assert len(inherited)==35 and len({x['path'] for x in inherited})==35
 for x in inherited:
  r=receipt('forensic/old35/',x['producerId']);assert digest(proof['forensic/old35/'+x['producerId']+'.receipt.json'])==x['producerReceiptSha256'];assert r['command']==x['producerCommand'] and r['pid']==x['producerPid'] and r['birthFileTime']==x['producerBirthFileTime'] and r['sourceSha256AtLaunch']==x['sourceSha256'];assert x['sourceSha256'] in shaSource
  obj=proof['forensic/inherited-objects/'+basename(x['path'])];assert digest(obj)==x['sha256'] and len(obj)==x['size'] and r['outputs'][0]['sha256']==x['sha256'];assert digest(proof['forensic/old35/native-execution-plan.authoritative.json'])==x['producerPlanSha256'] and digest(proof['forensic/old35/environment-whitelist.sdk-only.json'])==x['producerEnvironmentSha256']
 oldFailure=receipt('forensic/old-link1120/','link-native',1120);assert b'LNK1120' in proof['forensic/old-link1120/link-native.stdout.bin'] and b'??0AtomicCounter@Poco' in proof['forensic/old-link1120/link-native.stdout.bin'] and not oldFailure['outputs']
 actual={n:receipt('actual/',n) for n in ['compile-AtomicCounter','link-native','native-run-1','native-run-2']};assert len({x['pid'] for x in actual.values()})==4
 for n,r in actual.items():
  for o in r['outputs']:assert digest(proof['forensic/current-outputs/'+basename(o['path'])])==o['sha256']
 assert actual['compile-AtomicCounter']['sourceSha256AtLaunch'] in shaSource
 exe=digest(proof['forensic/current-outputs/timestamp-native-capturer.exe']);assert exe==parent['exeSha256']==actual['link-native']['outputs'][0]['sha256']
 result=json.loads(proof['forensic/current/execution-result.json']);assert result['status']=='completed' and result['compilerCount']==35;assert fixed['actualCounts']=={'newCompile':1,'inheritedObjects':35,'newLink':1,'nativeRuns':2}
 wr=json.loads(proof['forensic/current/supervisor-held-receipt.json']);assert wr['osExitCode']==0 and wr['bothEof'] and wr['handleHeldThroughOsExitAndBothEof'] and wr['birthMatchedAfterExit'];assert wr['pid']==parent['supervisorPid']
 case=js('portable-case-plan.json')['constructorCases'];assert digest(data('case-plan.json'))==js('portable-case-plan.json')['originalForensicCasePlanSha256'];assert case==js('case-plan.json')['constructorCases'];joined=js('native-semantic31-joined.json');assert len(case)==len(joined['cases'])==31
 transient={'processId','processBirthFileTimeDecimal','clockBeforeEpochMicrosecondsInt64Decimal','clockAfterEpochMicrosecondsInt64Decimal'};runs=[];hexpat=re.compile(r'^(?:[0-9a-f]{2})*$');intpat=re.compile(r'^-?(0|[1-9][0-9]*)$');profile=joined['sameProfile'];okCounts=[]
 for number in [1,2]:
  raw=data('native-run-'+str(number)+'.ndjson');assert raw==proof['actual/native-run-'+str(number)+'.stdout.bin'];rows=[json.loads(v) for v in raw.splitlines()];r=actual['native-run-'+str(number)];assert len(rows)==31;assert len({x['id'] for x in rows})==31
  for idx,(c,row,j) in enumerate(zip(case,rows,joined['cases'])):
   assert row['id']==c['id']==j['id'] and row['inputUtf8Hex']==c['stringUtf8Hex']==j['constructorInputUtf8Hex'];assert row['processId']==r['pid'] and int(row['processBirthFileTimeDecimal'])==r['birthFileTime'];assert row['sourceCommit']==m['fixedCommit'] and row['originalNodesSha256']==digest(src['source/Src/FilterEngine/FilterExpressionNodes.cpp']) and row['contextLineIndex']==-1 and row['contextPointersNull'] and row['scope']=='original-ctor-evaluate-timestamp-arm-only'
   assert {k:row[k] for k in profile}==profile and {k:v for k,v in row.items() if k not in transient}==j['rawNativeObservation'];assert j['rawRun'+str(number)+'Line']==idx+1 and j['run'+str(number)+'Sha256']==digest(raw)
   for key in ['timestampArmSerializedUtf8Hex','exceptionTypeNameHex','errorMessageHex']:assert hexpat.fullmatch(row[key])
   for key in ['clockBeforeEpochMicrosecondsInt64Decimal','clockAfterEpochMicrosecondsInt64Decimal']:assert intpat.fullmatch(row[key]) and -(1<<63)<=int(row[key])<(1<<63)
   if row['status']=='ok':assert row['valueTypeIndex']==4 and row['rawType']=='Poco::Timestamp' and intpat.fullmatch(row['epochMicrosecondsInt64Decimal']) and -(1<<63)<=int(row['epochMicrosecondsInt64Decimal'])<(1<<63)
   else:assert row['status']=='error' and row['errorPhase']=='constructor' and row['epochMicrosecondsInt64Decimal']==''
   nul=b'\0' in bytes.fromhex(c['stringUtf8Hex'])
   if nul:assert j['id']=='embedded-nul' and j['constructorOnly'] and j['publicproposal'] is None and j['definedBehaviorCompatibilityGolden'] is False and not j['publicNulCompileRejectQualification'];assert int(row['epochMicrosecondsInt64Decimal'])>(1<<63)//10
   else:assert j['publicproposal']['rawSerializedUtf8HexKey']==row['timestampArmSerializedUtf8Hex'] and j['publicproposal']['tokenPrefixCandidates']==['d','D'] and j['publicproposal']['publicTypedObservation'] is None and not j['publicproposal']['publicParserOrLexerQualified']
  runs.append(rows);okCounts.append(dict(collections.Counter(x['status'] for x in rows)))
 assert actual['native-run-1']['pid']!=actual['native-run-2']['pid'] and actual['native-run-1']['birthFileTime']!=actual['native-run-2']['birthFileTime'];assert [{k:v for k,v in r.items() if k not in transient} for r in runs[0]]==[{k:v for k,v in r.items() if k not in transient} for r in runs[1]]
 original=src['adapter/timestamp_adapter.cpp'];excluded=[x for x in original.splitlines(keepends=True) if b'{"embedded-nul",' in x];assert len(excluded)==1;assert data('reproduce-safe30-adapter.cpp')==b''.join(x for x in original.splitlines(keepends=True) if x not in excluded)
 scope=js('reproduction-scope.json');portable=js('portable-compile-plan.json');assert scope['excluded']==['embedded-nul'] and scope['unsafeOriginal31AdapterExecutionProhibited'] and scope['nextExactGoRequired'];assert portable['requiresNewSafe30ScopeApproval'] and len(portable['compileTUs'])==36 and portable['compileCount']==36 and portable['nativeCaseCount']==30 and portable['oldObjReuseProhibited']
 for c in portable['compileTUs']:
  b=data(c['sourceRelative']) if c['sourceRelative']=='reproduce-safe30-adapter.cpp' else src[c['sourceRelative']];assert digest(b)==c['sourceSha256']
 return {'qualified':True,'selfContainedNoOldActorRequired':True,'originalSources':375,'rawPieces':12,'sourceZipEntries':len(src),'proofZipEntries':len(proof),'actualNewCompile':1,'inheritedObjects':35,'actualNewLink':1,'actualNativeRuns':2,'recordsPerRun':31,'nativeStatusCounts':okCounts,'sameAmbientAndSemantic':True,'exeSha256':exe,'legacyCompilerCount35NotUsedAsNewCompile':True,'embeddedNulForensicOnly':True,'portableUniversalGolden':False,'publicProposals30Unqualified':True,'safe30ReproductionProposalOnly':True,'reproductionExecuted':False}
if __name__=='__main__':
 argparse.ArgumentParser(description=__doc__).parse_args();print(json.dumps(verify(),ensure_ascii=False))
