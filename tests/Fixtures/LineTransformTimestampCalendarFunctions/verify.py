#!/usr/bin/env python3
"""固定ZIPをメモリ内で読む。Native/製品起動・抽出・一時物・外部Temp探索なし。"""
import sys
sys.dont_write_bytecode = True
import pathlib,json,hashlib,zipfile,collections,io,zlib
ROOT=pathlib.Path(__file__).resolve().parent
e8feeccd3cb79435f0b4f90e944e60f99470ef993d6f5fc67cd8b4fa249aa08e_SHA = 'e8feeccd3cb79435f0b4f90e944e60f99470ef993d6f5fc67cd8b4fa249aa08e'
mb=(ROOT/'fixed-manifest.json').read_bytes()
assert hashlib.sha256(mb).hexdigest()==e8feeccd3cb79435f0b4f90e944e60f99470ef993d6f5fc67cd8b4fa249aa08e_SHA
fixed=json.loads(mb);payload=fixed['files'][0];archive=(ROOT/payload['path']).read_bytes()
assert len(archive)==payload['size'] and hashlib.sha256(archive).hexdigest()==payload['sha256']
closure=zipfile.ZipFile(io.BytesIO(archive));names=closure.namelist()
assert len(names)==len(set(names))==66 and closure.testzip() is None
assert all(not n.startswith('/') and '\\' not in n and '..' not in n.split('/') for n in names)
for pin in fixed['closureEntryPins']:
    b=closure.read(pin['path']);info=closure.getinfo(pin['path'])
    assert len(b)==pin['size'] and info.CRC==pin['crc32'] and hashlib.sha256(b).hexdigest()==pin['sha256']
S=pathlib.PurePosixPath('.')
def read(p):return closure.read(str(p))
def load(p):return json.loads(read(p))
def sha(p):return hashlib.sha256(read(p)).hexdigest()
for info in closure.infolist():
    b=read(info.filename);assert len(b)==info.file_size and (zlib.crc32(b)&0xffffffff)==info.CRC
def typed(v):
    if v is None:return None
    k=v['kind'];o={'variantIndex':v['variantIndex'],'kind':k}
    if k=='timestamp':o['epochSignedInt64Decimal']=v['epochSignedInt64Decimal']
    elif k=='string':o['utf8Hex']=v['utf8Hex']
    elif k=='array':o.update({'nullPointer':v['nullPointer'],'items':None if v['nullPointer'] else [typed(x) for x in v['items']]})
    elif k in ('bool','double'):o['value']=v['value']
    elif k=='int64':o['valueDecimal']=v['valueDecimal']
    return o
def descriptor(a):
    k=a['kind'];k='none' if k=='no-args' else 'array' if k=='null-array' else k
    o={'variantIndex':{'none':0,'bool':1,'double':2,'int64':3,'timestamp':4,'string':6,'array':8}[k],'kind':k}
    if k=='timestamp':o['epochSignedInt64Decimal']=a['epochSignedInt64Decimal']
    elif k=='string':o['utf8Hex']=a['utf8Hex']
    elif k in ('bool','double'):o['value']=a['value']
    elif k=='int64':o['valueDecimal']=a['value']
    elif k=='array':o.update({'nullPointer':a['kind']=='null-array','items':None if a['kind']=='null-array' else [descriptor(x) for x in a['items']]})
    return o
