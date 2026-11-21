"""Independent byte reader; never computes replacement or serializes oracle values."""
import argparse
import collections
import hashlib
import json
import pathlib
import subprocess
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent
COMMIT = '6780924ba35f7f55af0f6f14dd302561175830a6'
FIXED = {
    'cases.json': 'a0fe11284dc6b012f8eead24639fde753eaf416e1fc7e878465ab0c70c32a42e',
    'golden.ndjson': 'd7105fe61ae209c2d53833731683933ebaf4d9efba89359947999f96e05e9c65',
    'serialized-golden.ndjson': 'f5558c313d3ff5ad9d02a1207384eac497a65c04973607f4d33ea43ebb6c8182',
    'reference-source.zip': '18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7',
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def load(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'duplicate JSON property: ' + key)
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=unique)


def rows(data):
    require(data.endswith(b'\n'), 'NDJSON must end in LF')
    return [load(line) for line in data.splitlines()]


def verify(root, repo):
    manifest = load((root / 'fixed-manifest.json').read_bytes())
    expected = manifest['files']
    files = sorted(p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file())
    require(files == sorted([e['path'] for e in expected] + ['fixed-manifest.json']), 'manifest file inventory')
    for entry in expected:
        data = (root / entry['path']).read_bytes()
        require(len(data) == entry['size'] and sha(data) == entry['sha256'], 'fixed file: ' + entry['path'])
    for name, digest in FIXED.items():
        require(sha((root / name).read_bytes()) == digest, 'original capture SHA: ' + name)
    provenance = load((root / 'provenance.json').read_bytes())
    require(provenance['commit'] == COMMIT, 'source commit')
    archive_manifest = load((root / 'source-archive-manifest.json').read_bytes())
    with zipfile.ZipFile(root / 'reference-source.zip') as archive:
        require(archive.testzip() is None, 'source ZIP CRC')
        require(sorted(archive.namelist()) == sorted(e['path'] for e in archive_manifest), 'source ZIP inventory')
        for entry in archive_manifest:
            data = archive.read(entry['path'])
            require(len(data) == entry['size'] and sha(data) == entry['sha256'], 'ZIP entry: ' + entry['path'])
        sources = {}
        for source in provenance['sources']:
            data = archive.read('original/' + source['path'])
            blob = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
            require(blob == source['blob'] and len(data) == source['size'] and sha(data) == source['sha256'], 'source blob: ' + source['path'])
            actual_blob = subprocess.check_output(['git', 'rev-parse', COMMIT + ':' + source['path']], cwd=repo).decode().strip()
            actual = subprocess.check_output(['git', 'cat-file', 'blob', actual_blob], cwd=repo)
            require(actual_blob == blob and actual == data, 'fixed Git source: ' + source['path'])
            sources[source['path']] = data
        for extraction in provenance['extractions']:
            data = sources[extraction['path']]
            start, end = extraction['startByte'], extraction['endByte']
            body = data[start:end]
            require(sha(body) == extraction['sha256'] and archive.read(extraction['file']) == body + b'\n', 'extraction bytes: ' + extraction['file'])
            require(data[:start].count(b'\n') + 1 == extraction['startLine'] and data[:end].count(b'\n') + 1 == extraction['endLine'], 'extraction lines')
        require(len(sources) == 53 and len(provenance['extractions']) == 31, 'source/extraction counts')
    cases = load((root / 'cases.json').read_bytes())
    golden = rows((root / 'golden.ndjson').read_bytes())
    serialized = rows((root / 'serialized-golden.ndjson').read_bytes())
    plan = load((root / 'public-case-plan.json').read_bytes())
    ids = [c['id'] for c in cases]
    require(len(ids) == len(set(ids)) == 100, '100 unique case IDs')
    require(ids == [c['id'] for c in golden] == [c['id'] for c in serialized] == [c['id'] for c in plan['cases']], 'original ID order')
    applied = 0
    for case, old, new, public in zip(cases, golden, serialized, plan['cases']):
        require({k: v for k, v in new.items() if k not in ('serializedUtf8Hex', 'serializationApplied')} == old, 'original fields: ' + case['id'])
        require(public['originalArgsAST'] == case['args'] and public['originalCaseSensitive'] == case['caseSensitive'] and public['originalNullArgs'] == case.get('nullArgs', False) and public['mode'] == case['mode'], 'AST bridge: ' + case['id'])
        if old['errorUtf8Hex']:
            require(new['serializationApplied'] is False and new['serializedUtf8Hex'] is None, 'error serialization')
        else:
            require(new['serializationApplied'] is True and isinstance(new['serializedUtf8Hex'], str), 'native serialization')
            bytes.fromhex(new['serializedUtf8Hex'])
            applied += 1
    require(applied == 93, '93 native ToStringValue captures')
    for prefix, digest in [('native', FIXED['golden.ndjson']), ('serialized', FIXED['serialized-golden.ndjson'])]:
        pids = []
        for suffix in ['capture-receipt.json', 'recompile-receipt.json']:
            receipt = load((root / 'capture-evidence' / (prefix + '-' + suffix)).read_bytes())
            require(receipt['exitCode'] == 0 and receipt['timeout'] is False and receipt['heldProcessHandleSignaled'] is True and receipt['stdoutEof'] is True and receipt['stderrEof'] is True and receipt['birthFileTime'] > 0 and receipt['stdoutSha256'] == digest and receipt['caseCount'] == 100 and receipt['sourceCommit'] == COMMIT, 'native process receipt')
            pids.append(receipt['pid'])
        require(pids[0] != pids[1], 'independent process IDs')
    return plan, serialized, dict(caseCount=100, sourceGitBlobs=53, exactExtractions=31, zipEntries=len(archive_manifest), nativeSerialized=93, nativeErrors=7, fixedFiles=len(expected), fixtureGitBlobs='not staged; raw worktree only')


