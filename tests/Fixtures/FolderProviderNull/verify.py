"""固定native原本とFolder CLI選択投影を独立照合する。製品は起動しない。"""
import sys
sys.dont_write_bytecode = True
import argparse, datetime, re, hashlib, json, pathlib, subprocess, zipfile

BASE = pathlib.Path(__file__).resolve().parent
PROOF_SHA = '48fdbeda29a72c4e3515ef83951e2f3400495a64d05afd375c0ddcf4f72b111a'
sha = lambda data: hashlib.sha256(data).hexdigest()
def req(value, message):
    if not value:
        raise ValueError(message)
def load(path):
    return json.loads(path.read_bytes())
def quote(value):
    return "'" + str(value).replace('\\', '\\\\').replace("'", "\\'") + "'"
def validate_filter_bytes(data, rule, expression, directive):
    req(directive == '@noopt', 'runtime-body projection requires explicit @noopt')
    req(data == ('def: exclude\n'+rule+': '+directive+' '+expression+'\n').encode('utf-8'), 'exact @noopt filter bytes')
def utc_ticks(value, label):
    # .NET timestamps have 100 ns precision; do not truncate through microseconds.
    req(isinstance(value, str), 'UTC timestamp string ' + label)
    match = re.fullmatch(r'([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?(?:Z|\+00:00)', value)
    req(match is not None, 'canonical UTC timestamp ' + label)
    year, month, day, hour, minute, second = map(int, match.groups()[:6])
    date = datetime.datetime(year, month, day, hour, minute, second)
    seconds = ((date.toordinal() - 1) * 24 + hour) * 3600 + minute * 60 + second
    return seconds * 10_000_000 + int((match.group(7) or '').ljust(7, '0'))

