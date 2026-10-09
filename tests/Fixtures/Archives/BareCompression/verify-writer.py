# SPDX-License-Identifier: CC0-1.0
# Independent external-contract reader: Python standard library only.
import argparse, tempfile, bz2, hashlib, io, json, os, pathlib, stat, subprocess, sys, tarfile, threading, time, datetime

GENERATOR_SHA = 'D68BB5DDE8469F6781741BBD1D2CAC151403AF68FDE2CC8690E7C18528499A18'
CAP = 256 * 1024 * 1024
STDERR_CAP = 1024 * 1024
EXPECTED_CASE_IDS = ({'create-' + raw + '-' + tag for raw in ('japanese', 'empty', 'tar-body') for tag in ('bzip2-lower', 'bzip2-upper', 'z-upper', 'z-lower')} | {raw + '-' + tag for raw in ('zip', 'empty-zip', 'gzip', 'from-short-bz2', 'from-short-Z', 'from-empty-bz2', 'from-empty-Z', 'from-tar-bz2', 'from-tar-Z') for tag in ('bzip2-lower', 'z-upper')})
EXPECTED_TAR_IDS = {'priority' + suffix for suffix in ('.tar.bz2', '.tbz2', '.tbz', '.tar.Z', '.taz')}

def require(condition, message):
    if not condition:
        raise ValueError(message)

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def hash_file(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest().upper()

def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate JSON property: ' + key)
        result[key] = value
    return result

def load_json(path):
    require(path.stat().st_size <= 32 * 1024 * 1024, 'JSON size cap exceeded')
    return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=unique_pairs)

def safe_path(value, root=None, must_exist=True):
    require(isinstance(value, str) and pathlib.Path(value).is_absolute(), 'absolute path required: ' + str(value))
    path = pathlib.Path(os.path.abspath(value))
    if root is not None:
        require(os.path.commonpath((str(path), str(root))) == str(root), 'path outside run: ' + str(path))
    # Root-first lstat refuses an unknown linked ancestor before touching its children.
    for item in reversed((path, *path.parents)):
        try:
            info = item.lstat()
        except FileNotFoundError:
            if item == path and must_exist:
                raise ValueError('missing path: ' + str(item))
            continue
        require(not stat.S_ISLNK(info.st_mode) and not (getattr(info, 'st_file_attributes', 0) & 0x400), 'linked path refused: ' + str(item))
    return path

def checked_hash(path, expected):
    require(isinstance(expected, str) and len(expected) == 64, 'invalid SHA value')
    actual = hash_file(path)
    require(actual == expected.upper(), 'SHA mismatch: ' + str(path))
    return actual

def read_capped(path, maximum=CAP):
    require(path.is_file() and path.stat().st_size <= maximum, 'file size/type cap exceeded: ' + str(path))
    with path.open('rb') as stream:
        data = stream.read(maximum + 1)
    require(len(data) <= maximum, 'growing file size cap exceeded')
    return data

def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()