manifest=load(S/'closure-manifest.json')
for x in manifest['files']:assert len(read(S/x['path']))==x['size'] and sha(S/x['path'])==x['sha256'],x['path']
authority=load(S/'execution-authority.json')
for name,h in authority['pins'].items():assert sha(S/name)==h,name
source=load(S/'source-preflight-manifest.json')
with zipfile.ZipFile(io.BytesIO(read(S/'reference-source.zip'))) as z:
    assert sha(S/'reference-source.zip')==source['referenceSourceZipSha256'] and len(z.infolist())==394 and z.testzip() is None
    nodes=z.read('source/Src/FilterEngine/FilterExpressionNodes.cpp');assert hashlib.sha256(nodes).hexdigest()=='a2fcca950bd5e5cfa48f3ff43e4436ae0f5fd55eff9661da1d5a979cc9051135'
    spans={'toLocalDateTime':(1684,1691),'applyToScalarOrArray':(1693,1704),'StartOfWeekFunc':(2103,2117),'StartOfMonthFunc':(2119,2130),'StartOfYearFunc':(2132,2143)}
    for f in [pathlib.PurePosixPath(n) for n in closure.namelist() if n.startswith('original-pieces/') and n.endswith('.inc')]:
        if f.stem in spans:lo,hi=spans[f.stem];b=b''.join(nodes.splitlines(keepends=True)[lo-1:hi])
        else:b=z.read('original-pieces/'+f.name)
        assert read(f)==b,f.name
    assert read(S/'timestamp_original.hpp')==z.read('adapter/timestamp_original.hpp')
p=load(S/'native-execution-plan.json');go=load(S/p['requiredGoFileName']);assert go=={'approved':True,'authoritySha256':sha(S/'execution-authority.json'),'planSha256':sha(S/'native-execution-plan.json'),'supervisorSha256':sha(S/'native_executor.py'),'wrapperSha256':sha(S/'run_native_held.py'),'casePlanSha256':sha(S/p['casePlan']),'sourceManifestSha256':sha(S/p['sourceManifest']),'scope':p['scope'],'maxCommands':p['maxCommands']}
outer=load(S/'outer'/'parent-execute-v4-result.json');launch=load(S/'outer'/'parent-execute-v4-launch.json')
assert outer['pid']==launch['pid'] and outer['birthUtc']==launch['birthUtc'] and outer['exitCode']==0
assert all(outer[k] for k in ('terminal','heldUntilExit','stdoutComplete','stderrComplete')) and not outer['timedOut']
result=load(S/'execution-result.json');cases=load(S/p['casePlan'])['cases'];commands=p['compileCommands']+[p['linkCommand']]+p['nativeRunCommands'];assert len(result['receipts'])==4
captures=[];runhash=[]
for r,cmd in zip(result['receipts'],commands):
    assert r==load(S/'proof'/(r['id']+'.receipt.json')) and r['command']==cmd['command'] and r['id']==cmd['id']
    assert r['osExitCode']==0 and r['waitForSingleObjectResult']==0 and r['osExitFileTime']>=r['birthFileTime']>0
    assert all(r[k] for k in ('childCreated','birthCaptured','qualification','birthValidatedOnHeldHandleAfterExit','handleHeldThroughOsExitAndBothEof','jobAssignedBeforeResume')) and not r['drainErrors']
    for key in ('stdout','stderr'):
        x=r[key];f=S/'proof'/x['path'].replace(chr(92),'/').rsplit('/',1)[-1];assert x['eof'] and sha(f)==x['sha256'] and len(read(f))==x['size']
    if cmd['kind']=='native':
        rows=[json.loads(line) for line in read(S/'proof'/(r['id']+'.stdout.raw')).decode('utf-8').splitlines()];assert len(rows)==126 and sum(x['bodyDidRun'] for x in rows)==108
        for row,c in zip(rows,cases):
            assert (row['id'],row['function'],row['bodyDidRun'])==(c['id'],c['function'],c['bodyPlanned'])
            assert typed(row['input'])==descriptor(c['argument'])
            assert row['processId']==r['pid'] and int(row['processBirthFileTimeDecimal'])==r['birthFileTime']
            assert (row['userLcid'],row['userLocaleUtf8Hex'],row['timezoneState'],row['timezoneBiasMinutes'],row['pocoTimezoneTzdSeconds'])==(1041,'6a612d4a50',0,-540,32400)
            if not row['bodyDidRun']:assert row['status']=='not-run-static-guard' and row['result'] is None
        captures.append(rows);runhash.append(r['stdout']['sha256'])