def actual_verify(data, plan, serialized, enriched=True):
    actual = rows(data)
    public = plan['cases']
    require([r['id'] for r in actual] == [p['id'] for p in public], 'actual missing/extra/reordered IDs')
    counts = collections.Counter()
    for a, p, s in zip(actual, public, serialized):
        case_id = p['id']
        context = p['context']
        require(a['expression'] == p['expression'] and a['classification'] == p['oracleMatchedOrBoundary'], 'actual expression/classification: ' + case_id)
        require(a['originalHex'] == context['sourceLineHex'] and a['sourceLineCount'] == context['sourceLineCount'] and a['rowIndex'] == context['rowIndex'], 'actual source context: ' + case_id)
        if enriched:
            require(json.dumps(a['context'], sort_keys=True, ensure_ascii=True) == json.dumps(context, sort_keys=True, ensure_ascii=True) and a['sourceCommit'] == COMMIT and a['sourceSha256'].upper() == plan['originalNodesSha256'].upper(), 'actual source metadata/context: ' + case_id)
        cls = p['oracleMatchedOrBoundary']
        if cls == 'oracle-match-candidate':
            require(a['status'] == 'ok' and a['errorPhase'] == '' and a['exceptionType'] == '' and s['serializationApplied'] is True and a['keyHex'] == s['serializedUtf8Hex'], 'native serialized key: ' + case_id)
            counts['matched'] += 1
        elif p['expression'] is None:
            require(a['status'] == 'unsupported' and a['errorPhase'] == 'unrepresentable' and a['exceptionType'] == 'UnrepresentableAdapterType' and a['keyHex'] == a['originalHex'], 'fixed unrepresentable: ' + case_id)
            counts['unrepresentableNotExecuted'] += 1
        else:
            phase = 'evaluate' if cls == 'generated-crlf-boundary' else 'compile'
            require(a['status'] == 'error' and a['errorPhase'] == phase and a['exceptionType'] == 'ArgumentException' and a['keyHex'] == a['originalHex'], 'public rejection phase/key: ' + case_id)
            counts['rejected'] += 1
    require(dict(counts) == dict(matched=78, rejected=10, unrepresentableNotExecuted=12), 'public qualification counts')
    return dict(counts)


def frozen_verify(root, plan, serialized):
    with zipfile.ZipFile(root / 'capture-evidence/public-serialized-proof.zip') as archive:
        manifest = load(archive.read('manifest.json'))
        require(archive.testzip() is None and sorted(archive.namelist()) == sorted([e['path'] for e in manifest] + ['manifest.json']), 'public evidence ZIP inventory/CRC')
        for entry in manifest:
            data = archive.read(entry['path'])
            require(len(data) == entry['size'] and sha(data).upper() == entry['sha256'].upper(), 'public evidence SHA')
        require(archive.read('reference/public-case-plan.json') == (root / 'public-case-plan.json').read_bytes(), 'frozen plan bytes')
        require(archive.read('reference/serialized-golden.ndjson') == (root / 'serialized-golden.ndjson').read_bytes(), 'frozen native serialized bytes')
        receipt = load(archive.read('proof/process.json'))
        data = archive.read('proof/public.stdout')
        require(receipt['exitCode'] == 0 and receipt['heldHandleSignaled'] is True and receipt['stdoutEOF'] is True and receipt['stderrEOF'] is True and receipt['timeout'] is False and sha(data).upper() == receipt['stdoutSHA'].upper(), 'frozen public real process')
        return actual_verify(data, plan, serialized, enriched=False)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=pathlib.Path, default=ROOT)
    parser.add_argument('--repo', type=pathlib.Path, default=ROOT.parents[2])
    parser.add_argument('--actual', type=pathlib.Path)
    parser.add_argument('--public-plan', type=pathlib.Path)
    parser.add_argument('--legacy-proof', action='store_true')
    parser.add_argument('--verify', action='store_true', help='verify immutable originals (also always run)')
    args = parser.parse_args()
    plan, serialized, result = verify(args.root, args.repo)
    if args.public_plan:
        require(args.public_plan.read_bytes() == (args.root / 'public-case-plan.json').read_bytes(), 'public plan must be frozen exact bytes')
    if args.actual:
        require(args.public_plan is not None, '--actual requires --public-plan')
        result['actual'] = actual_verify(args.actual.read_bytes(), plan, serialized)
    if args.legacy_proof:
        result['frozenPublicPureCoreEvidence'] = frozen_verify(args.root, plan, serialized)
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
