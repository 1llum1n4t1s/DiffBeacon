"""固定native原本と公開Folder CLIの独立reader。原本実行とchunk投影を分離する。"""
import sys
sys.dont_write_bytecode = True
import argparse
import hashlib
import json
import pathlib
import subprocess
import zipfile
import datetime, re

BASE = pathlib.Path(__file__).resolve().parent
PROOF_SHA = '014fa78df132e1897bf93681d27afce7f86b287e262b93af7324cbf61a8370be'

def req(value, message):
    if not value:
        raise ValueError(message)

def sha(data):
    return hashlib.sha256(data).hexdigest()

def quote(value):
    return "'" + value.replace('\\', '\\\\').replace("'", "\\'").replace('\r', '\\r').replace('\n', '\\n').replace('\t', '\\t') + "'"

def decoded(hexadecimal):
    return bytes.fromhex(hexadecimal).decode('utf-8', errors='strict')

def verify_managed_source():
    provenance = json.loads((BASE / 'provenance.json').read_bytes())
    proof = provenance['managedRunnerSource']
    req(set(proof) == {'file', 'sha256', 'entries'}, 'managed source proof fields')
    req(proof['file'] == 'managed-runner-source.zip', 'fixed managed source payload name')
    raw = (BASE / proof['file']).read_bytes()
    req(sha(raw) == proof['sha256'], 'managed source whole ZIP SHA')
    with zipfile.ZipFile(BASE / proof['file']) as archive:
        names = archive.namelist()
        req(archive.testzip() is None and len(names) == len(set(names)) and set(names) == {'Program.cs', 'source-binding.json'}, 'managed source exact ZIP entries/CRC')
        req(set(proof['entries']) == set(names), 'managed source exact proof entry set')
        for name in names:
            entry = archive.read(name); fixed = proof['entries'][name]
            req(len(entry) == fixed['size'] and sha(entry) == fixed['sha256'], 'managed source entry bytes ' + name)
        program_sha = sha(archive.read('Program.cs'))
        binding = json.loads(archive.read('source-binding.json'))
        req(program_sha == provenance['productRunnerSourceSha256'] == binding['ProgramSha256'], 'managed Program source anchor')
        rows = [row for row in binding['sources'] if row['relative'] == 'tests/DiffBeacon.E2E/Program.cs']
        req(len(rows) == 1 and rows[0]['sha256'] == program_sha, 'unique actual compile Program source binding')
        req(binding['actualRunnerBinaryBound'] is False, 'source authentication is not binary authentication')
    return program_sha

def verify_fixture(repo):
    manifest = json.loads((BASE / 'fixed-manifest.json').read_bytes())
    actual = {p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file() and p.name != 'fixed-manifest.json'}
    req(actual == set(manifest['files']), 'fixture exact file set')
    for name, fixed in manifest['files'].items():
        raw = (BASE / name).read_bytes()
        req(len(raw) == fixed['size'] and sha(raw) == fixed['sha256'], 'fixture bytes ' + name)
    verify_managed_source()
    req(sha((BASE / 'native-casefold-proof.zip').read_bytes()) == PROOF_SHA, 'fixed native ZIP SHA')
    # repo-rootは呼出し元が原典oracleから導出する。fixture/binの祖先をrepoと推定しない。
    req((repo / 'DiffBeacon.slnx').is_file() and (repo / 'tests/Fixtures/LineTransformReplaceLists/source-manifest.json').is_file(), 'explicit real repository')
    result = subprocess.run([sys.executable, '-E', '-B', '-X', 'utf8', str(BASE / 'verify-proof.py'), '--repo-root', str(repo)],
                            capture_output=True, timeout=60, check=False)
    req(result.returncode == 0 and result.stderr == b'', 'native proof reader: ' + result.stdout.decode('utf-8', errors='replace') + result.stderr.decode('utf-8', errors='replace'))
    native_report = json.loads(result.stdout)
    req(native_report['passed'] and native_report['observedRowsPerChild'] == 1132 and native_report['independentChildren'] == 2, 'native reader final report')
    with zipfile.ZipFile(BASE / 'native-casefold-proof.zip') as archive:
        cases = {c['id']: c for c in json.loads(archive.read('cases.json'))}
        golden = {g['id']: g for g in map(json.loads, archive.read('run1-golden.ndjson').splitlines())}
    derived = json.loads((BASE / 'derived-cases.json').read_bytes())
    req(len(derived) == 11 and len({c['id'] for c in derived}) == 11, 'derived public controls')
    return cases, golden, derived, native_report

