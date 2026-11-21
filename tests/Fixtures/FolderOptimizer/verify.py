"""Read-only fixture/native schema/public process reader. Executes no product code."""
import argparse,collections,datetime,hashlib,importlib.util,json,pathlib,re,sys,zipfile
from projection import make_plan
R=pathlib.Path(__file__).resolve().parent
sha=lambda b:hashlib.sha256(b).hexdigest()
def req(ok,message):
    if not ok:raise AssertionError(message)
def load(path):return json.loads(pathlib.Path(path).read_bytes())
def exact(obj,keys,label):req(type(obj) is dict and set(obj)==set(keys),label)
MAC_STRINGS=('AppPath','NativeExecutable','LaunchExecutable','StartupGatePath','StartupGateSha256','GateReleaseObservedUtc','ActualOsBirthTimeUtc','ExitObservedUtc','LaunchNonce','WrapperReadinessLine','WrapperReadyObservedUtc','ProcessIdentityScope')
MAC_LISTS=('NativeArguments','LaunchArguments','CleanupIssues')
MAC_BOOLS=('GateReleased','WrapperReady','Terminal','StdoutComplete','StderrComplete','PipesReleased','ProcessDisposed')
MAC_INTS=('ActualPid','RawExitCode','CleanupGraceSeconds')
MAC_FIELDS=set(MAC_STRINGS+MAC_LISTS+MAC_BOOLS+MAC_INTS)
MAC_SCOPE='macos-wrapper-readiness-before-exec; native stdout excludes ready line; OS birth preserved by exec; release observes pipe flush; Terminal/Complete=false means unobserved termination or partial capture'
def ticks(value,label):
    req(type(value) is str,'UTC string '+label)
    m=re.fullmatch(r'([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?(?:Z|\+00:00)',value)
    req(m is not None,'canonical UTC '+label)
    y,mo,d,h,mi,s=map(int,m.groups()[:6]);dt=datetime.datetime(y,mo,d,h,mi,s)
    return (((dt.toordinal()-1)*24+h)*3600+mi*60+s)*10_000_000+int((m[7] or '').ljust(7,'0'))