def child(arguments, stdin, stdout_cap, deadline_seconds, evidence):
    # Each pipe is drained concurrently, bounded before retaining bytes; stdin uses its own writer.
    started = time.monotonic()
    record = {'arguments': arguments, 'LaunchUtc': utc(), 'stdoutCap': stdout_cap, 'stderrCap': STDERR_CAP, 'timeoutSeconds': deadline_seconds}
    evidence.append(record)
    proc = subprocess.Popen(arguments, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, shell=False)
    record['Pid'] = proc.pid
    buffers = [bytearray(), bytearray()]
    eof = [False, False]
    errors = []
    def drain(index, stream, bound):
        try:
            while True:
                block = stream.read(65536)
                if not block:
                    eof[index] = True
                    break
                if len(buffers[index]) + len(block) > bound:
                    errors.append('stdout cap exceeded' if index == 0 else 'stderr cap exceeded')
                    proc.kill()
                    break
                buffers[index].extend(block)
        except Exception as error:
            errors.append(type(error).__name__ + ': ' + str(error))
        finally:
            stream.close()
    def feed():
        try:
            proc.stdin.write(stdin)
            proc.stdin.flush()
        except BrokenPipeError:
            pass
        except Exception as error:
            errors.append('stdin: ' + str(error))
        finally:
            proc.stdin.close()
    threads = [threading.Thread(target=drain, args=(0, proc.stdout, stdout_cap), daemon=True), threading.Thread(target=drain, args=(1, proc.stderr, STDERR_CAP), daemon=True), threading.Thread(target=feed, daemon=True)]
    for thread in threads:
        thread.start()
    timed_out = False
    try:
        proc.wait(timeout=max(0.01, deadline_seconds - (time.monotonic() - started)))
        record['ExitObservedUtc'] = utc()
    except subprocess.TimeoutExpired:
        timed_out = True
        proc.kill()
        proc.wait(timeout=5)
        record['ExitObservedUtc'] = utc()
    for thread in threads:
        thread.join(timeout=max(0, started + deadline_seconds + 5 - time.monotonic()))
    stdout, stderr = map(bytes, buffers)
    record.update(actualExit=proc.returncode, DurationMilliseconds=round((time.monotonic() - started) * 1000), timedOut=timed_out, stdoutEof=eof[0], stderrEof=eof[1], threadsFinished=all(not thread.is_alive() for thread in threads), stdoutBytes=len(stdout), stdoutSha256=sha(stdout), stderrBytes=len(stderr), stderrSha256=sha(stderr), stdoutHex=stdout.hex(), stderrHex=stderr.hex(), errors=errors)
    require(not timed_out and not errors and all(eof) and record['threadsFinished'], 'child process incomplete: ' + json.dumps(record))
    require(proc.returncode == 0, 'child nonzero exit: ' + str(proc.returncode))
    return stdout

def bzip_decode(data, maximum):
    require(data[:3] == b'BZh', 'BZip2 magic absent')
    decoder = bz2.BZ2Decompressor()
    output = decoder.decompress(data, max_length=maximum + 1)
    require(len(output) <= maximum, 'BZip2 decoded length cap exceeded')
    while not decoder.eof and not decoder.needs_input:
        block = decoder.decompress(b'', max_length=maximum + 1 - len(output))
        output += block
        require(len(output) <= maximum, 'BZip2 decoded length cap exceeded')
    require(decoder.eof, 'BZip2 EOF/footer missing')
    require(not decoder.unused_data, 'BZip2 extra member/trailing bytes')
    # BZ2Decompressor validates member CRC and footer on successful EOF.
    return output

def decode(data, mode, maximum, z_reference, evidence):
    require(type(maximum) is int and 0 <= maximum <= CAP, 'invalid decoded length limit')
    if mode in ('python-bz2', 'python-bz2-then-tarfile'):
        return bzip_decode(data, maximum)
    require(mode in ('official-ncompress', 'official-ncompress-then-python-tarfile'), 'unsupported decoder')
    require(data[:2] == b'\x1f\x9d', 'Z magic absent')
    output = child([str(z_reference), '-d', '-c'], data, maximum + 1, 60, evidence)
    require(len(output) <= maximum, 'Z decoded length cap exceeded')
    evidence[-1]['formatLimits'] = 'Z has no checksum, declared length, or format EOF marker; qualified decoder real exit, pipe EOF, and expected full bytes are checked.'
    evidence[-1]['stdoutHex'] = output.hex()
    return output

def exact_ids(rows, expected, name):
    require(isinstance(rows, list), name + ' must be array')
    ids = [row['id'] for row in rows]
    require(len(ids) == len(set(ids)) and set(ids) == expected, name + ' exact ID set mismatch: ' + repr(ids))

