"""Read-only native-proof and actual public-process reader. No product execution."""
import re
import argparse,ast,collections,datetime,hashlib,json,pathlib,sys,zipfile
from projection import make_plan
R=pathlib.Path(__file__).resolve().parent
PROOF_SHA='ee9f13ee2e8de0684b376d97f8a1467a2591919f9ab1e0d2a49693c5c4e54a21'
GOLDEN_SHA='b5382bd3f03302881ea0e8aed98b5840928a69b4cb6c94bf539a91c831d3304d'
sha=lambda b:hashlib.sha256(b).hexdigest()
def req(ok,message):
    if not ok: raise AssertionError(message)
def load(p): return json.loads(pathlib.Path(p).read_bytes())
def utc_ticks(value, label):
    # .NET timestamps have 100 ns precision; do not truncate through microseconds.
    req(isinstance(value, str), 'UTC timestamp string ' + label)
    match = re.fullmatch(r'([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?(?:Z|\+00:00)', value)
    req(match is not None, 'canonical UTC timestamp ' + label)
    year, month, day, hour, minute, second = map(int, match.groups()[:6])
    date = datetime.datetime(year, month, day, hour, minute, second)
    seconds = ((date.toordinal() - 1) * 24 + hour) * 3600 + minute * 60 + second
    return seconds * 10_000_000 + int((match.group(7) or '').ljust(7, '0'))

def process_evidence(r, exitcode):
    req(r['platform'] == sys.platform and r['windows'] is (sys.platform == 'win32'), 'recorded actual platform')
    req(type(r['pid']) is int and r['pid'] > 0 and type(r['durationMilliseconds']) is int and r['durationMilliseconds'] >= 0, 'actual Pid and elapsed duration')
    req(type(r['exitCode']) is int and r['exitCode'] == exitcode and r['exitCode'] != -2, 'actual expected exit; no timeout')
    launch = utc_ticks(r['launchUtc'], 'launchUtc'); birth = utc_ticks(r['creationUtc'], 'creationUtc'); exit = utc_ticks(r['exitObservedUtc'], 'exitObservedUtc')
    req(launch <= exit and birth <= exit, 'actual launch/birth/exit ordering')
    evidence = r['launchEvidence']
    if sys.platform == 'win32':
        req(evidence is None, 'Windows Run has no Mac launcher evidence')
        req(launch <= birth and exit - launch <= (r['durationMilliseconds'] + 2000) * 10_000, 'Windows process launch/birth/duration')
    elif sys.platform == 'darwin':
        req(isinstance(evidence, dict) and type(evidence.get('ActualPid')) is int and evidence['ActualPid'] == r['pid']
            and type(evidence.get('RawExitCode')) is int and evidence['RawExitCode'] == exitcode
            and utc_ticks(evidence.get('ExitObservedUtc'), 'evidence.ExitObservedUtc') == exit
            and all(evidence.get(key) is True for key in ('Terminal', 'StdoutComplete', 'StderrComplete', 'PipesReleased', 'ProcessDisposed'))
            and type(evidence.get('CleanupIssues')) is list and evidence['CleanupIssues'] == [], 'Mac real child exit/EOF/cleanup')
    else: raise AssertionError('Unvalidated host platform: ' + sys.platform)