ignored=set(p['stableCompareExclusions']);assert ignored=={'processId','processBirthFileTimeDecimal','clockBeforeEpochMicrosecondsInt64Decimal','clockAfterEpochMicrosecondsInt64Decimal'}
assert [{k:v for k,v in x.items() if k not in ignored} for x in captures[0]]==[{k:v for k,v in x.items() if k not in ignored} for x in captures[1]]
g=load(S/'native-golden-candidate.v4.json');assert len(g['entries'])==108 and len(g['excludedNoGolden'])==18 and not g['dynamicNulNativeMeasured']
assert g['excludedNoGolden']==[r['id'] for r in captures[0] if not r['bodyDidRun']]
entries=iter(g['entries'])
for line,(row,c) in enumerate(zip(captures[0],cases),1):
    if not row['bodyDidRun']:continue
    e=next(entries);assert e['id']==row['id'] and e['function']==row['function'] and e['inputDescriptor']==c['argument'] and e['status']==row['status'] and e['typedResult']==typed(row['result'])
    ex={'typeNameHex':row['exceptionTypeNameHex'],'nameHex':row['exceptionNameHex'],'messageHex':row['exceptionMessageHex']} if row['status']=='exception' else None
    assert e['bodyException']==ex and e['rawReferences']==[{'run':i+1,'sha256':runhash[i],'line':line} for i in range(2)] and not e['TimestampDisplayGoldenGenerated']
assert next(entries,None) is None
print(json.dumps({'status':'portable-actual-capture-closure-pass','sourceZipEntries':394,'rawRuns':2,'rowsEach':126,'bodyEach':108,'excludedNoGolden':18,'bodyExceptionCount':sum(r['status']=='exception' for r in captures[0]),'allInputsAndGoldenEqualRaw':True,'stableRowsEqual':True,'held4childrenExitBirthEOF':True,'naturalTokyoOnly':True,'dynamicNulMeasured':False,'clockFullASTQualified':False,'currentExternalToolsObjectsLibrariesChecked':False,'NativeReexecution':False}))

# 各source entryのサイズ／SHA／CRCとserializerの分離も固定する。
with zipfile.ZipFile(io.BytesIO(read('reference-source.zip'))) as source_zip:
    assert len(source_zip.namelist())==len(set(source_zip.namelist()))==394
    assert any(n.startswith('licenses/') for n in source_zip.namelist())
    for pin in fixed['referenceSourceEntryPins']:
        b=source_zip.read(pin['path']);info=source_zip.getinfo(pin['path'])
        assert len(b)==pin['size'] and info.CRC==pin['crc32'] and hashlib.sha256(b).hexdigest()==pin['sha256']
    for member in source_zip.infolist():
        b=source_zip.read(member);assert len(b)==member.file_size and zlib.crc32(b)&0xffffffff==member.CRC
serializer=[r for r in captures[0] if r['result'] and r['result']['kind']=='timestamp' and r['result'].get('serializedUtf8Hex','').startswith('53797374656d')]
assert len(serializer)==14 and all(r['status']=='ok' and int(r['result']['epochSignedInt64Decimal'])<0 for r in serializer)
assert g['serializerDisplayObservationsSeparated'] and not g['clockFullASTParserOptimizeQualified']
# 任意の実アプリselection証跡は明示pathだけ読む。存在しない証跡を成功と扱わない。
if len(sys.argv)>1:
    actual=json.loads(pathlib.Path(sys.argv[1]).read_bytes())
    executed={x['id'] for x in actual['selection'] if x['executed']}
    ids={x['id'] for x in g['entries']}
    assert executed==ids if actual['matchingNativeProfile'] else not executed
    assert actual['measured']==len(executed) and actual['nativeSkipped']==108-len(executed)
    assert actual['excludedNoGolden']==g['excludedNoGolden']
    print(json.dumps({'actualSelectionChecked':True,'executed':len(executed),'productResultsIndependentlyQualified':False}))
closure.close()