def utc_ticks(value, label):
    # .NET timestamps have 100 ns precision; do not truncate through microseconds.
    req(isinstance(value, str), 'UTC timestamp string ' + label)
    match = re.fullmatch(r'([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?(?:Z|\+00:00)', value)
    req(match is not None, 'canonical UTC timestamp ' + label)
    year, month, day, hour, minute, second = map(int, match.groups()[:6])
    date = datetime.datetime(year, month, day, hour, minute, second)
    seconds = ((date.toordinal() - 1) * 24 + hour) * 3600 + minute * 60 + second
    return seconds * 10_000_000 + int((match.group(7) or '').ljust(7, '0'))

def verify_completion(row, platform, expected_exit=1):
    req(platform == ('windows' if sys.platform == 'win32' else 'macos' if sys.platform == 'darwin' else None), 'reader actual host platform')
    req(row['platform'] == platform and type(row['pid']) is int and row['pid'] > 0, 'actual product child identity/platform')
    req(type(row['exitCode']) is int and row['exitCode'] == expected_exit and row['exitCode'] != -2, 'canonical expected exit; no timeout')
    creation, launch, exited = (utc_ticks(row[key], key) for key in ('creationUtc', 'launchUtc', 'exitObservedUtc'))
    duration = row['durationMs']
    req(type(duration) is int and duration >= 0 and launch <= exited and creation <= exited
        and creation >= launch - 20_000_000
        and abs(exited - launch - duration * 10_000) <= 20_000_000, 'child timestamps/duration order')
    evidence = row['launchEvidence']
    if platform == 'windows':
        req(evidence is None, 'Windows canonical runner has no Mac launch evidence')
    else:
        req(isinstance(evidence, dict), 'macOS launch evidence required')
        req(type(evidence.get('ActualPid')) is int and evidence['ActualPid'] == row['pid']
            and type(evidence.get('RawExitCode')) is int and evidence['RawExitCode'] == expected_exit
            and utc_ticks(evidence.get('ExitObservedUtc'), 'evidence.ExitObservedUtc') == exited
            and all(evidence.get(key) is True for key in ('Terminal', 'StdoutComplete', 'StderrComplete', 'PipesReleased', 'ProcessDisposed'))
            and type(evidence.get('CleanupIssues')) is list and evidence['CleanupIssues'] == [], 'actual product completion and pipe EOF')