def tar_check(raw, rows):
    require(isinstance(rows, list), 'tar expected rows must be array')
    expected = {}
    for row in rows:
        require(row['name'] not in expected, 'duplicate expected TAR name')
        require(row['name'] and not row['name'].startswith('/') and '\\' not in row['name'] and all(part not in ('', '.', '..') for part in row['name'].split('/')), 'unsafe expected TAR name')
        require(type(row['directory']) is bool, 'TAR directory must be bool')
        expected[row['name']] = (row['directory'], bytes.fromhex(row['expectedHex']))
    actual = {}
    body_end = 0
    with tarfile.open(fileobj=io.BytesIO(raw), mode='r:') as archive:
        for member in archive:
            if member.isdir():
                member.name = member.name.removesuffix('/')
            require(member.name and not member.name.startswith('/') and '\\' not in member.name and all(part not in ('', '.', '..') for part in member.name.split('/')), 'unsafe TAR entry name')
            require(member.name not in actual, 'duplicate TAR entry: ' + member.name)
            require(member.isdir() or member.isfile(), 'unsupported TAR type: ' + member.name)
            content = b'' if member.isdir() else archive.extractfile(member).read(CAP + 1)
            require(member.size == len(content), 'TAR entry size mismatch: ' + member.name)
            actual[member.name] = (member.isdir(), content)
            body_end = max(body_end, member.offset_data + ((member.size + 511) // 512) * 512)
    require(len(raw) % 512 == 0 and len(raw) - body_end >= 1024 and not any(raw[body_end:]), 'TAR EOF/padding/trailing bytes invalid')
    require(actual == expected, 'TAR full entry/type/size/byte set mismatch')
    require(list(actual) == list(expected), 'TAR ordinal entry order mismatch')
    return [{'name': name, 'directory': directory, 'bytes': len(data), 'sha256': sha(data)} for name, (directory, data) in actual.items()]

def directory_entries(path):
    require(path.is_dir(), 'directory target is not a directory')
    items = []
    for current, directories, files in os.walk(path, followlinks=False):
        for name in directories + files:
            entry = safe_path(str(pathlib.Path(current) / name), path)
            items.append(entry)
    rows = [{'relativePath': str(entry.relative_to(path)), 'directory': entry.is_dir(), 'sha256': None if entry.is_dir() else hash_file(entry)} for entry in sorted(items, key=lambda item: str(item))]
    require(all(all(ord(character) < 128 for character in row['relativePath']) for row in rows), 'non-ASCII directory JSON needs explicit System.Text.Json encoding contract')
    return rows

def entries_fingerprint(rows):
    return sha(json.dumps(rows, separators=(',', ':'), ensure_ascii=True).encode('utf-8'))

def fingerprint(path, directory):
    return entries_fingerprint(directory_entries(path)) if directory else hash_file(path)

def lexical_path(value, root):
    require(isinstance(value, str) and pathlib.Path(value).is_absolute(), 'absolute negative-link path required')
    path = pathlib.Path(os.path.abspath(value))
    require(os.path.commonpath((str(path), str(root))) == str(root), 'negative link outside run')
    return path

def path_key(path):
    return os.path.normcase(os.path.abspath(path))

def lexical_link_target(raw, link_parent):
    require(isinstance(raw, str) and raw, 'missing link metadata target')
    if os.name == 'nt':
        # readlink may expose a Windows device prefix while .NET LinkTarget does not.
        if raw.startswith('\\\\?\\UNC\\'):
            raw = '\\\\' + raw[8:]
        elif raw.startswith('\\\\?\\'):
            raw = raw[4:]
        elif raw.startswith('\\??\\'):
            raw = raw[4:]
    candidate = pathlib.Path(raw)
    return path_key(candidate if candidate.is_absolute() else link_parent / candidate)

def mapped_path(value, run, mapping, case_id=None, field=None, must_exist=True):
    path = lexical_path(value, run)
    record = mapping.get(path_key(path))
    if record is None:
        return safe_path(value, run, must_exist)
    if case_id is not None:
        require(case_id == record['caseId'], 'negative link exact case mapping mismatch')
        if record['role'] == 'input':
            require(field == 'input' and case_id in ('input-link-bzip2-lower', 'input-link-z-upper'), 'negative input link role/case mismatch')
        elif record['role'] == 'output':
            require(field == 'destination' and case_id in ('output-link-bzip2-lower', 'output-link-z-upper'), 'negative output link role/case mismatch')
        else:
            require(field == 'destination' and case_id == record['caseId'], 'negative parent child role/case mismatch')
    return safe_path(str(record['target']), run, must_exist)

def validate_links(manifest, run, result):
    evidence = manifest.get('linkEvidence')
    require(isinstance(evidence, list), 'negative-link evidence array absent')
    unqualified = manifest.get('unqualified')
    require(isinstance(unqualified, list) and all(isinstance(x, str) for x in unqualified), 'manifest unqualified list absent')
    partial = [x for x in unqualified if x.startswith('link creation ')]
    result['unqualified'].extend(partial)
    mapping = {}
    roles = []
    def add(path, record):
        key = path_key(path)
        require(key not in mapping, 'duplicate negative-link map')
        mapping[key] = record
    for row in evidence:
        require(row['role'] in ('input', 'output', 'parent') and type(row['commandsExecuted']) is bool, 'invalid negative link role/status')
        link = lexical_path(row['linkPath'], run)
        safe_path(str(link.parent), run)
        info = link.lstat()
        require(stat.S_ISLNK(info.st_mode) or (getattr(info, 'st_file_attributes', 0) & 0x400), 'negative link replaced by normal path')
        target = safe_path(row['expectedTargetPath'], run)
        target_info = target.lstat()
        require(row['targetKind'] in ('file', 'directory') and (stat.S_ISDIR(target_info.st_mode) if row['targetKind'] == 'directory' else stat.S_ISREG(target_info.st_mode)), 'negative link target type mismatch')
        actual_target = os.readlink(link)
        for raw in (actual_target, row['linkTargetBefore'], row['linkTargetAfter']):
            require(lexical_link_target(raw, link.parent) == path_key(target), 'negative link metadata target changed')
        require(row['commandsExecuted'] or partial, 'partial negative link has no unqualified record')
        proof = {'role': row['role'], 'linkPath': str(link), 'targetPath': str(target), 'actualReadlink': actual_target, 'metadataAndTargetChecksPassed': False, 'commandsExecuted': row['commandsExecuted'], 'childDestinations': []}
        result['linkEvidence'].append(proof)
        roles.append(row['role'])
        if row['targetKind'] == 'file':
            require(row['role'] in ('input', 'output') and not row['childDestinations'], 'invalid file link role/children')
            checked_hash(target, row['targetFileSha256Before'])
            checked_hash(target, row['targetFileSha256After'])
            proof['targetSha256'] = hash_file(target)
            field = 'input' if row['role'] == 'input' else 'destination'
            allowed = {row['role'] + '-link-' + tag for tag in ('bzip2-lower', 'z-upper')}
            matches = [rejection['id'] for rejection in manifest['rejections'] if rejection.get(field) == str(link) and rejection['id'] in allowed]
            require(len(matches) == 1 if row['commandsExecuted'] else len(matches) <= 1, 'negative file link exact rejection binding missing/duplicate')
            add(link, {'role': row['role'], 'target': target, 'caseId': matches[0] if matches else None})
        else:
            require(row['role'] == 'parent', 'directory negative link must be parent')
            entries = directory_entries(target)
            require(entries == row['targetDirectoryEntriesBefore'] == row['targetDirectoryEntriesAfter'], 'negative directory full entries changed')
            digest = entries_fingerprint(entries)
            require(digest == row['targetDirectoryFingerprintBefore'].upper() == row['targetDirectoryFingerprintAfter'].upper(), 'negative directory fingerprint changed')
            proof.update(targetDirectoryFingerprint=digest, targetDirectoryEntries=entries)
            children = row['childDestinations']
            require(isinstance(children, list) and len(children) == 2, 'parent negative link needs existing and absent children')
            ids = {child['caseId'] for child in children}
            require(ids in ({'parent-link-bzip2-lower', 'parent-link-absent-bzip2-lower'}, {'parent-link-z-upper', 'parent-link-absent-z-upper'}), 'parent child exact case IDs mismatch')
            for child in children:
                link_child = lexical_path(child['linkChildPath'], run)
                canonical = safe_path(child['canonicalChildPath'], run, must_exist=False)
                require(path_key(link_child.parent) == path_key(link) and path_key(canonical.parent) == path_key(target) and link_child.name == canonical.name, 'negative parent child map mismatch')
                exists = canonical.exists()
                require(type(child['existedBefore']) is bool and type(child['existsAfter']) is bool and exists == child['existedBefore'] == child['existsAfter'], 'parent child existence changed')
                absent = child['caseId'].startswith('parent-link-absent-')
                require(exists != absent, 'parent existing/absent case mislabeled')
                if exists:
                    require(canonical.is_file(), 'parent sentinel must be file')
                    checked_hash(canonical, child['sha256Before'])
                    checked_hash(canonical, child['sha256After'])
                else:
                    require(child['sha256Before'] is None and child['sha256After'] is None, 'absent child has digest')
                add(link_child, {'role': 'parent', 'target': canonical, 'caseId': child['caseId']})
                proof['childDestinations'].append({'caseId': child['caseId'], 'canonicalChildPath': str(canonical), 'exists': exists, 'sha256': hash_file(canonical) if exists else None})
        proof['metadataAndTargetChecksPassed'] = True
    require(not partial and all(row['commandsExecuted'] for row in evidence), 'negative links partially created: rejection commands unqualified')
    require(sorted(roles) == sorted(['input', 'output', 'parent'] * 2), 'negative link role coverage incomplete')
    ids = [row['id'] for row in manifest['rejections']]
    require(len(ids) == len(set(ids)), 'duplicate rejection IDs')
    for tag in ('bzip2-lower', 'z-upper'):
        for prefix in ('input-link-', 'output-link-', 'parent-link-', 'parent-link-absent-'):
            require(prefix + tag in ids, 'negative link rejection command missing: ' + prefix + tag)
    return mapping

def verify(args, result):
    run = safe_path(args.run_root)
    require(run.is_dir(), 'run root must be directory')
    manifest_path = safe_path(args.manifest, run)
    result['manifestSha256'] = hash_file(manifest_path)
    manifest = load_json(manifest_path)
    require(manifest.get('schemaVersion') == 1, 'unsupported manifest schema')
    exact_ids(manifest['cases'], EXPECTED_CASE_IDS, 'cases')
    exact_ids(manifest['tarCases'], EXPECTED_TAR_IDS, 'tarCases')
    mapping = validate_links(manifest, run, result)
    # Validate every immutable declared input before any archive decoding.
    for row in manifest['cases']:
        for key, sha_key in (('archive', 'archiveSha256'), ('expectedPath', 'expectedSha256'), ('input', 'inputSha256')):
            checked_hash(safe_path(row[key], run), row[sha_key])
    for row in manifest['tarCases']:
        checked_hash(safe_path(row['archive'], run), row['archiveSha256'])
    require(isinstance(manifest['originals'], dict) and manifest['originals'], 'originals absent')
    for value, expected in manifest['originals'].items():
        checked_hash(mapped_path(value, run, mapping), expected)
    generator = safe_path(args.generator)
    checked_hash(generator, GENERATOR_SHA)
    source = safe_path(args.reference_source)
    z_reference = safe_path(args.z_reference)
    qualification = child([sys.executable, str(generator), '--validate-reference-only', '--z-reference', str(z_reference), '--reference-source', str(source)], b'', STDERR_CAP, 60, result['processEvidence'])
    result['processEvidence'][-1]['stdoutHex'] = qualification.hex()
    qualified = json.loads(qualification.decode('utf-8-sig'), object_pairs_hook=unique_pairs)
    checked_hash(z_reference, qualified['decoderSha256'])
    checked_hash(safe_path(qualified['buildProof']), qualified['buildProofSha256'])
    result['referenceQualification'] = qualified
    prior = manifest.get('referenceQualification')
    require(isinstance(prior, dict) and type(prior.get('processId')) is int and prior['processId'] > 0, 'original reference process PID missing')
    require(type(prior.get('exitCode')) is int and prior['exitCode'] == 0 and prior.get('stdoutEof') is True and prior.get('stderrEof') is True, 'original reference process exit/EOF not complete')
    require(isinstance(prior.get('startedUtc'), str) and datetime.datetime.fromisoformat(prior['startedUtc'].replace('Z', '+00:00')).tzinfo is not None, 'original reference launch time missing')
    require(isinstance(prior.get('stdout'), str) and isinstance(prior.get('stderr'), str), 'original reference stdout/stderr missing')
    original_reference = json.loads(prior['stdout'], object_pairs_hook=unique_pairs)
    require(original_reference == prior.get('reference') == qualified, 'original reference JSON does not match independently qualified decoder/source/build-proof')
    # The decoder is OS-specific; qualification is bound to its actual bytes via original-source build proof.
    for row in manifest['cases']:
        paths = {key: safe_path(row[key], run) for key in ('archive', 'expectedPath', 'input', 'export', 'extracted')}
        for key, sha_key in (('archive', 'archiveSha256'), ('expectedPath', 'expectedSha256'), ('input', 'inputSha256')):
            checked_hash(paths[key], row[sha_key])
        require(type(row['expectedBytes']) is int and 0 <= row['expectedBytes'] <= CAP, 'bad expected length')
        expected = read_capped(paths['expectedPath'], row['expectedBytes'])
        require(len(expected) == row['expectedBytes'], 'expected raw length mismatch')
        compressed = read_capped(paths['archive'])
        require(sha(compressed) == row['archiveSha256'].upper(), 'archive changed after preflight')
        raw = decode(compressed, row['decoder'], row['expectedBytes'], z_reference, result['processEvidence'])
        require(raw == expected and sha(raw) == row['expectedSha256'].upper(), 'decoded full bytes mismatch: ' + row['id'])
        for key in ('export', 'extracted'):
            require(read_capped(paths[key], row['expectedBytes']) == expected, key + ' full bytes mismatch: ' + row['id'])
        result['verifiedCaseIds'].append(row['id'])
    for row in manifest['tarCases']:
        archive = safe_path(row['archive'], run)
        checked_hash(archive, row['archiveSha256'])
        compressed = read_capped(archive)
        require(sha(compressed) == row['archiveSha256'].upper(), 'TAR archive changed after preflight')
        raw = decode(compressed, row['decoder'], CAP, z_reference, result['processEvidence'])
        result['tarEvidence'].append({'id': row['id'], 'entries': tar_check(raw, row['rows'])})
        result['verifiedTarCaseIds'].append(row['id'])
    require(isinstance(manifest.get('observations'), list) and manifest['observations'], 'process observations absent')
    observation_ids = set()
    for observation in manifest['observations']:
        require(observation['id'] not in observation_ids, 'duplicate process observation id')
        observation_ids.add(observation['id'])
        require(type(observation['actualExit']) is int and observation['actualExit'] == observation['expectedExit'], 'product observation exit mismatch')
        require(isinstance(observation['arguments'], list) and all(isinstance(x, str) for x in observation['arguments']), 'product arguments missing')
        require(type(observation['Pid']) is int and observation['Pid'] > 0, 'product process PID missing')
        process_created = datetime.datetime.fromisoformat(observation['CreationUtc'].replace('Z', '+00:00'))
        launched = datetime.datetime.fromisoformat(observation['LaunchUtc'].replace('Z', '+00:00'))
        exited = datetime.datetime.fromisoformat(observation['ExitObservedUtc'].replace('Z', '+00:00'))
        require(process_created.tzinfo is not None and launched.tzinfo is not None and exited.tzinfo is not None, 'product process timestamps lack UTC offsets')
        require(launched <= process_created <= exited, 'product process timestamps are inconsistent')
        launch_evidence = observation.get('LaunchEvidence')
        if sys.platform == 'darwin':
            require(isinstance(launch_evidence, dict), 'macOS native launch evidence absent')
        if launch_evidence is not None:
            require(isinstance(launch_evidence, dict), 'malformed native launch evidence')
            require(launch_evidence.get('ActualPid') == observation['Pid'] and launch_evidence.get('Terminal') is True
                    and launch_evidence.get('GateReleased') is True and launch_evidence.get('WrapperReady') is True,
                    'native launch identity/termination evidence incomplete')
            require(launch_evidence.get('StdoutComplete') is True and launch_evidence.get('StderrComplete') is True
                    and launch_evidence.get('PipesReleased') is True and launch_evidence.get('ProcessDisposed') is True
                    and launch_evidence.get('CleanupIssues') == [], 'native launch stream cleanup incomplete')
    require(isinstance(manifest['originals'], dict) and manifest['originals'], 'originals absent')
    for value, expected in manifest['originals'].items():
        checked_hash(mapped_path(value, run, mapping), expected)
    require(isinstance(manifest['rejections'], list) and manifest['rejections'], 'rejections absent')
    for row in manifest['rejections']:
        source_path = mapped_path(row['input'], run, mapping, row['id'], 'input')
        actual = fingerprint(source_path, row['inputIsDirectory'])
        require(actual == row['inputFingerprintBefore'].upper() == row['inputFingerprintAfter'].upper(), 'rejected input changed: ' + row['id'])
        destination = mapped_path(row['destination'], run, mapping, row['id'], 'destination', must_exist=False)
        exists = destination.exists()
        require(type(row['existedBefore']) is bool and type(row['existsAfter']) is bool and exists == row['existedBefore'] == row['existsAfter'], 'rejected destination existence changed')
        if exists:
            checked_hash(destination, row['outputSha256Before'])
            checked_hash(destination, row['outputSha256After'])
        else:
            require(row['outputSha256Before'] is None and row['outputSha256After'] is None, 'absent destination has digest')
        expected_reason = row['expectedReasonContains']
        actual_reason = row['actualReason']
        require(isinstance(actual_reason, str) and bool(actual_reason.strip()), 'rejection reason missing')
        if expected_reason:
            require(expected_reason in actual_reason, 'expected rejection reason missing')
    final_link_result = {'linkEvidence': [], 'unqualified': []}
    validate_links(manifest, run, final_link_result)
    require(final_link_result['linkEvidence'] == result['linkEvidence'], 'negative link metadata/targets changed during validation')
    checked_hash(generator, GENERATOR_SHA)
    checked_hash(z_reference, qualified['decoderSha256'])
    require(hash_file(manifest_path) == result['manifestSha256'], 'manifest changed during validation')
    require(set(result['verifiedCaseIds']) == EXPECTED_CASE_IDS and set(result['verifiedTarCaseIds']) == EXPECTED_TAR_IDS, 'verification ID coverage incomplete')

def main():
    parser = argparse.ArgumentParser(description='Independent bare compression writer manifest reader. Product execution is never performed.')
    for option in ('manifest', 'proof', 'run-root', 'z-reference', 'reference-source', 'generator'):
        parser.add_argument('--' + option, required=True)
    args = parser.parse_args()
    # Output must be a new file inside the explicit run root; no deletion or overwriting.
    run = safe_path(args.run_root)
    temporary = safe_path(tempfile.gettempdir())
    require(os.path.commonpath((str(run), str(temporary))) == str(temporary), 'run output must remain in external Temp')
    require(all(part.lower() not in ('artifacts', '.codex') for part in run.parts), 'forbidden run output component')
    proof = safe_path(args.proof, run, must_exist=False)
    require(not proof.exists() and proof.parent.is_dir(), 'proof must be new file in existing run directory')
    result = {'allChecksPassed': False, 'manifestSha256': '', 'verifiedCaseIds': [], 'verifiedTarCaseIds': [], 'processEvidence': [], 'tarEvidence': [], 'linkEvidence': [], 'failures': [], 'readerSha256': hash_file(pathlib.Path(__file__)), 'unqualified': ['Product E2E, GUI, AOT, and callback integration are not qualified by standalone synthetic reader checks.']}
    try:
        verify(args, result)
        result['allChecksPassed'] = True
    except Exception as error:
        result['failures'].append(type(error).__name__ + ': ' + str(error))
    with proof.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write('\n')
    print(json.dumps({'allChecksPassed': result['allChecksPassed'], 'proof': str(proof), 'failures': result['failures']}, ensure_ascii=False))
    return 0 if result['allChecksPassed'] else 1

if __name__ == '__main__':
    sys.exit(main())
