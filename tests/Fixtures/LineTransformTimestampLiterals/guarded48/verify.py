#!/usr/bin/env python3
# Guarded48 supplement の相対入力だけを読む。Native/公開parser/goldenの再計算は行わない。
from pathlib import Path,PurePosixPath
import sys,json,hashlib,zipfile,stat,re,binascii,collections
ROOT=Path(sys.argv[1] if len(sys.argv)==2 else Path(__file__).parent).absolute()
assert len(sys.argv)<=2
COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
ROOT_PROOF_SHA='1649d8c59d0897f1c9054dd3020c4c1a20fbd304d1a05706561843b0f2a92f5a'
REFERENCE_SHA='044a1e0497951b740f6b11185592983ed9fee921df8d98ca4ffd987031eaa120'
BUDGET=64*1024*1024
def canonical_relative(name):
 assert isinstance(name,str) and name and '\\' not in name and '\0' not in name and ':' not in name
 p=PurePosixPath(name);assert not p.is_absolute() and str(p)==name and all(x not in ['.','..',''] for x in p.parts)
 return p
def local(name):
 p=ROOT.joinpath(*canonical_relative(name).parts)
 assert p.resolve(strict=True).is_relative_to(ROOT.resolve(strict=True))
 for x in [p,*p.parents]:
  assert not x.is_symlink() and not (hasattr(x,'is_junction') and x.is_junction()),('symlink/junction',str(x))
  if x==ROOT:break
 return p
def sha_bytes(data):return hashlib.sha256(data).hexdigest()
def bytes_local(name):return local(name).read_bytes()
def sha_local(name):return sha_bytes(bytes_local(name))
def load(name):return json.loads(bytes_local(name).decode('utf-8',errors='strict'))
for p in [ROOT,*ROOT.parents]:assert not p.is_symlink() and not (hasattr(p,'is_junction') and p.is_junction())
assert ROOT.is_dir() and len(list(ROOT.rglob('*')))<2000
disk=0
for p in ROOT.rglob('*'):
 assert not p.is_symlink() and not (hasattr(p,'is_junction') and p.is_junction())
 if p.is_file():disk+=p.stat().st_size;assert disk<=BUDGET
fixed=load('fixed-manifest.json')
assert fixed['schemaVersion']==1 and fixed['sourceCommit']==COMMIT and fixed['budgetBytes']==BUDGET
files=fixed['files'];assert len({x['path'] for x in files})==len(files)
for x in files:
 assert local(x['path']).is_file() and local(x['path']).stat().st_size==x['size'] and sha_local(x['path'])==x['sha256']
assert sha_local('reference-source.zip')==REFERENCE_SHA and sha_local('forensic/root-parent-proof.json')==ROOT_PROOF_SHA
zmanifest=load('source-zip-manifest.json');zip_rows={x['path']:x for x in zmanifest};assert len(zip_rows)==394
archive={}
with zipfile.ZipFile(local('reference-source.zip')) as z:
 infos=z.infolist();assert len(infos)==394 and len({i.filename for i in infos})==394
 expanded=sum(i.file_size for i in infos);assert expanded<=BUDGET and expanded+disk<=BUDGET
 assert {i.filename for i in infos}==set(zip_rows)
 for i in infos:
  canonical_relative(i.filename);assert not i.is_dir() and not stat.S_ISLNK(i.external_attr>>16) and not (i.flag_bits&1)
  expected=zip_rows[i.filename];assert i.file_size==expected['size'] and i.CRC==expected['crc32']
  data=z.read(i);assert len(data)==i.file_size and sha_bytes(data)==expected['sha256'] and (binascii.crc32(data)&0xffffffff)==expected['crc32']
  archive[i.filename]=data
 # z.read() has verified every CRC; no extraction or disk path construction from entries.