def validate_launch(row, expected_exit, platform):
    req(platform == ('windows' if sys.platform == 'win32' else 'macos' if sys.platform == 'darwin' else None), 'reader actual host platform')
    req(row.get('platform') == platform and type(row.get('pid')) is int and row['pid'] > 0, 'actual platform/PID')
    req(type(row.get('exitCode')) is int and row['exitCode'] == expected_exit and row['exitCode'] != -2, 'actual expected exit; timeout -2 rejected')
    creation = utc_ticks(row.get('creationUtc'), 'creationUtc')
    launch = utc_ticks(row.get('launchUtc'), 'launchUtc'); exit_time = utc_ticks(row.get('exitObservedUtc'), 'exitObservedUtc')
    duration = row.get('durationMs')
    req(type(duration) is int and duration >= 0, 'actual nonnegative duration')
    req(launch <= exit_time and creation <= exit_time and creation >= launch - 20_000_000, 'launch/birth/exit ordering')
    req(abs(duration * 10_000 - (exit_time - launch)) <= 20_000_000, 'elapsed/duration consistency')
    if platform == 'windows':
        req('launchEvidence' in row and row['launchEvidence'] is None, 'Windows evidence must remain null')
    else:
        e = row.get('launchEvidence')
        req(isinstance(e, dict) and type(e.get('ActualPid')) is int and e['ActualPid'] == row['pid']
            and type(e.get('RawExitCode')) is int and e['RawExitCode'] == expected_exit
            and utc_ticks(e.get('ExitObservedUtc'), 'evidence.ExitObservedUtc') == exit_time
            and all(e.get(key) is True for key in ('Terminal', 'StdoutComplete', 'StderrComplete', 'PipesReleased', 'ProcessDisposed'))
            and type(e.get('CleanupIssues')) is list and e['CleanupIssues'] == [], 'actual Mac terminal/pipe EOF/cleanup evidence')

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--repo-root', type=pathlib.Path, required=True)
    ap.add_argument('--actual', type=pathlib.Path)
    ap.add_argument('--platform', choices=['windows','macos'], default='windows' if sys.platform == 'win32' else 'macos')
    args = ap.parse_args()
    repo = args.repo_root.resolve()
    req(args.platform == ('windows' if sys.platform == 'win32' else 'macos' if sys.platform == 'darwin' else None), 'reader platform must match actual host')
    req((repo/'DiffBeacon.slnx').is_file(), 'real repository root')
    fixed = load(BASE/'fixed-manifest.json')
    req({p.name for p in BASE.iterdir() if p.is_file()} == set(fixed)|{'fixed-manifest.json'}, 'fixture exact fileset')
    for name, expected in fixed.items():
        data = (BASE/name).read_bytes()
        req(len(data) == expected['size'] and sha(data) == expected['sha256'], 'fixed SHA/size '+name)
    req(sha((BASE/'native-folder-null-proof.zip').read_bytes()) == PROOF_SHA, 'pinned original native proof')
    preparation = load(BASE/'property-preparation-source.json')
    req(len(preparation) == 3, 'three source-only property preparation spans')
    for evidence in preparation:
        source = (repo/evidence['path']).read_bytes()
        span = b''.join(source.splitlines(keepends=True)[evidence['startLine']-1:evidence['endLine']])
        req(sha(source) == evidence['sourceSha256'] and sha(span) == evidence['spanSha256'] and span == evidence['spanUtf8'].encode('utf-8'), 'separate source-only property preparation '+evidence['path'])
    runner = load(BASE/'runner-source.json')
    # runner（E2E Program.cs）は他の検証追加で頻繁に変わる。資格に必要なのは待機・stream・時刻記録のspanだけなので、
    # ファイル全体のSHAと行番号は照合しない（sourceSha256／startLine／endLineは採取時点の記録として残す）。
    # autocrlfのcheckoutとLFの作業コピー、macOSのLF checkoutで結果が変わらないよう、改行をLFへ正規化して照合する。
    req(runner['signedAttestation'] is False, 'runner source-only qualification')
    source = (repo/runner['path']).read_bytes().replace(b'\r\n', b'\n')
    for evidence in runner['spans']:
        recorded = evidence['spanUtf8'].encode('utf-8')
        req(sha(recorded) == evidence['spanSha256'], 'recorded runner span SHA')
        req(source.count(recorded.replace(b'\r\n', b'\n')) == 1, 'runner wait/stream/timestamp source span present exactly once')
    child = subprocess.run([sys.executable,'-E','-B','-X','utf8',str(BASE/'verify-native.py'),'--repo-root',str(repo)], capture_output=True, check=False)
    req(child.returncode == 0 and child.stderr == b'', 'unchanged native reader: '+child.stdout.decode('utf-8')+child.stderr.decode('utf-8'))
    native_receipt = json.loads(child.stdout)
    req(native_receipt['nativeCases'] == 159 and native_receipt['contextTriadObserved'] and native_receipt['allArgumentsUnevaluated'], 'native phase qualification')
    with zipfile.ZipFile(BASE/'native-folder-null-proof.zip') as archive:
        functions = json.loads(archive.read('functions.json'))
        cases = json.loads(archive.read('cases.json'))
        projections = json.loads(archive.read('public-projection.json'))
        golden = {r['id']: r for r in map(json.loads, archive.read('run1-golden.ndjson').splitlines())}
    req(len(functions) == 15 and {f['name'] for f in functions} >= {'matchdistancebefore','matchdistanceafter'}, 'canonical names')
    by_name = {f['name']: f for f in functions}
    plan = load(BASE/'public-plan.json')
    by_id = {p['id']: p for p in plan}
    req(len(plan) == len(by_id) == 432, '432 unique planned commands per side count')
    req(all(p['optimizationDirective'] == '@noopt' and p['nativePhaseQualification'] == 'native-function-body-only' and p['nativeOptimizedPhaseQualified'] is False and p['nativeDefaultOptimizationQualified'] is False for p in plan), 'all 864 runtime-body projections explicitly @noopt; optimized/default phase unqualified')
    mapping = load(BASE/'projection-map.json')
    req(len(mapping) == 159 and [m['nativeCaseId'] for m in mapping] == [c['id'] for c in cases], '159 native mapping/order')
    for projection, m in zip(projections, mapping):
        identity = projection['nativeCaseId']; expression = projection['expression']
        req(m['optimizationDirective'] == '@noopt' and m['nativePhaseQualification'] == 'native-function-body-only' and m['nativeOptimizedPhaseQualified'] is False and m['nativeDefaultOptimizationQualified'] is False, 'projection map phase qualification '+identity)
        req(m['function'] == projection['canonicalFunction'] and m['publicExpression'] == expression and not m['publicRuntimeExecuted'] and not m['exactNativeAstProjection'], 'qualified native mapping '+identity)
        if expression is None:
            req(m['publicPlanId'] is None and 'native-'+identity not in by_id, 'controlled AST probe unprojected')
        else:
            p = by_id['native-'+identity]
            req(m['publicPlanId'] == p['id'] and p['expression'] == expression+' == None' and p['nativeIds'] == [identity] and p['scope'] == 'native-none-public-notation', 'native None public notation '+identity)
            req(golden[identity]['value'] == {'kind':'none'}, 'original None expectation '+identity)
    req(sum(m['publicExpression'] is not None for m in mapping) == 114, '114 public notations / 45 probes unprojected')
    for f in functions:
        name = f['name']
        req(all(name+'-'+s in by_id for s in ['rule-e','rule-de','neq','lt-not','gt-not','if','direct','case','inner-log','outer-log','property-child','external-list-child','throw-child']), 'derived function coverage '+name)
        req(by_id[name+'-property-child']['windowsPropertyPreparation'] and all(not p['windowsPropertyPreparation'] for p in plan if p['id'] != name+'-property-child' and p['id'].startswith(name+'-') and not p['id'].endswith('-property-child')), 'Windows property preparation is a separate contract '+name)
        req(all(name+'-reject-'+s in by_id and by_id[name+'-reject-'+s]['rejected'] for s in ['zero','excess','missing-comma','trailing-comma','missing-close','unknown-child','malformed-nested','bare']), 'strict parser coverage '+name)
        observed_arities = {c['arity'] for c in cases if c['function'] == name and 'native-'+c['id'] in by_id}
        req(observed_arities == {f['minArgs'],f['maxArgs']}, 'native min/max public coverage '+name)
    req(all(not p['nativeFullAstEquivalent'] and not p['nativeExceptionEquivalent'] and not p['actualTypedValueObserved'] for p in plan), 'no native AST/exception/typed-value overclaim')
    failure_matrix = load(BASE/'failure-matrix.json')
    req(len(failure_matrix['cases']) == 432 and all(p['optimizationDirective'] == '@noopt' and p['nativePhaseQualification'] == 'native-function-body-only' and p['nativeOptimizedPhaseQualified'] is False and p['nativeDefaultOptimizationQualified'] is False for p in [failure_matrix]+failure_matrix['cases']), 'failure matrix uses same noopt phase qualification')
    result = dict(passed=True, platform=args.platform, nativeProof=native_receipt, publicNotations=114, unprojectedControlledProbes=45, derivedPlans=318, plannedCommands=864, explicitNooptCommands=864, nativeOptimizedPhaseQualified=False, nativeDefaultOptimizationQualified=False, productExecuted=False, publicObservationsChecked=0)
    if args.actual is None:
        print(json.dumps(result)); return
    actual_path = args.actual.resolve(); work = actual_path.parent
    rows = load(actual_path)
    req(len(rows) == 864, 'all 864 actual commands required')
    keyed = {(r['count'],r['id']):r for r in rows}
    req(len(keyed) == 864 and set(keyed) == {(count,p['id']) for count in [2,3] for p in plan}, 'exact public case/count set')
    list_path = work/'empty-search.tsv'; absent = work/'not-registered-and-absent.tsv'; sentinel = work/'existing-output.sentinel'
    req(list_path.read_bytes() == b'\tX\n' and not absent.exists() and sentinel.read_bytes() == b'existing output bytes remain unchanged\n', 'derived list/control/sentinel inputs')
    identities = set()
    for count in [2,3]:
        roots = [work/('sides-'+str(count))/('side-'+str(i)) for i in range(count)]
        for side, root in enumerate(roots):
            req({p.name for p in root.iterdir()} == {'row.bin','row.dir'} and (root/'row.bin').read_bytes() == bytes([side+1])*(side+1) and (root/'row.dir').is_dir() and not list((root/'row.dir').iterdir()), 'fixed files and empty directory')
        for p in plan:
            identity = p['id']; r = keyed[count,identity]
            req(r['optimizationDirective'] == '@noopt' and r['nativePhaseQualification'] == 'native-function-body-only' and r['nativeOptimizedPhaseQualified'] is False and r['nativeDefaultOptimizationQualified'] is False, 'actual phase qualification '+identity)
            expression = p['expression'].replace('__EMPTY_LIST__',quote(list_path)).replace('__UNREGISTERED_LIST__',quote(absent))
            prefix = str(count)+'-'+identity; filter_path = work/(prefix+'.flt')
            stdout_path = work/(prefix+'.stdout'); stderr_path = work/(prefix+'.stderr')
            req(r['expression'] == expression and pathlib.Path(r['filter']).resolve() == filter_path and [pathlib.Path(x).resolve() for x in r['roots']] == roots, 'exact expression/input paths '+prefix)
            validate_filter_bytes(filter_path.read_bytes(), p['rule'], expression, p['optimizationDirective'])
            arguments = ['--folder',str(roots[0]),str(roots[-1]),'--filter',str(filter_path),'--show-filtered'] + (['--middle',str(roots[1])] if count == 3 else [])
            req(r['arguments'] == arguments, 'actual CLI arguments '+prefix)
            protected = {str(root/'row.bin') for root in roots}|{str(filter_path),str(list_path),str(sentinel)}
            before = r['protectedBefore']; after = r['protectedAfter']
            req(set(before) == set(after) == protected and before == after and all(sha(pathlib.Path(path).read_bytes()) == digest for path,digest in after.items()), 'all input/filter/list/sentinel SHA '+prefix)
            req(pathlib.Path(r['stdout']).resolve() == stdout_path and pathlib.Path(r['stderr']).resolve() == stderr_path, 'retained stream paths '+prefix)
            out = stdout_path.read_bytes(); err = stderr_path.read_bytes()
            req(r['stdoutSha256'] == sha(out) and r['stderrSha256'] == sha(err), 'actual stream hashes '+prefix)
            property_rejected = p['windowsPropertyPreparation'] and sys.platform != 'win32'
            rejected = p['rejected'] or property_rejected
            req(r['windows'] == (sys.platform == 'win32'), 'actual host OS qualification '+prefix)
            expected_exit = 2 if rejected else 1 if p['selected'] and p['rule'] in ['e','fe'] else 0
            req(r['exitCode'] == expected_exit, 'exact exit '+prefix)
            if rejected:
                req(out == b'' and err and (b'Windows' in err if property_rejected else p['diagnostic'] is None or p['diagnostic'].encode() in err), 'strict rejection streams '+prefix)
            else:
                logs = err.decode('utf-8').splitlines()
                req(len(logs) == p['expectedLogCount'] and all(line.startswith('info: provider-null-outer ') for line in logs) and b'provider-null-inner' not in err, 'inner/outer runtime logs '+prefix)
                model = json.loads(out); entries = model['entries']
                req(model['allEntryCount'] == 2 and len(entries) == 2 and {e['path'] for e in entries} == {'row.bin','row.dir'} and model.get('threeWay',False) == (count == 3), 'full directory model '+prefix)
                for entry in entries:
                    is_file = entry['path'] == 'row.bin'
                    filtered = not p['selected'] or (p['rule'] == 'de' if is_file else p['rule'] == 'fe')
                    req(entry['filtered'] == filtered and entry['status'] == ('Uncompared' if filtered else 'Modified' if is_file else 'Equal'), 'row filter/status '+prefix)
                    names = ['left','right'] if count == 2 else ['left','middle','right']
                    for side,name in enumerate(names):
                        state = entry[name]
                        req(pathlib.Path(state['path']).resolve() == roots[side]/entry['path'] and state['kind'] == ('File' if is_file else 'Directory') and state['filtered'] == filtered and state.get('error') is None, 'side path/kind/filter '+prefix)
                        if is_file: req(state['size'] == side+1, 'side fixed input size '+prefix)
            validate_launch(r, expected_exit, args.platform)
            child_identity = (r['pid'],r['creationUtc'])
            req(child_identity not in identities,'distinct child '+prefix); identities.add(child_identity)
            req(all(r[k] is False for k in ['actualTypedValueObserved','nativeFullAstEquivalent','nativeExceptionEquivalent','propertyReadInstrumented','listReadInstrumented']), 'qualification flags '+prefix)
    result.update(productExecuted=True,publicObservationsChecked=864,actualTypedValueObserved=False,nativeFullAstEquivalent=False,nativeExceptionEquivalent=False,lexerAndCancellationInjectionExecuted=False)
    print(json.dumps(result))
if __name__ == '__main__':
    main()
