"""Read-only independent validation of fixed raw blobs, extractions and native stdout.

No Python regex engine is used to produce or validate regex expected results.
"""
import argparse
import collections
import hashlib
import json
import pathlib
import subprocess
import zipfile

BASE = pathlib.Path(__file__).resolve().parent
COMMIT = '6780924ba35f7f55af0f6f14dd302561175830a6'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


def require(ok, reason):
    if not ok:
        raise ValueError(reason)


def records(path):
    raw = path.read_bytes()
    require(raw.endswith(b'\n') and b'\r' not in raw.replace(b'\r\n', b'\n'), f'{path}: NDJSON framing')
    rows = [json.loads(line) for line in raw.splitlines()]
    require(len({r['id'] for r in rows}) == len(rows), f'{path}: duplicate IDs')
    return rows


def typed(value):
    require(isinstance(value, dict), 'value must be a typed object')
    kind = value['kind']
    if kind == 'none':
        require(value == {'kind': 'none'}, 'none schema')
    elif kind == 'integer':
        require(type(value['value']) is int and -(2**63) <= value['value'] < 2**63, 'integer schema')
    elif kind == 'bool':
        require(type(value['value']) is bool, 'bool schema')
    elif kind == 'string':
        require(bytes.fromhex(value['utf8Hex']).hex() == value['utf8Hex'], 'string byte encoding')
    elif kind == 'array':
        require(isinstance(value['items'], list), 'array schema')
        for item in value['items']:
            typed(item)
    else:
        raise ValueError('unknown value kind '+kind)


def verify(check_git=False, actual=None):
    manifest = load(BASE/'fixed-manifest.json')
    files = {p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file()}
    require(files == set(manifest['files']) | {'fixed-manifest.json'}, 'fixed file set differs')
    for name, entry in manifest['files'].items():
        raw = (BASE/name).read_bytes()
        require(len(raw) == entry['size'] and sha(raw) == entry['sha256'], 'fixed bytes: '+name)
    provenance = load(BASE/'provenance.json')
    require(provenance['commit'] == COMMIT == manifest['sourceCommit'], 'fixed commit')
    archive_manifest = load(BASE/'source-archive-manifest.json')
    with zipfile.ZipFile(BASE/'reference-source.zip') as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)) and set(names) == set(archive_manifest), 'archive entry set')
        for name, entry in archive_manifest.items():
            require(not name.startswith('/') and '..' not in pathlib.PurePosixPath(name).parts, 'unsafe archive name')
            raw = archive.read(name)
            require(len(raw) == entry['size'] and sha(raw) == entry['sha256'], 'archive bytes: '+name)
            require(archive.getinfo(name).CRC == entry['crc32'], 'archive CRC: '+name)
        for entry in provenance['sources']:
            raw = archive.read('original/'+entry['path'])
            blob = hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()
            require(blob == entry['blob'] and sha(raw) == entry['sha256'] and len(raw) == entry['size'], 'raw blob: '+entry['path'])
            if check_git:
                actual_blob = subprocess.check_output(['git', 'rev-parse', COMMIT+':'+entry['path']], cwd=BASE).decode().strip()
                require(actual_blob == blob, 'Git object identity: '+entry['path'])
                require(subprocess.check_output(['git', 'cat-file', 'blob', blob], cwd=BASE) == raw, 'Git raw bytes: '+entry['path'])
        for entry in provenance['extractions']:
            raw = archive.read('original/'+entry['path'])
            part = raw[entry['startByte']:entry['endByte']]
            require(sha(part) == entry['sha256'] and archive.read(entry['file']) == part+b'\n', 'unmodified extraction: '+entry['file'])
            require(raw[:entry['startByte']].count(b'\n')+1 == entry['startLine'] and raw[:entry['endByte']].count(b'\n')+1 == entry['endLine'], 'extraction line numbers')
    cases = load(BASE/'cases.json')
    golden = records(BASE/'golden.ndjson')
    legacy = (BASE/'golden.ndjson').read_bytes().splitlines(keepends=True)[:325]
    require(sha(b''.join(legacy)) == 'edff508973ae736fba73d6ad44cb76228524e9d99445a6107d693551b6a44ced', 'original 325 native rows unchanged')
    require([c['id'] for c in cases] == [r['id'] for r in golden], 'golden ID order')
    require(len(cases) == manifest['caseCount'], 'fixed case count')
    for c, row in zip(cases, golden):
        require(row['mode'] == c['mode'], 'mode: '+c['id'])
        require(type(row['folded']) is bool and type(row['regexNeedle']) is bool, 'optimizer schema')
        require(row['evaluated'] == sorted(set(row['evaluated'])), 'argument trace schema')
        if row['exceptionType']:
            require(row['value'] is None and row['serializationApplied'] is False and row['serializedUtf8Hex'] is None, 'error output: '+c['id'])
            require(row['errorPhase'] in ['optimize', 'evaluate'] and bytes.fromhex(row['errorUtf8Hex']), 'error evidence: '+c['id'])
        else:
            typed(row['value'])
            require(row['serializationApplied'] is True and row['errorPhase'] == '' and row['errorUtf8Hex'] == '', 'success output: '+c['id'])
            bytes.fromhex(row['serializedUtf8Hex'])
    for name in ['capture-receipt.json', 'recompile-receipt.json']:
        receipt = load(BASE/name)
        require(receipt['exitCode'] == 0 and receipt['timeout'] is False and receipt['heldProcessHandleSignaled'] is True, 'process completion')
        require(receipt['stdoutEof'] is True and receipt['stderrEof'] is True and receipt['stdoutSha256'] == sha((BASE/'golden.ndjson').read_bytes()), 'stdout collection')
        require(receipt['stderrSha256'] == sha(b'') and receipt['caseCount'] == len(cases) and receipt['sourceCommit'] == COMMIT, 'capture provenance')
    if actual is not None:
        observed = records(actual)
        require([r['id'] for r in observed] == [r['id'] for r in golden], 'actual ID order')
        for expected, got in zip(golden, observed):
            require(got == expected, 'actual native result differs: '+expected['id'])
    result = dict(cases=len(cases), sources=len(provenance['sources']), extractions=len(provenance['extractions']),
                  archiveEntries=len(archive_manifest), functions=dict(collections.Counter(c['function'] for c in cases)),
                  modes=dict(collections.Counter(c['mode'] for c in cases)),
                  exceptions=dict(collections.Counter(r['exceptionType'] or 'none' for r in golden)),
                  goldenSha256=sha((BASE/'golden.ndjson').read_bytes()), gitChecked=check_git, actualChecked=actual is not None)
    print(json.dumps(result))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--verify', action='store_true', help='verify the complete fixed fixture (also the default)')
    parser.add_argument('--git', action='store_true', help='also check every source against the pinned local Git object')
    parser.add_argument('--actual', type=pathlib.Path, help='compare independently captured native stdout, including adapter diagnostics')
    options = parser.parse_args()
    verify(options.git, options.actual)