def verify_public(path, cases, golden, derived, platform, repo):
    inventory = json.loads(path.read_bytes())
    work = path.parent.resolve()
    provenance = json.loads((BASE / 'provenance.json').read_bytes())
    req(inventory['runnerSourceSha256'] == provenance['productRunnerSourceSha256'] == verify_managed_source(), 'fixed managed Program source SHA; actual binary is outer-bound')
    req(inventory['nativeRowsPerChild'] == 1132 and inventory['productProjectionNotNativeInvocation'] is True
        and inventory['fullListScalarValidationLowerBound'] == 79524 and inventory['publicListChunkSize'] == inventory['publicDirectChunkSize'] == 16, 'projection qualification')
    rows = inventory['observations']
    index = {(row['count'], row['id']): row for row in rows}
    req(len(index) == len(rows) == 310, 'unique public command identities')
    plan = json.loads((BASE / 'fixed-public-plan.json').read_bytes())
    plan_index = {(row['count'], row['id']): row for row in plan}
    req(len(plan_index) == len(plan) == 310 and set(plan_index) == set(index), 'fixed public plan identities')
    expected_keys, preserved_paths = set(), set()
    direct_coverage = {2: set(), 3: set()}
    projected_list_cases = 0

    def compare(count, identity, expression, scope, native_ids, direction=None, start=0, length=0, list_path=None, list_bytes=None):
        key = (count, identity)
        expected_keys.add(key)
        req(key in index, 'missing public command ' + str(key))
        row = index[key]
        fixed = plan_index[key]
        req(fixed['nativeIds'] == native_ids, 'fixed public plan native identities')
        if scope != 'derived-shared-backend-control':
            req(fixed['start'] == start and fixed['length'] == length and fixed['phase'] == 'function-body'
                and expression.startswith(fixed['directive'] + ' '), 'fixed function-body phase/chunk')
        else:
            req(fixed['expression'] == expression and fixed['phase'] == 'derived-control', 'fixed derived control')
        req(row['expression'] == expression and row['scope'] == scope and row['nativeIds'] == native_ids, 'native/derived expression mapping ' + identity)
        req(row['direction'] == direction and row['start'] == start and row['length'] == length, 'exact scalar slice ' + identity)
        roots = [work / ('sides-' + str(count)) / ('side-' + str(i)) for i in range(count)]
        req([pathlib.Path(root).resolve() for root in row['roots']] == roots, 'side root identities')
        filter_path = work / (str(count) + '-' + identity + '.flt')
        req(pathlib.Path(row['filter']).resolve() == filter_path and filter_path.read_bytes() == ('def: exclude\nfe: ' + expression + '\n').encode(), 'filter exact bytes')
        arguments = ['--folder', str(roots[0]), str(roots[-1]), '--filter', str(filter_path), '--show-filtered']
        if count == 3:
            arguments += ['--middle', str(roots[1])]
        req(row['arguments'] == arguments, 'public CLI route ' + identity)
        protected = {str(root / 'row.bin') for root in roots} | {str(filter_path)}
        if list_path is None:
            req(row['listPath'] is None, 'no hidden list input')
        else:
            req(pathlib.Path(row['listPath']).resolve() == list_path and list_path.read_bytes() == list_bytes, 'literal list bytes ' + identity)
            protected.add(str(list_path))
        req(set(row['protectedBefore']) == set(row['protectedAfter']) == protected, 'exact protected file set ' + identity)
        for name in protected:
            expected_hash = sha(pathlib.Path(name).read_bytes())
            req(row['protectedBefore'][name] == row['protectedAfter'][name] == expected_hash, 'input/filter/list SHA ' + name)
        preserved_paths.update(protected)
        for side, root in enumerate(roots):
            req((root / 'row.bin').read_bytes() == bytes([side + 1]) * (side + 1), 'independent side bytes')
        stdout_path = work / (str(count) + '-' + identity + '.stdout')
        stderr_path = work / (str(count) + '-' + identity + '.stderr')
        req(pathlib.Path(row['stdout']).resolve() == stdout_path and pathlib.Path(row['stderr']).resolve() == stderr_path, 'stream path identities')
        stdout, stderr = stdout_path.read_bytes(), stderr_path.read_bytes()
        req(row['exitCode'] == 1 and stderr == b'' and row['stdoutSha256'] == sha(stdout) and row['stderrSha256'] == sha(stderr), 'exit/stdout/stderr bytes ' + identity)
        model = json.loads(stdout)
        req(model['allEntryCount'] == 1 and len(model['entries']) == 1, 'single folder row ' + identity)
        entry = model['entries'][0]
        req(entry['path'] == 'row.bin' and entry['kind'] == 'File' and entry['status'] == entry['differenceKind'] == 'Modified'
            and entry['filtered'] is False and entry.get('error') is None and entry['children'] == [], 'typed inclusion row ' + identity)
        names = ['left', 'right'] if count == 2 else ['left', 'middle', 'right']
        req({name for name in ['left', 'middle', 'right'] if entry.get(name) is not None} == set(names), 'exact present sides ' + identity)
        req(model.get('threeWay', False) == (count == 3), 'two/three way model ' + identity)
        for side, name in enumerate(names):
            actual = entry[name]
            req(pathlib.Path(actual['path']).resolve() == roots[side] / 'row.bin' and actual['kind'] == 'File'
                and actual['size'] == side + 1 and actual['filtered'] is False and actual.get('error') is None, 'side model ' + identity + ' ' + name)
        verify_completion(row, platform)

    for count in [2, 3]:
        for direction in ['low-needle', 'high-needle']:
            for cs in [False, True]:
                suffix, directive = ('cs', '@noopt @cs ') if cs else ('ci', '@noopt @ci ')
                list_id = 'replacewithlist-' + direction + '-' + suffix
                case, native = cases[list_id], golden[list_id]
                lines = decoded(case['fileUtf8Hex']).splitlines()
                subject, expected = decoded(case['sourceUtf8Hex']), decoded(native['serializedUtf8Hex'])
                req(len(lines) == len(subject) == len(expected) == 282 and all(line[1:] == '\tX' for line in lines), 'native list scalar partition')
                needles = [line[0] for line in lines]
                req(len(set(needles)) == len(set(subject)) == 282 and not (set(needles) & set(subject)), 'no strict opposite-direction fallback')
                native_ids = ['replace-' + suffix + '-u' + format(ord(needle), 'x') for needle in needles]
                calls, results = [], []
                for native_id, needle, source in zip(native_ids, needles, subject, strict=True):
                    direct, actual = cases[native_id], golden[native_id]
                    req(decoded(direct['sourceUtf8Hex']) == source and decoded(direct['needleUtf8Hex']) == needle
                        and direct['caseSensitive'] == cs and actual['serializedUtf8Hex'] == (source.encode().hex() if cs else '58'), 'direct native identity')
                    calls.append('Replace(' + quote(source) + ',' + quote(needle) + ",'X')")
                    results.append(quote(decoded(actual['serializedUtf8Hex'])))
                    req(native_id not in direct_coverage[count], 'duplicate direct native direction')
                    direct_coverage[count].add(native_id)
                reassembled_ids = []
                for start in range(0, 282, 16):
                    size = min(16, 282 - start)
                    chunk_ids = native_ids[start:start + size]
                    compare(count, 'direct-' + direction + '-' + suffix + '-' + format(start, '03'), directive + 'AllEqual(Array(Array(' + ','.join(calls[start:start + size]) + '),Array(' + ','.join(results[start:start + size]) + ')))',
                            'native-direct-array-projection', chunk_ids, direction, start, size)
                    reassembled_ids.extend(chunk_ids)
                req(reassembled_ids == native_ids, 'direct native exact ordered reassembly')
                reassembled_source, reassembled_expected, reassembled_rules = [], [], []
                for start in range(0, 282, 16):
                    size = min(16, 282 - start)
                    list_path = work / (direction + '-' + format(start, '03') + '.tsv')
                    rule_bytes = ('\n'.join(lines[start:start + size]) + '\n').encode()
                    part_subject, part_expected = subject[start:start + size], expected[start:start + size]
                    expression = directive + 'AllEqual(Array(ReplaceWithList(' + quote(part_subject) + ',' + quote(str(list_path)) + '),' + quote(part_expected) + '))'
                    compare(count, 'list-' + direction + '-' + suffix + '-' + format(start, '03'), expression,
                            'native-list-scalar-slice-projection', [list_id], direction, start, size, list_path, rule_bytes)
                    reassembled_source.append(part_subject)
                    reassembled_expected.append(part_expected)
                    reassembled_rules.append(rule_bytes)
                req(''.join(reassembled_source).encode() == bytes.fromhex(case['sourceUtf8Hex'])
                    and ''.join(reassembled_expected).encode() == bytes.fromhex(native['serializedUtf8Hex'])
                    and b''.join(reassembled_rules) == bytes.fromhex(case['fileUtf8Hex']), 'native source/result/rules exact ordered reassembly')
                projected_list_cases += 1
        for control in derived:
            compare(count, control['id'], control['expression'], 'derived-shared-backend-control', [])
    req(expected_keys == set(index) and all(len(ids) == 1128 for ids in direct_coverage.values()), 'exact native direction/CS/side coverage')
    req(set(inventory['preserved']) == preserved_paths, 'final preserved exact set')
    for name, digest in inventory['preserved'].items():
        req(sha(pathlib.Path(name).read_bytes()) == digest, 'final input SHA ' + name)
    return dict(publicCommands=len(rows), directArrayCommands=144, nativeDirectRowsProjectedPerSideCount=1128,
                nativeListRowsProjectedPerSideCount=4, listChunkCommands=144, derivedCommands=22,
                publicSideCounts=[2, 3], listScalarPartitionsReassembled=projected_list_cases,
                inputFilesPreserved=len(preserved_paths), productExecuted=True)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=pathlib.Path, required=True)
    parser.add_argument('--platform', choices=['windows', 'macos'], required=True)
    parser.add_argument('--public-observations', type=pathlib.Path)
    args = parser.parse_args()
    cases, golden, derived, native = verify_fixture(args.repo_root.resolve())
    public = verify_public(args.public_observations.resolve(), cases, golden, derived, args.platform, args.repo_root.resolve()) if args.public_observations else dict(productExecuted=False)
    print(json.dumps(dict(passed=True, nativeProof=native, public=public, excluded=['cancellation', 'budget injection', 'escaped/class/backreference'], nativeRowsAreNotPublicCommandCount=True)))

if __name__ == '__main__':
    main()
