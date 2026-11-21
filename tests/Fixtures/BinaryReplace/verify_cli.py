"""CLI copy/package outputs against fixed Frhed golden and manual archive bytes."""
import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import stat
import zipfile


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate JSON key: ' + key)
        result[key] = value
    return result


def document(data):
    return json.loads(data.decode('utf-8-sig'), object_pairs_hook=unique_object)


def physical(path, root, fixed_input=None):
    path = Path(path).absolute()
    for part in (path, *path.parents):
        require(not part.is_symlink(), 'OS path contains link: ' + str(part))
        if part.exists():
            require(not getattr(part.stat(), 'st_file_attributes', 0) & 0x400, 'OS reparse point')
    path = path.resolve(strict=True)
    require((path.is_relative_to(root) or path == fixed_input) and path.is_file(), 'physical file outside work: ' + str(path))
    return path


def virtual(name):
    require(isinstance(name, str) and name and '\\' not in name and ':' not in name and '\0' not in name,
            'invalid ZIP reference')
    parts = name.split('/')
    require(all(part and part not in ('.', '..') for part in parts), 'ZIP traversal or empty component')
    require(not PurePosixPath(name).is_absolute(), 'absolute ZIP name')
    return name


def zip_bytes(data):
    result = {}
    folded = set()
    with zipfile.ZipFile(io.BytesIO(data)) as container:
        for entry in container.infolist():
            name = virtual(entry.filename)
            require(name not in result and name.casefold() not in folded, 'duplicate ZIP entry')
            require(not entry.is_dir() and not stat.S_ISLNK(entry.external_attr >> 16), 'ZIP directory/link')
            require(not entry.flag_bits & 1, 'encrypted ZIP entry')
            result[name] = container.read(entry)  # 全entryを読むため全CRCも独立検査される。
            folded.add(name.casefold())
        require(container.testzip() is None, 'ZIP CRC')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work', required=True, type=Path)
    parser.add_argument('--receipt', required=True, type=Path)
    args = parser.parse_args()
    work = args.work.resolve(strict=True)
    require(work.is_dir() and not args.receipt.exists(), 'work directory / fresh receipt')
    require(args.receipt.absolute().parent.resolve(strict=True) == work, 'receipt directly below work')
    fixed = Path(__file__).resolve().parent
    manifest = document((fixed / 'manifest.json').read_bytes())
    fixed_cases = {case['caseId']: case for case in manifest['cases']}
    require(len(fixed_cases) == 51, 'fixed 51 unique cases')
    case = fixed_cases['forward-grow']
    require(case['inputFile'] == 'forward-grow.input.bin', 'exact fixed source name')
    fixed_input = (fixed / case['inputFile']).resolve(strict=True)
    input_bytes = physical(fixed_input, work, fixed_input).read_bytes()
    expected_bytes = (fixed / case['expectedFile']).read_bytes()
    require(sha(input_bytes).lower() == case['inputSha256'] and len(input_bytes) == case['inputLength'], 'fixed input SHA/length')
    require(sha(expected_bytes).lower() == case['outputSha256'] and len(expected_bytes) == case['outputLength'], 'fixed golden SHA/length')
    original, edited = b'xABABABz', b'xXYZABABz'

    proof_candidates = list((work / 'gui').rglob('proof.json'))
    require(len(proof_candidates) == 1, 'exactly one GUI proof')
    proof_path = physical(proof_candidates[0], work)
    proof = document(proof_path.read_bytes())
    projects = proof['cliProjects']
    require(len(projects) == 2 and {row['id'] for row in projects} == {'ordinary', 'archive'}, 'exact GUI project set')
    projects = {row['id']: row for row in projects}
    gui_receipt = document(physical(work / 'reader-receipt.json', work).read_bytes())
    require(gui_receipt['success'] is True and gui_receipt['referenceCases'] == 51
            and gui_receipt['fullBytes'] is True and gui_receipt['zipCrc'] is True, 'GUI reader passed')
    require(physical(gui_receipt['proofFile'], work) == proof_path, 'GUI receipt proof identity')
    ordinary = projects['ordinary']
    require(ordinary['kind'] == 'ordinary' and ordinary['referenceCase'] == 'forward-grow' and ordinary['side'] == 2,
            'fixed ordinary project expectation')
    saved = [row for row in proof['saved'] if row['id'] == 'forward-grow']
    require(len(saved) == 1 and saved[0]['side'] == 2 and saved[0]['referenceCase'] == 'forward-grow', 'fixed saved ordinary side')
    saved_path = Path(saved[0]['path'])
    if not saved_path.is_absolute():
        saved_path = proof_path.parent / saved_path
    require(physical(saved_path, work).read_bytes() == expected_bytes, 'saved golden all bytes')

    def inspect_project(data, read, ident):
        value = document(data)
        require(isinstance(value, dict), ident + '/project object')
        envelope_fields = {'formatVersion', 'entries', 'activeEntryIndex'}
        if envelope_fields.intersection(value):
            # workspaceはenvelopeだけを持ち、単一DTOとの混在を拒否する。
            require(set(value) <= envelope_fields and {'formatVersion', 'entries'} <= set(value), ident + '/workspace shape')
            require(type(value['formatVersion']) is int and value['formatVersion'] == (1 if ident == 'ordinary' else 5),
                    ident + '/fixed workspace version')
            require(type(value.get('activeEntryIndex', 0)) is int and value.get('activeEntryIndex', 0) == 0,
                    ident + '/one active project')
            require(isinstance(value['entries'], list) and len(value['entries']) == 1, ident + '/one entry')
            entry = value['entries'][0]
            require(isinstance(entry, dict) and not envelope_fields.intersection(entry), ident + '/project DTO shape')
        else:
            # SaveAsyncが出す通常単一project DTOは暗黙version 1。内包v5は必ずworkspace。
            require(ident == 'ordinary' and {'mode', 'leftPath', 'rightPath'} <= set(value), ident + '/single version 1 DTO')
            entry = value
        require(entry['mode'] == 'Binary', ident + '/Binary mode')
        if ident == 'ordinary':
            require(not entry.get('basePath') and not any(entry.get(field) for field in
                    ('leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput')), 'ordinary two physical inputs')
            require(read(entry['rightPath']) == expected_bytes, 'ordinary right fixed golden full bytes')
            require(read(entry['leftPath']) == input_bytes, 'ordinary left fixed input full bytes')
        else:
            for field in ('leftArchiveInput', 'baseArchiveInput', 'rightArchiveInput'):
                source = entry[field]
                root_data = read(source['rootPath'])
                entries = zip_bytes(root_data)
                require(entries == {'leaf.bin': original}, field + '/all root entries CRC and original bytes')
                require(sha(root_data) == source['rootSha256'].upper(), field + '/root SHA')
                require(source['leafEntry'] == 'leaf.bin' and source['entryChain'] == [], field + '/fixed leaf')
                assets = source['workingTexts']
                require(len(assets) == 1 and assets[0]['kind'] == 'Binary', field + '/one Binary asset')
                asset = assets[0]
                snapshot = read(asset['snapshotPath'])
                require(snapshot == edited and sha(snapshot) == asset['sha256'].upper(), field + '/fixed edited full bytes and SHA')

    def workspace(path, ident):
        def read(name):
            require(isinstance(name, str) and name and '\0' not in name, 'physical input name')
            return physical(path.parent / name, work, fixed_input if ident == 'ordinary' else None).read_bytes()
        inspect_project(path.read_bytes(), read, ident)

    def package(path, ident):
        entries = zip_bytes(path.read_bytes())
        require('project.json' in entries, 'ZIP project document')
        used = {'project.json'}
        def read(name):
            name = virtual(name)
            require(name in entries, 'ZIP referenced entry exists')
            used.add(name)
            return entries[name]  # 仮想名をOS pathへ変換しない。
        inspect_project(entries['project.json'], read, ident)
        require(used == set(entries), 'ZIP has no unreferenced entries')

    rows = document(physical(work / 'cli-projects.json', work).read_bytes())
    require(len(rows) == 2 and {row['id'] for row in rows} == {'ordinary', 'archive'}, 'exact two CLI runs')
    for row in rows:
        ident = row['id']
        require(row['kind'] == ident and row['sourceMetadata'] == projects[ident], ident + '/GUI metadata identity')
        require(physical(row['proofPath'], work) == proof_path, ident + '/GUI proof identity')
        input_path = physical(row['projectFile'], work)
        require(input_path == physical(proof_path.parent / projects[ident]['projectFile'], work), ident + '/source project identity')
        copy_path = physical(row['copyFile'], work)
        package_path = physical(row['packageFile'], work)
        require(len({input_path, copy_path, package_path}) == 3, ident + '/distinct outputs')
        for key, command, target, suffix in (('copyResult', '--project-copy', copy_path, 'project-copy'),
                                              ('packageResult', '--package-project', package_path, 'package-project')):
            result = row[key]
            require(result['exitCode'] == 0 and result['pid'] > 0, ident + '/successful actual CLI process')
            require(result['name'] == 'binary-replace-' + ident + '-' + suffix, ident + '/exact run')
            require(result['arguments'] == [command, str(input_path), str(target)], ident + '/exact CLI argv')
        workspace(input_path, ident)
        workspace(copy_path, ident)
        package(package_path, ident)
    receipt = dict(success=True, projects=2, fullBytes=True, zipCrc=True, readerPid=os.getpid(),
                   proofFile=str(proof_path), oracle='fixed Frhed forward-grow golden/input and manual xABABABz/xXYZABABz')
    with args.receipt.open('x', encoding='utf-8') as output:
        json.dump(receipt, output, indent=2)
        output.write('\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