def completion(row,exitcode):
    req(row['platform']==sys.platform and row['windows'] is (sys.platform=='win32'),'actual host platform')
    req(type(row['pid']) is int and row['pid']>0 and type(row['durationMilliseconds']) is int and row['durationMilliseconds']>=0,'strict PID/duration')
    req(type(row['exitCode']) is int and row['exitCode']==exitcode and row['exitCode']!=-2,'actual expected OS exit; no timeout')
    launch=ticks(row['launchUtc'],'launch');birth=ticks(row['creationUtc'],'birth');end=ticks(row['exitObservedUtc'],'exit')
    req(launch<=end and birth<=end,'process temporal order')
    e=row['launchEvidence']
    if sys.platform=='win32':
        req(e is None,'Windows producer has no Mac LaunchEvidence')
        req(launch<=birth and end-launch<=(row['durationMilliseconds']+2000)*10000,'Windows process lifetime')
    elif sys.platform=='darwin':
        req(type(e) is dict and type(e.get('ActualPid')) is int and e['ActualPid']==row['pid'],'Mac raw PID')
        exact(e,MAC_FIELDS,'finite source Mac CommandLaunchEvidence fields')
        for key in MAC_STRINGS:req(type(e[key]) is str,'Mac string field '+key)
        for key in MAC_LISTS:req(type(e[key]) is list and all(type(x) is str for x in e[key]),'Mac string list field '+key)
        for key in MAC_BOOLS:req(type(e[key]) is bool,'Mac Boolean field '+key)
        for key in MAC_INTS:req(type(e[key]) is int,'Mac integer field '+key)
        req(type(row.get('arguments')) is list and all(type(x) is str for x in row['arguments']),'Mac outer string arguments')
        req(pathlib.PurePosixPath(e['AppPath']).is_absolute(),'Mac absolute AppPath')
        gatepath=pathlib.PurePosixPath(e['StartupGatePath'])
        req(gatepath.is_absolute() and tuple(gatepath.parts[-3:])==('tests','DiffBeacon.E2E','process_startup_gate.py'),'Mac absolute source startup gate path')
        req(e['StartupGateSha256']=='760F968A8438F91441ACE6A7F6556B64D3CC54785A3825B9D1D98112A5CD074E','Mac fixed startup gate SHA')
        dll=e['AppPath'].lower().endswith('.dll');native='dotnet' if dll else e['AppPath'];nativeargs=([e['AppPath']] if dll else [])+row['arguments']
        req(e['NativeExecutable']==native and e['NativeArguments']==nativeargs,'Mac derived native executable/arguments tuple')
        req(len(e['LaunchExecutable'])>0,'Mac nonempty caller Python executable text')
        req(e['LaunchArguments']==['-B','-X','utf8',e['StartupGatePath'],e['LaunchNonce'],native]+nativeargs,'Mac derived Python gate launch arguments')
        req(e['CleanupGraceSeconds']==5 and e['ProcessIdentityScope']==MAC_SCOPE,'Mac source grace and identity scope')
        req(type(e.get('RawExitCode')) is int and e['RawExitCode']==exitcode and ticks(e.get('ExitObservedUtc'),'Mac exit')==end,'Mac actual exit')
        req(ticks(e.get('ActualOsBirthTimeUtc'),'Mac OS birth')==birth,'Mac OS birth matches recorded creationUtc')
        req(all(e.get(k) is True for k in ('GateReleased','WrapperReady')),'Mac ready and gate released')
        ready=ticks(e.get('WrapperReadyObservedUtc'),'Mac wrapper ready');gate=ticks(e.get('GateReleaseObservedUtc'),'Mac gate release')
        req(launch<=birth<=ready<=gate<=end,'Mac launch/birth/ready/gate temporal order')
        nonce=e.get('LaunchNonce')
        req(type(nonce) is str and re.fullmatch('[0-9a-f]{32}',nonce) is not None,'Mac launch nonce format')
        req(type(e.get('WrapperReadinessLine')) is str and e['WrapperReadinessLine']=='DIFFBEACON_E2E_GATE_READY_V2\t'+nonce+'\t'+str(row['pid']),'Mac readiness nonce/PID line')
        req(all(e.get(k) is True for k in ('Terminal','StdoutComplete','StderrComplete','PipesReleased','ProcessDisposed')),'Mac real EOF and release')
        req(type(e.get('CleanupIssues')) is list and not e['CleanupIssues'],'Mac cleanup')
    else:raise AssertionError('host not qualified: '+sys.platform)

