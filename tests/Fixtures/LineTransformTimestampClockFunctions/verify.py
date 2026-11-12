"""Portable read-only evidence verifier: never launch/import actor executors or open actor paths."""
import pathlib,json,hashlib,zipfile,io,re,struct,stat,zlib,datetime,argparse
ROOT=pathlib.Path(__file__).resolve().parent
CAP=64*1024**2
FORBIDDEN='fullAST publicParser fullOwnerLayout ctor copyctor Optimize Mac DST clockBackward midnightCrossing runtimeApiFailure'.split()
def digest(b):return hashlib.sha256(b).hexdigest()
def js(b):return json.loads(b.decode('utf-8',errors='strict'))
def safe_name(n):
    p=pathlib.PurePosixPath(n);assert n and '\\' not in n and not p.is_absolute() and '..' not in p.parts and ':' not in n and all(x not in ('','.','artifacts') for x in p.parts),n
    assert str(p)==n,n

def verify(archive,fixed):
    for p in (archive,fixed):assert p.is_file() and not p.is_symlink() and not(getattr(p.stat(follow_symlinks=False),'st_file_attributes',0)&0x400),str(p)
    fx=js(fixed.read_bytes());assert fx['archive']['name']==archive.name and archive.stat().st_size==fx['archive']['size']<=CAP and digest(archive.read_bytes())==fx['archive']['sha256']
    assert digest(pathlib.Path(__file__).read_bytes())==fx['portableReaderSha256']
    with zipfile.ZipFile(archive) as z:
        inf=z.infolist();assert len(inf)==len(set(i.filename for i in inf))==len(set(i.filename.casefold() for i in inf))
        assert sum(i.file_size for i in inf)<=CAP
        for i in inf:safe_name(i.filename);assert not i.is_dir() and not stat.S_ISLNK(i.external_attr>>16) and i.file_size<=CAP
        assert z.testzip() is None
        manifestRaw=z.read('manifest.json');assert digest(manifestRaw)==fx['closureManifestSha256'];m=js(manifestRaw)
        assert set(z.namelist())=={'manifest.json'}|set(m['blobEntries'])
        for n,row in m['blobEntries'].items():
            raw=z.read(n);assert len(raw)==row['size'] and digest(raw)==row['sha256'] and zlib.crc32(raw)==row['crc32'] and n=='blobs/'+row['sha256']
        def blob(h):return z.read('blobs/'+h)
        def actor(path):return blob(m['actorBindings'][path])
        def actorjson(path):return js(actor(path))
        src=actorjson(m['inputs']['sourceManifest']);authority=actorjson(m['inputs']['authority']);plan=actorjson(m['inputs']['plan']);go=actorjson(m['inputs']['go']);case=actorjson(m['inputs']['casePlan']);outer=actorjson(m['inputs']['outerResult']);outerLaunch=actorjson(m['inputs']['outerLaunch']);result=actorjson(m['inputs']['executionResult']);post=blob(m['inputs']['postInputProofSha256']);post=js(post)
        assert digest(actor(m['inputs']['authority']))==fx['originalAuthoritySha256']=='d76a193fabb2a590bf1673888184665a58afa18cc62442f5d5bd75445edf6baa'
        assert digest(actor(m['inputs']['go']))=='24d2864a54b6c63a8d800b78858d93850abcce7d7a604e4bb5f4ebac0b74d50b'
        for n,h in authority['pins'].items():assert digest(actor(m['actorStage']+'\\'+n.replace('/','\\')))==h
        expected={'approved':True,'authoritySha256':digest(actor(m['inputs']['authority'])),'planSha256':digest(actor(m['inputs']['plan'])),'supervisorSha256':digest(actor(m['inputs']['supervisor'])),'wrapperSha256':digest(actor(m['inputs']['wrapper'])),'casePlanSha256':digest(actor(m['inputs']['casePlan'])),'sourceManifestSha256':digest(actor(m['inputs']['sourceManifest'])),'scope':plan['scope'],'maxCommands':plan['maxCommands']};assert go==expected
        assert outer['exitCode']==0 and outer['heldUntilExit'] and outer['stdoutComplete'] and outer['stderrComplete'] and not outer['timedOut'] and outer['pid']==outerLaunch['pid'] and outer['birthUtc']==outerLaunch['birthUtc']
        assert outer['arguments'][0]=='-B' and outer['arguments'][-1]=='--execute-parent-go' and result['compileCount']==result['linkCount']==1 and result['nativeRunCount']==2 and result['status']=='complete-raw-original-clock-capture'
        assert len(src['files'])==437 and len(m['sourceRows'])==437 and len(m['objects'])==35
        for x in src['files']:assert len(actor(x['path']))==x['size'] and digest(actor(x['path']))==x['sha256']
        reference=blob(m['inputs']['referenceSourceZipSha256']);assert digest(reference)==src['referenceSourceZipSha256']
        with zipfile.ZipFile(io.BytesIO(reference)) as rz:
            assert rz.testzip() is None and len(rz.namelist())==len(set(rz.namelist())) and sum(i.file_size for i in rz.infolist())<=CAP
            for i in rz.infolist():safe_name(i.filename);assert not stat.S_ISLNK(i.external_attr>>16)
        spans=actorjson(m['inputs']['spanManifest'])['spans'];assert len(spans)==11
        for x in spans:
            raw=actor(x['source']);part=actor(x['path']);assert digest(part)==x['sha256'] and len(part)==x['size']
            if 'startLine' in x:assert digest(raw)==x['sourceSHA256'] and hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()==x['sourceGitBlob'] and part==b''.join(raw.splitlines(keepends=True)[x['startLine']-1:x['endLine']])
            else:assert part==raw
        expectedObjs={x['path']:x for x in src['objects'] if x['includedInNewLink']};assert len(expectedObjs)==35
        for x in m['objects']:
            original=expectedObjs[x['path']];receipt=actorjson(original['producerReceipt']);assert digest(actor(original['producerReceipt']))==original['producerReceiptSha256']
            assert receipt['osExitCode']==0 and receipt['pid']==original['producerPid'] and receipt['birthFileTime']==original['producerBirthFileTime'] and receipt['birthValidatedOnHeldHandleAfterExit'] and receipt['handleHeldThroughOsExitAndBothEof'] and receipt['command']==original['producerCommand']
            assert any(o['sha256']==x['sha256'] and o['size']==x['size'] for o in receipt['outputs'])
            assert digest(actor(original['source']))==original['sourceSha256']
            for prefix in ('producerEnvironment','producerPlan'):assert digest(actor(original[prefix+'Path']))==original[prefix+'Sha256']
            for channel in ('stdout','stderr'):
                t=receipt[channel];assert t['eof'] and len(actor(t['path']))==t['size'] and digest(actor(t['path']))==t['sha256']
            binding=next(v for v in post['objectProducerBindings'] if v['object']==x['path']);assert binding['sha256']==x['sha256'] and binding['identity']['fileId']==original['statFileId']
        actualReads={x['path']:x for x in post['toolAndLibraryCurrentReads']}
        for x in list(src['toolPins'].values())+src['libraries']:
            assert actualReads[x['path']]['sha256']==x['sha256'] and actualReads[x['path']]['size']==x['size']
        assert len(src['toolPins'])==21 and len(src['libraries'])==7 and len(m['licenseRefs'])==5
        for x in m['licenseRefs']:assert digest(blob(x['sha256']))==x['sha256'] and len(blob(x['sha256']))==x['size']
        assert post['runBlockOnlyFinalCapacityDeltaQualified'] and not post['currentCounterAPIBytesUnchanged'] and post['currentWin32APIAndOwnershipUnchanged']
        env=actorjson(m['inputs']['environment']);assert env['values']['TEMP']==env['values']['TMP']==m['actorStage']+'\\output'
        assert '/utf-8' in plan['compileCommands'][0]['command'] and '/std:c++17' in plan['compileCommands'][0]['command'] and '/MD' in plan['compileCommands'][0]['command'] and plan['maxCommands']=={'compile':1,'link':1,'nativeRun':2}
        linked=[x for x in plan['linkCommand']['command'] if x.endswith('.obj')];assert len(linked)==len(set(linked))==36 and set(expectedObjs).issubset(linked)
        receipts=[actorjson(p) for p in m['actualReceiptPaths']];assert receipts==result['receipts'] and len(receipts)==4
        commands=plan['compileCommands']+[plan['linkCommand']]+plan['nativeRunCommands'];assert len(commands)==4
        for r,item in zip(receipts,commands):
            assert r['id']==item['id'] and r['command']==item['command'] and r['qualification'] and r['osExitCode']==0 and r['pythonExitCode'] is None and r['birthFileTime']>0 and r['osExitFileTime']>r['birthFileTime'] and r['birthValidatedOnHeldHandleAfterExit'] and r['handleHeldThroughOsExitAndBothEof'] and r['jobAssignedBeforeResume'] and r['createdSuspended'] and r['stopReason'] is None and not r['terminationRequested'] and r['executionApi']=='CreateProcessW/GetExitCodeProcess'
            assert r['freeBytesAtCompletion']>=10*1024**3 and r['bytesAdditionalAtCompletion']==r['bytesAdditional']<64*1024**2 and not r['drainErrors']
            for c in ('stdout','stderr'):
                t=r[c];data=actor(t['path']);assert t['eof'] and len(data)==t['size'] and digest(data)==t['sha256'];data.decode('utf-8',errors='strict')
            assert not actor(r['stderr']['path']) and r['sourcePreflightSha256']==digest(actor(m['inputs']['sourceManifest']))
        for r in receipts[:2]:
            for o in r['outputs']:assert len(actor(o['path']))==o['size'] and digest(actor(o['path']))==o['sha256']
        exe=actor(receipts[1]['outputs'][0]['path']);assert digest(exe)==result['exeSha256'] and exe[:2]==b'MZ';offset=struct.unpack_from('<I',exe,0x3c)[0];assert exe[offset:offset+4]==b'PE\0\0' and struct.unpack_from('<H',exe,offset+4)[0]==0x8664 and struct.unpack_from('<H',exe,offset+24)[0]==0x20b
        profiles=[];snapshots=[]
        def integer(v):assert isinstance(v,str) and re.fullmatch(r'-?(0|[1-9][0-9]*)',v);n=int(v);assert -(1<<63)<=n<(1<<63);return n
        def local(n,tzd):return datetime.datetime(1970,1,1)+datetime.timedelta(microseconds=n+tzd*1000000)
        for r in receipts[2:]:
            rows=[js(line) for line in actor(r['stdout']['path']).splitlines()];assert len(rows)==2;previous=None
            for row,c in zip(rows,case['cases']):
                assert row['id']==c['id'] and row['updateOrdinal']==c['explicitUpdateOrdinal'] and row['processId']==r['pid'] and int(row['processBirthFileTimeDecimal'])==r['birthFileTime'] and row['sourceCommit']=='6780924ba35f7f55af0f6f14dd302561175830a6' and row['originalExpressionCppSha256']=='ed97832b4287b54b5127fa55d3a0d5a9d2b5c5f34105be160f9eb81205ff589e' and row['originalNodesSha256']=='a2fcca950bd5e5cfa48f3ff43e4436ae0f5fd55eff9661da1d5a979cc9051135'
                assert row['qualified'] and row['updateBodyDidRun'] and row['functionBodyCalls']==6 and row['variantIndex']==4 and row['argsEmptyVector'] and row['publicParserArityQualified'] is False and row['contextLineIndex']==-1 and row['contextOtherPointersNull']
                p=row['profileBefore'];assert p==row['profileAfter'] and (p['userLcid'],p['userLocaleUtf8Hex'],p['timezoneState'],p['timezoneBiasMinutes'],p['timezoneStandardBiasMinutes'],p['timezoneDaylightBiasMinutes'],p['pocoTimezoneTzdSeconds'],p['timezoneStandardNameUtf8Hex'])==(1041,'6a612d4a50',0,-540,0,-60,32400,'e69db1e4baac2028e6a899e6ba96e6998229');profiles.append(p)
                before=integer(row['clockBeforeEpochMicrosecondsInt64Decimal']);after=integer(row['clockAfterEpochMicrosecondsInt64Decimal']);repeat=integer(row['repeatAfterEpochMicrosecondsInt64Decimal']);now=integer(row['nowEpochSignedInt64Decimal']);today=integer(row['todayEpochSignedInt64Decimal']);assert before<=now<=after<=repeat and (previous is None or previous<=before);previous=repeat
                assert [integer(n) for n in row['nowRepeatedEpochSignedInt64Decimal']]==[now]*3 and [integer(n) for n in row['todayRepeatedEpochSignedInt64Decimal']]==[today]*3
                dates=[local(n,p['pocoTimezoneTzdSeconds']) for n in (before,now,after,repeat,today)];assert all(d.date()==dates[0].date() for d in dates)
                for n,key in [(now,'localNow'),(today,'localToday'),(before,'localClockBefore'),(after,'localClockAfter')]:
                    d=local(n,p['pocoTimezoneTzdSeconds']);x=row[key];assert tuple(x[k] for k in ['year','month','day','hour','minute','second','millisecond','microsecond','tzdSeconds'])==(d.year,d.month,d.day,d.hour,d.minute,d.second,d.microsecond//1000,d.microsecond%1000,p['pocoTimezoneTzdSeconds'])
                midnight=dates[1].replace(hour=0,minute=0,second=0,microsecond=0);delta=midnight-datetime.datetime(1970,1,1);expected=(delta.days*86400+delta.seconds-p['pocoTimezoneTzdSeconds'])*1000000;assert today==expected and dates[-1].time()==datetime.time(0)
                assert row['clockMonotonic'] and not row['midnightCrossed'] and row['sameSnapshotStable'] and row['nowInClockInterval'] and row['todayLocalMidnight'];snapshots.append(row)
        assert len(profiles)==4 and all(p==profiles[0] for p in profiles)
        assert not result['goldenGenerated'] and not result['parserAstOptimizeQualified'] and case['twoRunExactEpochEqualityRequired'] is False and all(m['unmeasuredScopes'][k] is False for k in FORBIDDEN)
        return {'status':'portable-clock-closure-qualified','archiveSha256':fx['archive']['sha256'],'sourceRows':437,'rawSpans':11,'inheritedObjectReceiptJoins':35,'actualCommands':4,'nativeRuns':2,'snapshotRecords':4,'actualUpdateCalls':4,'actualNowCalls':12,'actualTodayCalls':12,'sameSnapshotAndDynamicRangeAndLocalMidnight':True,'twoRunExactEpochEqualityRequired':False,'localeProfile':'natural Windows ja-JP/Tokyo only','actualRootPidBirthOsExitBothEOF':[{'pid':r['pid'],'birthFileTime':r['birthFileTime'],'osExitCode':r['osExitCode'],'bothEOF':True} for r in receipts],'nativeExeSha256':result['exeSha256'],'actorPathsOpened':0,'NativeCompilerLinkReruns':0,'unmeasuredScopes':m['unmeasuredScopes']}
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--archive',type=pathlib.Path,default=ROOT/'clock-native-portable-v1.zip');p.add_argument('--fixed',type=pathlib.Path,default=ROOT/'clock-native-portable-fixed-v1.json');a=p.parse_args();print(json.dumps(verify(a.archive,a.fixed),ensure_ascii=False))