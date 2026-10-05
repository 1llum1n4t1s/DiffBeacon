"""stdlib独立reader。製品DTOを使用せず、特殊nodeを開かない。"""
import datetime
import hashlib
import json
import os
import pathlib
import socket
import stat
import sys

def read(p):
    return json.loads(pathlib.Path(p).read_text(encoding='utf-8-sig'))

def digest(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for block in iter(lambda: f.read(65536), b''):
            h.update(block)
    return h.hexdigest().upper()

def snapshot(root):
    facts = {}
    for side in ('left', 'right', 'LEFT'):
        base = root / side
        if not base.exists() or side == 'LEFT' and os.path.samefile(base, root / 'left'):
            continue
        for p in [base, *sorted(base.rglob('*'))]:
            s = p.lstat()
            kind = 'directory' if stat.S_ISDIR(s.st_mode) else 'file' if stat.S_ISREG(s.st_mode) else 'fifo' if stat.S_ISFIFO(s.st_mode) else 'socket' if stat.S_ISSOCK(s.st_mode) else 'other'
            facts[p.relative_to(root).as_posix()] = dict(kind=kind, size=s.st_size if kind == 'file' else 0,
                sha=digest(p) if kind == 'file' else None, mtime=s.st_mtime_ns, ctime=s.st_ctime_ns,
                attrs=getattr(s, 'st_file_attributes', None), mode=stat.S_IMODE(s.st_mode))
    return facts

def command(call, code=2):
    assert call['ExitCode'] == code and call['Pid'] > 0 and call['CreationUtc'] and call['ExitObservedUtc'], 'actual command/exit'
    if code == 2:
        assert call['Stdout'] == '', 'rejection must leave stdout empty'

def regular(fixture, work):
    fixed = read(fixture / 'review-expectations.json')
    assert read(work / 'review-expectations.json') == fixed, 'review fixed expectations replaced'
    records = read(work / 'review-commands.json')
    assert [r['id'] for r in records] == [c['id'] for c in fixed['cli']], 'review exact CLI coverage'
    for expected, result in zip(fixed['cli'], records):
        root = pathlib.Path(result['work']).resolve()
        assert root == (work / 'review' / expected['id']).resolve(), 'review dedicated root'
        before, after = read(root / 'before.json'), read(root / 'after.json')
        assert before == after, expected['id'] + ' zero mutation including metadata'
        actual = snapshot(root)
        for side in ('left', 'right'):
            for relative, literal in expected['files'][side].items():
                p = root / side / relative
                assert p.read_bytes() == bytes.fromhex(literal), expected['id'] + ' independent literal'
        for row in after:
            p = root / row['Path']
            s = p.lstat()
            assert s.st_mtime_ns // 100 + 116444736000000000 == row['LastWriteFileTime'], 'actual mtime'
            assert (stat.S_ISDIR(s.st_mode) and row['Kind'] == 'directory') or (stat.S_ISREG(s.st_mode) and row['Kind'] == 'file'), 'actual kind'
            if row['Kind'] == 'file':
                assert s.st_size == row['Length'] and digest(p) == row['Sha256'], 'all actual bytes'
            if os.name == 'nt':
                assert s.st_file_attributes == row['Attributes'], 'all attrs'
            else:
                assert stat.S_IMODE(s.st_mode) == row['UnixMode'], 'all mode'
        assert set(actual) == {x['Path'] for x in after}, 'all membership'
        command(result['command'])
        if expected.get('plan'):
            command(result['plan'], 0)
            assert result['plan']['Stderr'] == '', 'readonly filter plan must parse and succeed without diagnostics'
            model = json.loads(result['plan']['Stdout'])
            assert [x['path'] for x in model['candidates']] == ['rules.flt'], 'filter readonly plan retains candidate'
            assert read(root / 'after-plan.json') == before, 'readonly plan no mutation'
            assert expected['stderrContains'] in result['command']['Stderr'], 'active filter sync must reject for output protection, not parser/other failure'
    return len(records)

def mac(fixture, work):
    records = read(work / 'review-mac-commands.json')
    if sys.platform != 'darwin':
        assert records == [], 'Mac must not be fabricated'
        return dict(actual=False, cases=0)
    fixed = read(fixture / 'review-expectations.json')['mac']
    assert [r['id'] for r in records] == [c['id'] for c in fixed], 'Mac coverage'
    for case, r in zip(fixed, records):
        root = pathlib.Path(r['work']).resolve()
        assert root == (work / 'review-mac' / case['id']).resolve(), 'Mac root'
        for helper in (r['before'], r['after']):
            command(helper, 0)
        before = json.loads(r['before']['Stdout'])
        after = json.loads(r['after']['Stdout'])
        assert before['facts'] == after['facts'], 'Mac no mutation before release'
        if case['kind'] == 'alias':
            assert r['release'] is None
            if before['aliasAvailable']:
                assert snapshot(root) == after['facts']
                for relative, payload in {'a.bin': b'\x01', 'Sub/a.bin': b'\x02', 'Sub/Sub/a.bin': b'\x03'}.items():
                    assert (root / 'left' / relative).read_bytes() == payload, 'nested alias fixed input'
                assert len(r['commands']) == 8 and r['skipped'] is None
                for call in r['commands']:
                    command(call)
            else:
                assert r['skipped'] == 'Case-sensitive filesystem: nested alias unavailable'
                assert r['commands'] == []
                assert before['separateRootsActual'], 'separate case-sensitive roots observed'
                for call in r['separateCommands']:
                    command(call, 0)
                command(read(root / 'separate-snapshot-process.json'), 0)
                separate = read(root / 'separate-after.json')
                for side in ('left', 'LEFT'):
                    assert (root / side / 'independent.bin').read_bytes() == b'\x31', 'case-sensitive independent root accepted'
                assert snapshot(root) == separate and set(separate) == set(after['facts']), 'separate actual membership/metadata'
                for path, value in separate.items():
                    if path not in ('left', 'left/independent.bin', 'LEFT', 'LEFT/independent.bin'):
                        assert value == after['facts'][path], 'case-sensitive source/unrelated entries preserved'
        else:
            assert before['facts']['right/node.bin']['kind'] == case['kind'], 'actual Mac destination type'
            source_path = 'left/node.bin' if case['position'] == 'leaf' else 'left/node.bin/child.bin'
            assert before['facts'][source_path]['sha'] == hashlib.sha256(b'\x41\x00\xff').hexdigest().upper(), 'Mac fixed source byte literal'
            assert len(r['commands']) == (1 if case['position'] == 'leaf' else 2)
            for call in r['commands']:
                command(call)
            command(r['release'], 0)
            release = json.loads(r['release']['Stdout'])
            assert release['beforeAfterEqual'] and release['released'] == str(root / 'right/node.bin'), 'Mac node lifecycle'
            assert not os.path.lexists(root / 'right/node.bin') and snapshot(root) == release['retained']
            assert set(release['retained']) == set(after['facts']) - {'right/node.bin'}, 'only own node released'
            for key, value in release['retained'].items():
                if key != 'right':
                    assert value == after['facts'][key], 'unrelated node retained'
    return dict(actual=True, cases=len(records))

def gui(fixture, output):
    paths = list((output / 'gui').rglob('folder-review-observations.json'))
    assert len(paths) == 1, 'review GUI observations'
    report = read(paths[0]); expected = read(fixture / 'review-expectations.json')['gui']
    rows = {x['id']: x for x in report['cases']}
    required = expected['portable'] + (expected['windows'] if os.name == 'nt' else [])
    assert set(rows) == set(required), 'GUI exact coverage'
    for name, row in rows.items():
        root = pathlib.Path(row['work'])
        assert root.parent == paths[0].parent, 'GUI dedicated case root'
        assert set(snapshot(root)) == {x['path'] for x in row['after']}, 'GUI exact current membership'
        assert row['rejected'], 'GUI button rejection'
        if name == 'active-filter-late':
            assert [x for x in row['before'] if x['path'] != 'right/rules.flt'] == [x for x in row['after'] if x['path'] != 'right/rules.flt'], 'only declared filter mutation'
            assert (root / 'left/a.bin').read_bytes() == b'\x01' and (root / 'right/a.bin').read_bytes() == b'\x02'
            before_filter = next(x for x in row['before'] if x['path'] == 'right/rules.flt')
            assert before_filter['sha256'] == hashlib.sha256(expected['filterBefore'].encode()).hexdigest().upper(), 'original filter literal'
            assert (root / 'right/rules.flt').read_bytes() == expected['filterAfter'].encode(), 'declared later filter literal'
            after_filter = next(x for x in row['after'] if x['path'] == 'right/rules.flt')
            assert all(before_filter[k] == after_filter[k] for k in ('directory', 'size', 'mtime', 'attributes')), 'filter same size/metadata hash replacement'
        else:
            assert row['before'] == row['after'], 'GUI button no mutation'
        for e in row['after']:
            p = root / e['path']; s = p.lstat()
            assert s.st_mtime_ns // 100 + 116444736000000000 == e['mtime'], 'GUI actual mtime'
            if not e['directory']:
                assert s.st_size == e['size'] and digest(p) == e['sha256'], 'GUI all actual bytes'
            if os.name == 'nt':
                assert s.st_file_attributes == e['attributes'], 'GUI attrs'
            else:
                assert stat.S_IMODE(s.st_mode) == e['mode'], 'GUI mode'
        if name == 'central-ads-container':
            assert row['adsAccepted'] and row['copyAttempted'], 'ADS must really be accepted before protection check'
            payload = bytes.fromhex(expected['adsHex'])
            assert pathlib.Path(row['adsPath']).read_bytes() == payload
            assert row['adsBefore'] == row['adsAfter'] == expected['adsHex'], 'ADS full literal retained'
            assert (root / 'left/base.bin').read_bytes() == bytes.fromhex(expected['adsMainLeft'])
            assert (root / 'right/base.bin').read_bytes() == bytes.fromhex(expected['adsMainRight'])
            assert (root / 'left/support.bin').read_bytes() == bytes.fromhex(expected['adsSupport'])
        elif name == 'extended-directory-container':
            assert os.name == 'nt' and row['folderAccepted'] and row['copyAttempted'], 'extended Folder must really be accepted'
            requested = row['requestedRoot']; observed = row['observedRoot']
            assert requested.startswith('\\\\?\\') and row['inputText'], 'requested extended spelling retained as evidence'
            assert os.path.samefile(requested, root / 'right') and os.path.samefile(observed, root / 'right'), 'actual same root identity'
            assert row['extendedPrefixNormalized'] == (not observed.startswith('\\\\?\\')), 'observed prefix normalization'
            assert (root / 'left/base.bin').read_bytes() == bytes.fromhex(expected['extendedMainLeft'])
            assert (root / 'right/base.bin').read_bytes() == bytes.fromhex(expected['extendedMainRight'])
        elif name != 'active-filter-late':
            for side in ('left', 'right'):
                for relative, literal in expected['nestedFiles'][side].items():
                    assert (root / side / relative).read_bytes() == bytes.fromhex(literal), 'GUI fixed nested literal'
    return len(rows)

def verify(fixture, work):
    return dict(cli=regular(fixture, work), mac=mac(fixture, work), gui=gui(fixture, work), fullBytesAndMetadata=True)

def helper():
    action, location, kind, position = sys.argv[2:6]
    root = pathlib.Path(location).resolve()
    assert '/folder-copy/review-mac/' in root.as_posix() and root.is_dir() and sys.platform == 'darwin'
    node = root / 'right/node.bin'
    if action == 'prepare':
        for side in ('left', 'right'):
            (root / side).mkdir()
        if kind == 'alias':
            for relative, data in {'a.bin': b'\x01', 'Sub/a.bin': b'\x02', 'Sub/Sub/a.bin': b'\x03'}.items():
                p = root / 'left' / relative; p.parent.mkdir(parents=True, exist_ok=True); p.write_bytes(data)
            available = (root / 'LEFT/Sub').exists() and os.path.samefile(root / 'left/Sub', root / 'LEFT/Sub')
            if not available:
                (root / 'LEFT').mkdir()
                (root / 'left/independent.bin').write_bytes(b'\x31')
                (root / 'LEFT/independent.bin').write_bytes(b'\x32')
            (root / 'alias.json').write_text(json.dumps({'aliasAvailable': available, 'separateRootsActual': not available and not os.path.samefile(root / 'left', root / 'LEFT')}))
        else:
            source = root / ('left/node.bin' if position == 'leaf' else 'left/node.bin/child.bin')
            source.parent.mkdir(parents=True, exist_ok=True); source.write_bytes(b'\x41\x00\xff')
            if kind == 'fifo':
                os.mkfifo(node)
            else:
                previous = os.getcwd()
                try:
                    os.chdir(node.parent)
                    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
                        sock.bind(node.name)
                finally:
                    os.chdir(previous)
        fixed = int(datetime.datetime(2002, 3, 4, 5, 6, 7, tzinfo=datetime.timezone.utc).timestamp() * 1_000_000_000)
        for p in [root, *sorted(root.rglob('*'))]:
            os.utime(p, ns=(fixed, fixed), follow_symlinks=False)
        before = snapshot(root)
        (root / 'helper-before.json').write_text(json.dumps(before))
    elif action == 'release':
        assert kind in ('fifo', 'socket') and node.parent.resolve() == root / 'right'
        before = read(root / 'helper-before.json'); after = read(root / 'helper-after.json')
        assert before == after == snapshot(root), 'release only after no-mutation proof'
        mode = node.lstat().st_mode
        assert stat.S_ISFIFO(mode) if kind == 'fifo' else stat.S_ISSOCK(mode)
        node.unlink()  # 実Macでこのhelperが作成した特殊nodeの寿命終了だけ。
        print(json.dumps({'beforeAfterEqual': True, 'released': str(node), 'retained': snapshot(root)})); return
    elif action == 'separate-snapshot':
        facts = snapshot(root); (root / 'separate-after.json').write_text(json.dumps(facts)); print(json.dumps({'facts': facts})); return
    elif action != 'snapshot':
        raise ValueError(action)
    facts = snapshot(root)
    if action == 'snapshot':
        (root / 'helper-after.json').write_text(json.dumps(facts))
    meta = read(root / 'alias.json') if kind == 'alias' else {}
    print(json.dumps({'facts': facts, **meta}))

if __name__ == '__main__':
    helper() if len(sys.argv) > 1 and sys.argv[1] == '--helper' else print(json.dumps(verify(pathlib.Path(sys.argv[1]).resolve(), pathlib.Path(sys.argv[2]).resolve())))