def native():
    binding=load(R/'proof-binding.json')
    req(sha((R/'native-proof.zip').read_bytes())==binding['nativeProofSha256'],'fixed proof ZIP')
    with zipfile.ZipFile(R/'native-proof.zip') as z:
        req(z.testzip() is None,'whole proof CRC')
        manifest=json.loads(z.read('proof-manifest.json'))
        req(len(z.namelist())==len(set(z.namelist())),'no duplicate ZIP member')
        req(set(z.namelist())=={x['path'] for x in manifest['entries']}|{'proof-manifest.json'},'whole exact proof entry set')
        for item in manifest['entries']:
            data=z.read(item['path']);req(len(data)==item['size'] and sha(data)==item['sha256'],'proof entry '+item['path'])
        for path,key in [('cases.json','nativeCasesSha256'),('prepared-inputs.json','nativeInputFreezeSha256'),('post-capture-r2/post-reader-chain.json','postReaderChainSha256'),('post-capture-r2/strict-reader.receipt.json','postReaderReceiptSha256')]:
            req(sha(z.read(path))==binding[key],'qualification binding '+path)
        prepared=json.loads(z.read('prepared-inputs.json'))
        for name,h in prepared['inputSha256'].items():req(sha(z.read(name))==h,'compile closure '+name)
        source=json.loads(z.read('source-manifest.json'));req(source['commit']==binding['sourceCommit'],'source commit')
        for item in source['files']:
            data=z.read(item['file']);req(len(data)==item['size'] and sha(data)==item['sha256'],'source bytes '+item['file'])
            req(hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==item['blob'],'Git source blob '+item['file'])
        nativeIdentities=[];compileIdentities=[]
        for run in ('run1','run2'):
            data=z.read(run+'/native.ndjson');req(sha(data)==binding['nativeOutputSha256'] and data==z.read(run+'/native.stdout'),'whole native stream '+run)
            e=json.loads(z.read(run+'/native.receipt.json'))
            req(type(e['exitCode']) is int and e['exitCode']==0 and e['timeout'] is False,'native actual exit '+run)
            req(type(e['pid']) is int and e['pid']>0 and type(e['birthFileTime']) is int and e['birthFileTime']>0,'native strict identity')
            req(all(e[k] is True for k in ('heldProcessHandleSignaled','createdSuspended','jobAssignedBeforeResume','resumeSucceeded','stdoutEof','stderrEof')),'native terminal/job/EOF')
            req(type(e['resumePreviousSuspendCount']) is int and e['resumePreviousSuspendCount']==1,'native Job before resume')
            req(e['preparedInputsSha256']==binding['nativeInputFreezeSha256'] and e['casesSha256']==binding['nativeCasesSha256'],'native immutable inputs')
            req(sha(z.read(run+'/reference.exe'))==e['executableSha256'],'actual native executable')
            req(z.read(run+'/native.stderr')==b'' and e['stdoutSha256']==sha(data),'native complete streams')
            nativeIdentities.append((e['pid'],e['birthFileTime']))
            compiled=json.loads(z.read(run+'/compile-receipt.json'))
            req(type(compiled['exitCode']) is int and compiled['exitCode']==0 and compiled['timeout'] is False and compiled['stdoutEof'] is True and compiled['stderrEof'] is True,'completed native compilation')
            req(compiled['preparedInputsSha256']==binding['nativeInputFreezeSha256'] and compiled['executableSha256']==e['executableSha256'],'compiled executable binding')
            for stage in ('pcre','cpp','link'):
                receipt=json.loads(z.read(run+'/'+stage+'-process.receipt.json'))
                req(type(receipt['pid']) is int and receipt['pid']>0 and type(receipt['exitCode']) is int and receipt['exitCode']==0 and receipt['timeout'] is False,'strict compiler child exit')
                req(all(receipt[k] is True for k in ('heldProcessHandleSignaled','createdSuspended','jobAssignedBeforeResume','resumeSucceeded','stdoutEof','stderrEof')),'compiler child Job and EOF')
                req(receipt['stdoutSha256']==sha(z.read(run+'/'+stage+'-process.stdout')) and receipt['stderrSha256']==sha(z.read(run+'/'+stage+'-process.stderr')),'compiler whole streams')
                if stage=='cpp':compileIdentities.append((receipt['pid'],receipt['birthFileTime']))
        req(len(set(nativeIdentities))==len(set(compileIdentities))==2,'two independent native compiles and children')
        chain=json.loads(z.read('post-capture-r2/post-reader-chain.json'))
        for name,h in chain['nativeEvidence'].items():req(sha(z.read(name))==h,'r2 fixed native evidence '+name)
        for name,h in chain['postReaderInputs'].items():req(sha(z.read('post-capture-r2/'+name))==h,'r2 reader inputs '+name)
        e=json.loads(z.read('post-capture-r2/strict-reader.receipt.json'))
        req(type(e['exitCode']) is int and e['exitCode']==0 and e['timeout'] is False,'recorded r2 strict reader exit')
        req(all(e[k] is True for k in ('heldProcessHandleSignaled','createdSuspended','jobAssignedBeforeResume','resumeSucceeded','stdoutEof','stderrEof')),'recorded r2 reader terminal/EOF')
        req(e['stdoutSha256']==sha(z.read('post-capture-r2/strict-reader.stdout')) and z.read('post-capture-r2/strict-reader.stderr')==b'','recorded r2 reader streams')
        req(json.loads(z.read('post-capture-r2/strict-reader.stdout'))['passed'] is True,'recorded r2 final qualification')
        cases=json.loads(z.read('cases.json'));rows=[json.loads(x) for x in z.read('run1/native.ndjson').splitlines()]
        req(len(cases)==len(rows)==6756 and len({x['id'] for x in rows})==6756,'all fixed native cases')
        spec=importlib.util.spec_from_file_location('finite_schema',R/'finite-schema.py');schema=importlib.util.module_from_spec(spec);spec.loader.exec_module(schema)
        functions={}
        for name in z.namelist():
            if name.startswith('table-row-') and name.endswith('.inc'):
                match=re.search(r'\{"([a-z]+)",\s*\w+,\s*(-?\d+),\s*(-?\d+)\}',z.read(name).decode())
                if match:functions[match[1]]=(int(match[2]),int(match[3]))
        for prefix in ('','left','middle','right'):
            for name in ('lineat','lineoffsetat','column','columnrange','columnat','columnoffsetat'):
                functions[prefix+name]=(2,2) if name in ('columnat','columnoffsetat') else (1,1)
        tokens={k:int(v) for k,v in re.findall(r'#define TK_(\w+)\s+(\d+)',z.read('source/Src/FilterEngine/FilterParser.h').decode())}
        operators={tokens[n] for n in ('EQ','NE','LT','LE','GT','GE','CONTAINS','RECONTAINS','LIKE','MATCHES','PLUS','MINUS','STAR','SLASH','MOD')}
        def ids(n):return {n['id']}|set().union(*(ids(c) for c in n.get('children',[])))
        for case,row in zip(cases,rows):schema.row_schema(row,case,functions,operators,ids(case['tree']))
        return cases,{row['id']:row for row in rows}