src=load('source-provenance.json');oldsrc=load('source-manifest.json')
assert src['sourceCommit']==oldsrc['fixedCommit']==COMMIT and len(src['wholeSources'])==len(oldsrc['sources'])==375
assert src['wholeSources']==oldsrc['sources'] and src['wholeSourceCountIncludesAtomicCounterTu'] and len(src['rawSpans'])==15
sources={}
for x in src['wholeSources']:
 canonical_relative(x['archivePath']);b=archive[x['archivePath']]
 assert x['fixedCommit']==COMMIT and len(b)==x['size'] and sha_bytes(b)==x['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==x['gitBlob']
 assert x['sourcePath'] not in sources;sources[x['sourcePath']]=x
assert 'Externals/poco/Foundation/src/AtomicCounter.cpp' in sources
for x in src['rawSpans']:
 original=sources[x['sourcePath']];b=archive[original['archivePath']]
 assert original['sha256']==x['sourceSha256'] and original['gitBlob']==x['sourceGitBlob']
 raw=b''.join(b.splitlines(keepends=True)[x['firstLine']-1:x['lastLine']])
 data=archive[x['archivePath']] if x.get('archivePath') else bytes_local(x['localPath'])
 assert data==raw and len(data)==x['size'] and sha_bytes(data)==x['sha256']
licenses=src['licenses'];assert len(licenses)==5 and len({x['archivePath'] for x in licenses})==5
for x in licenses:assert sha_bytes(archive[x['archivePath']])==x['sha256']
assert len([x for x in archive if x.startswith('licenses/')])==5
for x in src['activeAdapterInputs']:
 data=bytes_local(x['localPath']);data.decode('utf-8',errors='strict');assert sha_bytes(data)==x['sha256']
 if x.get('sameAsArchivePath'):assert data==archive[x['sameAsArchivePath']]
compiler=load('compiler-scope.json')
assert compiler['adapterCharsetFlag']=='/utf-8' and compiler['cppStandard']=='c++17' and compiler['runtime']=='/MD'
assert compiler['noFreshReproductionPerformed'] and compiler['freshReproductionApproved'] is False and len(compiler['inheritedOriginalObjectBindings'])==35
for x in compiler['inheritedOriginalObjectBindings']:
 assert x['sourcePath'] in sources and sources[x['sourcePath']]['sha256']==x['sourceSha256'] and x['oldActorFileNotRequired']
assert compiler['adapterSha256']==sha_local('adapter/timestamp_boundary_adapter.cpp')
assert compiler['publicParserWholeAstOtherProfilesQualified'] is False
rootproof=load('forensic/root-parent-proof.json')
assert rootproof['qualified'] and rootproof['sourceOriginalCount']==375 and rootproof['sourceRawPieces']==15 and rootproof['sameAmbientAndSemantic']
assert rootproof['newCompile']==rootproof['newLink']==1 and rootproof['inheritedObjects']==35 and rootproof['nativeRuns']==2 and rootproof['casesPerRun']==48
assert rootproof['actualPidBirthOsExit0BothEOF'] and rootproof['sourceObjectProducerAndExeShaQualified'] and not rootproof['goldenGenerated'] and not rootproof['wholeAstPublicParserFullSerializerOtherArmsQualified'] and not rootproof['otherLocaleTzQualified']
rootresult=load('forensic/root-parent-actual-result.json')
assert rootresult['pid']==24632 and rootresult['exitCode']==0 and rootresult['terminal'] and rootresult['heldUntilExit'] and rootresult['stdoutComplete'] and rootresult['stderrComplete'] and not rootresult['timedOut']
links=load('receipt-local-map.json');assert len(links['actualReceipts'])==4
receipts=[]
for x in links['actualReceipts']:
 r=load(x['localReceiptPath'])
 assert r['id']==x['id'] and r['osExitCode']==r['pythonExitCode']==0 and r['birthValidatedOnHeldHandleAfterExit'] and r['handleHeldThroughOsExitAndBothEof'] and r['waitForSingleObjectResult']==0 and r['jobAssigned'] and r['stopReason'] is None and not r['terminationRequested'] and not r['drainErrors']
 for key in ['stdout','stderr']:
  assert r[key]['eof'] and sha_local(x[key+'LocalPath'])==r[key]['sha256'] and len(bytes_local(x[key+'LocalPath']))==r[key]['size']
 assert r['pid'] in rootproof['pids'] and r['birthFileTime'] in rootproof['birthFileTimes']
 receipts.append(r)
assert [r['pid'] for r in receipts]==rootproof['pids'] and [r['birthFileTime'] for r in receipts]==rootproof['birthFileTimes']
assert receipts[0]['sourceSha256AtLaunch']==sha_local('adapter/timestamp_boundary_adapter.cpp') and receipts[0]['command'].count('/utf-8')==1
assert receipts[0]['outputs'][0]['sha256']==compiler['newObjectSha256'] and receipts[1]['outputs'][0]['sha256']==compiler['exeSha256']==rootproof['exeSha256']
sup=load('forensic/supervisor-held-receipt.json')
assert sup['birthMatchedAfterExit'] and sup['jobAssigned'] and sup['osExitCode']==sup['pythonExitCode']==0 and sup['waitResult']==0 and sup['bothEof'] and sup['handleHeldThroughOsExitAndBothEof'] and sup['stopReason'] is None and not sup['errors']
assert sha_local('forensic/supervisor.stdout.raw')==sup['stdout']['sha256'] and sha_local('forensic/supervisor.stderr.raw')==sup['stderr']['sha256']
plan=load('case-plan.json');joined=load('case-join.json')
assert len(plan['constructorCases'])==len(joined['cases'])==48 and all(x['expected'] is None for x in plan['constructorCases'])
raws=[bytes_local('native-run-1.ndjson'),bytes_local('native-run-2.ndjson')]
rows=[[json.loads(line) for line in raw.decode('utf-8',errors='strict').splitlines()] for raw in raws];assert all(len(x)==48 for x in rows)
assert [sha_bytes(x) for x in raws]==fixed['nativeRawSha256']
volatile={'processId','processBirthFileTimeDecimal','clockBeforeEpochMicrosecondsInt64Decimal','clockAfterEpochMicrosecondsInt64Decimal'}
signed=re.compile(r'-?(?:0|[1-9][0-9]*)');hex_bytes=re.compile(r'(?:[0-9a-f]{2})*')
categories=collections.Counter();status=collections.Counter();guard=collections.Counter()
for i,(case,j,a,b) in enumerate(zip(plan['constructorCases'],joined['cases'],*rows),start=1):
 assert case['id']==j['id']==a['id']==b['id'] and j['rawNativeObservation']==a
 assert {k:v for k,v in a.items() if k not in volatile}=={k:v for k,v in b.items() if k not in volatile}
 assert a['callerKind']==case['callerKind'] and a['inputUtf8Hex']==(case['stringUtf8Hex'] or '') and a['inputRawTimestampInt64Decimal']==case['rawTimestampInt64Decimal']
 assert a['sourceCommit']==COMMIT and a['originalNodesSha256']==sources['Src/FilterEngine/FilterExpressionNodes.cpp']['sha256'] and a['contextLineIndex']==-1 and a['contextPointersNull'] is True
 for row,receipt in [(a,receipts[2]),(b,receipts[3])]:
  assert row['processId']==receipt['pid'] and row['processBirthFileTimeDecimal']==str(receipt['birthFileTime'])
  for key in ['inputUtf8Hex','timestampArmSerializedUtf8Hex','exceptionTypeNameHex','errorMessageHex','userLocaleUtf8Hex','timezoneStandardNameUtf8Hex']:assert hex_bytes.fullmatch(row[key])
  if row['epochMicrosecondsInt64Decimal']:assert signed.fullmatch(row['epochMicrosecondsInt64Decimal'])
 for n in [1,2]:
  ref=j['run'+str(n)+'Ref'];assert ref=={'path':'native-run-'+str(n)+'.ndjson','sha256':sha_bytes(raws[n-1]),'line':i,'id':case['id']}
 assert j['expected'] is None and j['publicTypedObservation'] is None and j['TypedOriginObservation'] is None and j['privatePublicTypedObservation'] is None
 assert j['publicParserLexerFullAstOtherProfilesQualified'] is False and j['definedBehaviorCompatibilityGolden'] is None
 epoch=j['epochStorageObservation']
 if a['epochMicrosecondsInt64Decimal']:
  assert epoch=={'epochMicrosecondsInt64Decimal':a['epochMicrosecondsInt64Decimal'],'rawType':a['rawType'],'valueTypeIndex':a['valueTypeIndex'],'scope':'original constructor/Evaluate storage observation only'}
 else:assert epoch is None
 serial=j['serializerObservation'];assert serial['serializationStatus']==a['serializationStatus'] and serial['serializationRequested']==a['serializationRequested']
 if a['serializationStatus']=='completed':assert serial['armCalled'] and serial['serializedUtf8HexObservation']==a['timestampArmSerializedUtf8Hex']
 else:assert serial['armCalled'] is False and serial['serializedUtf8HexObservation'] is None
 prop=j['publicproposal']
 if case['callerKind']=='rawTimestampCtor':
  assert prop is None and j['rawTimestampInt64PublicSyntaxProposal'] is None and a['epochMicrosecondsInt64Decimal']==case['rawTimestampInt64Decimal'];category='raw12-no-public-syntax'
 elif '00' in [case['stringUtf8Hex'][k:k+2] for k in range(0,len(case['stringUtf8Hex']),2)]:
  assert prop is None and j['publicNulCompileRejectQualified'] is False;category='nul1-no-public-proposal'
 else:
  assert prop['tokenPrefixCandidates']==['d','D'] and prop['payloadUtf8Hex']==case['stringUtf8Hex']
  assert prop['publicTypedObservation'] is None and prop['TypedOriginObservation'] is None and prop['privatePublicTypedObservation'] is None and prop['expectedCompileOutcome'] is None and not prop['publicSerializedBridgeQualified'] and not prop['publicParserLexerQualified']
  if a['serializationStatus']=='not-requested-unsafe-original-utcTime':
   assert prop['classification']=='syntax-only-unsafe-epoch-storage' and prop['expectedSerializedUtf8Hex'] is None and prop['rawSerializedUtf8HexKey'] is None;category='unsafe3-syntax-only'
  else:
   assert prop['classification']=='serialized-or-constructor-compile-proposal';category='safe32-serialized-compile-proposal'
   expected=a['timestampArmSerializedUtf8Hex'] if a['serializationStatus']=='completed' else None
   assert prop['expectedSerializedUtf8Hex']==prop['rawSerializedUtf8HexKey']==expected
 assert j['publicProposalCategory']==category
 categories[category]+=1;status[a['status']]+=1;guard[a['serializationStatus']]+=1
assert dict(categories)=={'raw12-no-public-syntax':12,'safe32-serialized-compile-proposal':32,'unsafe3-syntax-only':3,'nul1-no-public-proposal':1}
assert dict(status)==rootproof['statusCounts']=={'ok':40,'error':8}
assert guard['not-requested-unsafe-original-utcTime']==rootproof['unsafeSerializationUncalledCount']==6 and guard['completed']==34 and guard['not-reached']==8
assert joined['proposalCounts']=={'serializedOrConstructorCases':32,'serializedOrConstructorPrefixDrafts':64,'syntaxOnlyUnsafeCases':3,'syntaxOnlyUnsafePrefixDrafts':6,'publicProposalNullCases':13}
assert not joined['publicParserOrTypedOriginQualified'] and joined['otherNativeLocaleTimezoneProfilesQualified'] is False
print(json.dumps({'selfContainedGuarded48SupplementVerified':True,'fixedManifestSha256':sha_local('fixed-manifest.json'),'readerSha256':sha_bytes(Path(__file__).read_bytes()),'sourceCommit':COMMIT,'wholeSources':375,'rawSpans':15,'zipEntries':394,'licenses':5,'nativeRawRows':96,'actualReceipts':4,'callerCounts':{'rawTimestampCtor':12,'stringCtor':36},'guardCountsObservedOnly':dict(guard),'publicProposalCategories':dict(categories),'proposalCounts':joined['proposalCounts'],'oldActorFilesOpened':False,'zipCRC_SHA_GitBlobAndRawSpanQualified':True,'joinOnlyNoEpochCalendarGuardOrGoldenRecalculation':True,'NativeCompileLinkOrReproductionExecuted':False,'publicParserTypedOriginOtherProfileQualified':False,'goldenGenerated':False,'bytesOnDisk':disk,'zipExpandedBytes':expanded},ensure_ascii=False))