def native():
    b=(R/'native-proof.zip').read_bytes();req(sha(b)==PROOF_SHA,'fixed native proof SHA')
    with zipfile.ZipFile(R/'native-proof.zip') as z:
        req(z.testzip() is None,'all ZIP CRC')
        manifest=json.loads(z.read('native-proof-manifest.json'))
        req(set(z.namelist())=={e['path'] for e in manifest['entries']}|{'native-proof-manifest.json'},'exact archive entry set')
        for e in manifest['entries']:
            data=z.read(e['path']);req(len(data)==e['size'] and sha(data)==e['sha256'],'native entry '+e['path'])
        source=json.loads(z.read('source-manifest.json'))
        for e in source['sources']:
            data=z.read(pathlib.Path(e['path']).name+'.original')
            req(sha(data)==e['sha256'] and hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==e['blob'],'original source '+e['path'])
        for e in source['extractions']:
            original=z.read(pathlib.Path(e['path']).name+'.original')[e['startByte']:e['endByte']]
            req(sha(original)==e['sha256'] and z.read(e['file'])==original+(b'\n' if e.get('outerLf') else b''),'unmodified extraction '+e['file'])
        req(sha(z.read('COPYING'))==source['license']['sha256'],'native GPL license')
        cases=json.loads(z.read('cases.json'));req(len(cases)==2416 and len({c['id'] for c in cases})==2416,'all native inputs')
        # Execute only the retained reader's pure expected-value function. Never its main,
        # writes, process launch, compile commands, or candidate implementation.
        tree=ast.parse(z.read('verify.py'));node=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='expected')
        ns={};exec(compile(ast.Module(body=[node],type_ignores=[]),'<fixed-native-expected>','exec'),ns)
        goldens=[]
        for run in ('run1','run2'):
            data=z.read(run+'-golden.ndjson');req(sha(data)==GOLDEN_SHA,'native golden stdout SHA')
            receipt=json.loads(z.read(run+'-receipt.json')); comp=json.loads(z.read(run+'-compile-receipt.json'))
            req(receipt['exitCode']==0 and not receipt['timeout'] and receipt['heldProcessHandleSignaled'] and receipt['stdoutEof'] and receipt['stderrEof'],'native actual child completion')
            req(comp['exitCode']==0 and not comp['timeout'] and comp['stdoutEof'] and comp['stderrEof'],'native compile completion')
            req(sha(z.read(run+'.exe'))==receipt['executableSha256'] and sha(z.read('cases.json'))==receipt['casesSha256'] and z.read(run+'-reference.stderr')==b'','native child input/executable/stderr')
            fixed=json.loads(z.read('prepared-inputs.json'))
            req(comp['inputSha256']==fixed['inputSha256'] and all(sha(z.read(p))==h for p,h in comp['inputSha256'].items()),'native compile exact inputs')
            rows=[json.loads(x) for x in data.splitlines()];req(len(rows)==2416,'native output count')
            for c,g in zip(cases,rows):
                req(c['id']==g['id'] and (g['phase'],g['errorKind'],g['value'],g['trace'],g['optimizeTrace'])==ns['expected'](c),'typed/phase/trace native '+c['id'])
                req(g['context']==dict(dirs=c['dirs'],providerNull=True,sharedContextNull=True,lineIndex=-1),'native context')
            goldens.append(rows)
        req(goldens[0]==goldens[1],'independent native golden equality')
        return cases,{g['id']:g for g in goldens[0]}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--repo-root');parser.add_argument('--actual',type=pathlib.Path);a=parser.parse_args()
    fixed=load(R/'fixed-manifest.json')
    for p,h in fixed['files'].items(): req(sha((R/p).read_bytes())==h,'fixed fixture SHA '+p)
    cases,golden=native();plan,classification,gaps=make_plan(cases,golden)
    req(plan==load(R/'public-plan.json'),'public plan reconstructed from native golden')
    req(all(p['expression'].startswith('@opt ' if p['nativeOptimize'] else '@noopt ') for p in plan if p['nativeIds']),'native Optimize mode explicitly projected')
    req(classification==load(R/'native-classification.json') and gaps==load(R/'required-unimplemented.json'),'full native classification and mandatory gap')
    summary=dict(passed=True,nativeCases=2416,plannedCommands=len(plan),classification=dict(collections.Counter(c['scope'] for c in classification)),
        productExecuted=False,internalPhasePubliclyObserved=False,nativeFullAstEquivalent=False,actualTypedValueObserved=False,requiredUnimplemented=len(gaps),completeFeatureVerification=False)
    if a.actual is None: print(json.dumps(summary));return
    actual=load(a.actual);work=a.actual.resolve().parent;req(len(actual)==len(plan),'all planned product processes required')
    keyed={(r['count'],r['id']):r for r in actual};req(len(keyed)==len(plan),'unique product case identities')
    identities=set(); listpath=work/'empty-search.tsv'; sentinel=work/'existing-output.sentinel'
    req(listpath.read_bytes()==b'\tX\n' and sentinel.read_bytes()==b'existing output bytes remain unchanged\n' and not (work/'not-registered-and-absent.tsv').exists(),'list/sentinel/absent preservation')
    for count in (2,3):
        roots=[work/('sides-'+str(count))/('side-'+str(i)) for i in range(count)]
        for i,root in enumerate(roots):
            req({p.name for p in root.iterdir()}=={'row.bin','row.dir'} and (root/'row.bin').read_bytes()==bytes([i+1])*(i+1) and (root/'row.dir').is_dir() and not list((root/'row.dir').iterdir()),'fixed whole input tree')
    for p in plan:
        count=p['count'];r=keyed[count,p['id']];prefix=str(count)+'-'+p['id'];roots=[work/('sides-'+str(count))/('side-'+str(i)) for i in range(count)]
        filterpath=work/(prefix+'.flt');stdoutpath=work/(prefix+'.stdout');stderrpath=work/(prefix+'.stderr')
        req(r['expression']==p['expression'] and pathlib.Path(r['filter']).resolve()==filterpath and list(map(lambda x:pathlib.Path(x).resolve(),r['roots']))==roots,'actual paths/expression '+prefix)
        req(filterpath.read_bytes()==('def: exclude\n'+p['rule']+': '+p['expression']+'\n').encode(),'actual filter '+prefix)
        args=['--folder',str(roots[0]),str(roots[-1]),'--filter',str(filterpath),'--show-filtered']+(['--middle',str(roots[1])] if count==3 else [])
        req(r['arguments']==args,'exact product arguments '+prefix)
        protected={str(root/'row.bin') for root in roots}|{str(filterpath),str(listpath),str(sentinel)}
        req(set(r['protectedBefore'])==set(r['protectedAfter'])==protected and r['protectedBefore']==r['protectedAfter'] and all(sha(pathlib.Path(path).read_bytes())==h for path,h in r['protectedAfter'].items()),'all input/filter/list SHA '+prefix)
        req(pathlib.Path(r['stdout']).resolve()==stdoutpath and pathlib.Path(r['stderr']).resolve()==stderrpath,'stream paths')
        out=stdoutpath.read_bytes();err=stderrpath.read_bytes();req(sha(out)==r['stdoutSha256'] and sha(err)==r['stderrSha256'],'stream SHA')
        exitcode=2 if p['rejected'] else 1 if p['selected'] and p['rule'] in ('e','fe') else 0
        req(r['exitCode']==exitcode,'exact actual exit '+prefix)
        lines=err.decode('utf8').splitlines(); logs=[line for line in lines if line.startswith('info: ')]
        req(len(logs)==len(p['expectedLogTokens']) and all(line.startswith('info: '+token+' ') for line,token in zip(logs,p['expectedLogTokens'])),'runtime argument order/count '+prefix)
        diagnostics=[line for line in lines if not line.startswith('info: ')]
        if p['rejected']: req(out==b'' and diagnostics,'strict reject streams '+prefix)
        else:
            req(not diagnostics,'no unexpected stderr '+prefix)
            model=json.loads(out);entries=model['entries'];req(model['allEntryCount']==2 and len(entries)==2 and {e['path'] for e in entries}=={'row.bin','row.dir'} and model.get('threeWay',False)==(count==3),'whole row set '+prefix)
            for row in entries:
                isfile=row['path']=='row.bin';filtered=not p['selected'] or (p['rule']=='de' if isfile else p['rule']=='fe')
                req(row['filtered']==filtered and row['status']==('Uncompared' if filtered else 'Modified' if isfile else 'Equal'),'row status/filtered '+prefix)
                for i,name in enumerate(['left','right'] if count==2 else ['left','middle','right']):
                    side=row[name];req(pathlib.Path(side['path']).resolve()==roots[i]/row['path'] and side['kind']==('File' if isfile else 'Directory') and side['filtered']==filtered and side.get('error') is None,'all side path/kind/filter '+prefix)
                    if isfile: req(side['size']==i+1,'side size '+prefix)
        process_evidence(r,exitcode)
        identity=(r['pid'],r['creationUtc']);req(identity not in identities,'distinct child');identities.add(identity)
        req(all(r[k] is False for k in ('actualTypedValueObserved','nativeFullAstEquivalent','nativeExceptionEquivalent','propertyReadInstrumented','listReadInstrumented')),'qualification flags')
    summary.update(productExecuted=True,publicObservationsChecked=len(plan));print(json.dumps(summary))
if __name__=='__main__':main()