FIELDS={'id','count','dataset','expression','filter','roots','arguments','list','exitCode','stdout','stderr','stdoutSha256','stderrSha256','protectedBefore','protectedAfter','pid','creationUtc','launchUtc','exitObservedUtc','durationMilliseconds','platform','windows','launchEvidence','actualTypedValueObserved','nativeFullAstEquivalent','nativeExceptionEquivalent','compilePhaseObserved','listReadInstrumented'}
def actual(plan,path):
    rows=load(path);work=path.resolve().parent
    needed=[p for p in plan if not p.get('windowsOnly') or sys.platform=='win32']
    req(type(rows) is list and len(rows)==len(needed),'all planned observations for actual OS')
    keyed={(r['count'],r['id']):r for r in rows};req(len(keyed)==len(rows),'unique observations')
    identities=set();sentinel=work/'existing-output.sentinel';req(sentinel.read_bytes()==b'existing output bytes remain unchanged\n','sentinel bytes')
    for p in needed:
        count=p['count'];identity=p['id'];r=keyed[count,identity];exact(r,FIELDS,'finite actual observation fields')
        req(type(r['count']) is int and r['count']==count and type(r['id']) is str,'strict case identity')
        dataset=p.get('dataset','all-present');roots=[work/f'data-{dataset}-{count}'/f'side-{i}' for i in range(count)]
        expression=p['expression'];listpath=work/f'{count}-{identity}.list' if p['listFixture'] else None
        if listpath:
            req(listpath.read_bytes()==b'a\tfirst\n','immutable first list fixture');expression=expression.replace('$LIST',str(listpath).replace('"','""'))
        prefix=f'{count}-{identity}';filterpath=work/(prefix+'.flt');stdoutpath=work/(prefix+'.stdout');stderrpath=work/(prefix+'.stderr')
        req(r['dataset']==dataset and r['expression']==expression and pathlib.Path(r['filter']).resolve()==filterpath,'actual expression/filter/data')
        req(filterpath.read_bytes()==('def: exclude\n'+p['rule']+': '+expression+'\n').encode(),'whole actual filter bytes')
        req([pathlib.Path(x).resolve() for x in r['roots']]==roots,'actual roots')
        req((pathlib.Path(r['list']).resolve() if r['list'] else None)==listpath,'actual list path')
        args=['--directory',str(roots[0]),str(roots[-1]),'--filter',str(filterpath),'--show-filtered']+(['--middle',str(roots[1])] if count==3 else [])
        req(r['arguments']==args,'exact product arguments')
        protected={str(filterpath),str(sentinel)}|({str(listpath)} if listpath else set())
        for i,root in enumerate(roots):
            names={'row.bin'} if dataset=='kind-collision' else {'row.dir'}|({'row.bin'} if dataset!='missing-side' or i==count-1 else set())
            req({f.name for f in root.iterdir()}==names,'whole root entry set')
            if dataset=='kind-collision' and i==count-1:req((root/'row.bin').is_dir() and not list((root/'row.bin').iterdir()),'collision directory original')
            elif 'row.bin' in names:
                req((root/'row.bin').read_bytes()==bytes([i+1]),'whole input file bytes');protected.add(str(root/'row.bin'))
            if 'row.dir' in names:req((root/'row.dir').is_dir() and not list((root/'row.dir').iterdir()),'empty common directory')
        req(type(r['protectedBefore']) is dict and type(r['protectedAfter']) is dict and set(r['protectedBefore'])==set(r['protectedAfter'])==protected,'exact protected input set')
        req(r['protectedBefore']==r['protectedAfter'] and all(type(h) is str and re.fullmatch('[0-9a-f]{64}',h) and sha(pathlib.Path(f).read_bytes())==h for f,h in r['protectedAfter'].items()),'all input/filter/list/sentinel SHA')
        req(pathlib.Path(r['stdout']).resolve()==stdoutpath and pathlib.Path(r['stderr']).resolve()==stderrpath,'stream paths')
        out=stdoutpath.read_bytes();err=stderrpath.read_bytes();req(sha(out)==r['stdoutSha256'] and sha(err)==r['stderrSha256'],'whole stream SHA')
        expectedExit=2 if p['rejected'] else 1 if p['selected'] else 0;completion(r,expectedExit)
        ident=(r['pid'],r['creationUtc']);req(ident not in identities,'distinct real process');identities.add(ident)
        for k in ('actualTypedValueObserved','nativeFullAstEquivalent','nativeExceptionEquivalent','compilePhaseObserved','listReadInstrumented'):req(r[k] is False,'no inflated qualification '+k)
        if p['rejected']:req(out==b'' and err!=b'','strict public failure streams');continue
        req(err==b'','successful public stderr empty')
        model=json.loads(out);exact(model,{'allEntryCount','entries'}|({'threeWay','middleRoot'} if count==3 else set()),'finite folder model root')
        names={'row.bin'} if dataset=='kind-collision' else {'row.bin','row.dir'}
        req(type(model['allEntryCount']) is int and model['allEntryCount']==len(names) and type(model['entries']) is list and len(model['entries'])==len(names) and {e['path'] for e in model['entries']}==names,'whole visible row set')
        if count==3:req(model['threeWay'] is True and pathlib.Path(model['middleRoot']).resolve()==roots[1],'three-way metadata')
        for entry in model['entries']:
            exact(entry,{'path','status','kind','differenceKind','filtered','left','right','children'}|({'middle','presence','classification','middleLeft','middleRight','leftRight'} if count==3 else set()),'finite row DTO')
            req(entry['status'] in ('Equal','Modified','LeftOnly','RightOnly','TypeConflict','Uncompared','MiddleOnly') and entry['kind'] in ('File','Directory') and entry.get('error') is None,'finite row status/kind/no error')
            req(entry['differenceKind']==entry['status'] and entry['children']==[],'row status projection and empty children')
            if count==3:
                req(type(entry['presence']) is int and entry['presence']==(4 if dataset=='missing-side' and entry['path']=='row.bin' else 7),'exact physical presence')
                req(entry['classification'] in (None,'AllChanged','OnlyLeft','OnlyMiddle','OnlyRight'),'finite classification')
                for pair in ('middleLeft','middleRight','leftRight'):
                    exact(entry[pair],{'status'},'finite error-free pair DTO')
                    req(entry[pair]['status'] in ('Equal','Modified','LeftOnly','RightOnly','TypeConflict','Uncompared','MiddleOnly'),'finite pair status')
            flags=[]
            for i,key in enumerate(('left','right') if count==2 else ('left','middle','right')):
                present=not(dataset=='missing-side' and entry['path']=='row.bin' and i<count-1)
                side=entry[key]
                if not present:req(side is None,'missing side remains JSON null');continue
                exact(side,{'path','kind','size','lastWriteTimeUtc','filtered','scan'},'finite side DTO')
                directory=entry['path']=='row.dir' or dataset=='kind-collision' and i==count-1
                filtered=not p['selected'] or (p['rule']!='e' and (directory if p['rule']=='fe' else not directory))
                flags.append(filtered)
                req(pathlib.Path(side['path']).resolve()==roots[i]/entry['path'] and side['kind']==('Directory' if directory else 'File'),'all side paths/kinds')
                req(type(side['size']) is int and side['size']==(0 if directory else 1) and side['filtered'] is filtered,'all side size/filter values')
                req(side['scan']==('Unscanned' if filtered else 'Scanned') if directory else side['scan']=='NotApplicable','all side scan states')
                ticks(side['lastWriteTimeUtc'],'side time')
            req(entry['filtered'] is all(flags),'row filtered is derived from every present side')

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--actual',type=pathlib.Path);args=parser.parse_args()
    fixed=load(R/'fixed-manifest.json')
    req(fixed['productExecuted'] is False and fixed['fullParserNativeExecuted'] is False,'pre-execution fixture qualification')
    req(set(fixed['files'])=={p.name for p in R.iterdir() if p.is_file() and p.name!='fixed-manifest.json'},'whole exact fixture file set')
    for name,h in fixed['files'].items():req(sha((R/name).read_bytes())==h,'fixed fixture SHA '+name)
    source_support=load(R/'source-support.json')
    with zipfile.ZipFile(R/'source-support.zip') as z:
        req(z.testzip() is None and set(z.namelist())=={x['path'] for x in source_support['files']},'supplemental source ZIP closure')
        for item in source_support['files']:
            b=z.read(item['path']);req(len(b)==item['size'] and sha(b)==item['sha256'],'supplemental source SHA')
            req(hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==item['gitBlobSha1'],'supplemental source blob')
    runner=load(R/'runner-source-chain.json')
    req(runner['actualRunnerBinaryBound'] is False and runner['actualOsProducerExecuted'] is False and runner['allLocalFunctionAndRecordTokensIdentical'] is True,'static runner qualification only')
    req(sha((R/'runner-source-proof.zip').read_bytes())==runner['zipSha256'],'runner proof ZIP binding')
    with zipfile.ZipFile(R/'runner-source-proof.zip') as z:
        req(z.testzip() is None and len(z.namelist())==len(set(z.namelist())) and set(z.namelist())=={x['path'] for x in runner['entries']},'whole runner proof closure')
        for item in runner['entries']:
            b=z.read(item['path']);req(len(b)==item['size'] and sha(b)==item['sha256'],'runner proof bytes '+item['path'])
        req(sha(z.read('original/Program.cs'))==runner['originalProgramSha256'] and sha(z.read('candidate/Program.cs'))==runner['candidateProgramSha256'],'old/new whole Program SHA')
        req(json.loads(z.read('producer-tokens.json'))==runner['allLocalFunctionsAndRecords'],'static producer token proof binding')
    cases,golden=native();plan,classification=make_plan(cases,golden)
    req(plan==load(R/'public-plan.json') and classification==load(R/'native-classification.json'),'source/golden plan reconstruction')
    if args.actual:actual(plan,args.actual)
    print(json.dumps(dict(passed=True,nativeRows=6756,independentNativeCompiles=2,publicPlan=len(plan),nativeBound=sum(bool(x['nativeIds']) for x in plan),sourceDerived=sum(not x['nativeIds'] for x in plan),excluded=collections.Counter(x['scope'] for x in classification if not x['publicCandidate']),productExecuted=bool(args.actual),fullParserNativeExecuted=False,actualTypedValueObserved=False,nativeFullAstEquivalent=False,compilePhaseObserved=False,listReadInstrumented=False,requiredHostChecksRemain=True)))
if __name__=='__main__':main()
