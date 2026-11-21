"""独立 stdlib reader: 固定literal期待と実diskを照合し、製品DTOを使用しない。"""
import datetime, hashlib, json, os, pathlib, stat, sys, socket, importlib.util, struct, zlib

def review_reader(fixture):
    path=fixture/'verify-review.py'
    fixed=read(fixture/'copy-provenance.json')['fixedFiles']
    for name in ('verify-review.py','review-expectations.json'):
        assert digest(fixture/name)==fixed[name],'review reader/expected replaced '+name
    spec=importlib.util.spec_from_file_location('folder_review_reader',path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    return module

def special_snapshot(root):
    facts = {}
    for side in ['left','right','controls']:
        for path in [root/side] + sorted((root/side).rglob('*')):
            s=path.lstat();kind='directory' if stat.S_ISDIR(s.st_mode) else 'file' if stat.S_ISREG(s.st_mode) else 'fifo' if stat.S_ISFIFO(s.st_mode) else 'socket' if stat.S_ISSOCK(s.st_mode) else 'link' if stat.S_ISLNK(s.st_mode) else 'other'
            facts[path.relative_to(root).as_posix()]=dict(kind=kind,size=s.st_size if kind=='file' else 0,sha=digest(path) if kind=='file' else None,mtime=s.st_mtime_ns,ctime=s.st_ctime_ns,attributes=getattr(s,'st_file_attributes',None),mode=stat.S_IMODE(s.st_mode),target=os.readlink(path) if kind=='link' else None)
    return facts

def special_helper():
    action,location,kind,side,mixed=sys.argv[2:7]
    if mixed not in ('plain','mixed'):raise ValueError('special input mode must be plain or mixed')
    mixed=mixed=='mixed';root=pathlib.Path(location).resolve()
    assert '/folder-copy/special/' in root.as_posix() and root.is_dir(), 'dedicated special root'
    nodes=[root/s/'node.bin' for s in (['left','right'] if side=='both' else [side])]
    assert all(p.parent.resolve()==root/p.parent.name for p in nodes), 'node containment'
    if action=='prepare':
        for s in ['left','right','controls']:(root/s).mkdir()
        if os.name=='nt':
            import ctypes
            from ctypes import wintypes
            path=root/'left'/'payload.bin';path.touch()
            api=ctypes.WinDLL('kernel32',use_last_error=True)
            api.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE];api.CreateFileW.restype=wintypes.HANDLE
            api.DeviceIoControl.argtypes=[wintypes.HANDLE,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD),ctypes.c_void_p];api.DeviceIoControl.restype=wintypes.BOOL
            api.CloseHandle.argtypes=[wintypes.HANDLE];api.CloseHandle.restype=wintypes.BOOL
            handle=api.CreateFileW(str(path),0xC0000000,7,None,3,0,None)
            assert handle not in (None,ctypes.c_void_p(-1).value), 'CreateFileW '+str(ctypes.get_last_error())
            try:
                data=ctypes.create_string_buffer(b'\x01\x00') if kind=='compressed' else None;returned=wintypes.DWORD()
                assert api.DeviceIoControl(handle,0x9c040 if kind=='compressed' else 0x900c4,data,2 if data else 0,None,0,ctypes.byref(returned),None), 'DeviceIoControl '+str(ctypes.get_last_error())
            finally:assert api.CloseHandle(handle)
            with path.open('r+b') as f:
                if kind=='compressed':f.write(bytes(range(256))*256+b'\0')
                else:f.write(b'\xaa\xbb\xcc\xdd');f.seek(4194303);f.write(b'\x10\x20')
        else:
            assert sys.platform=='darwin'
            (root/'controls'/'target.bin').write_bytes(b'\x01')
            if kind=='case-alias':(root/'left'/'payload.bin').write_bytes(b'\x01\x02')
            for p in ([] if kind=='case-alias' else nodes):
                if kind=='fifo':os.mkfifo(p)
                elif kind=='socket':
                    previous=os.getcwd()
                    try:
                        os.chdir(p.parent)
                        with socket.socket(socket.AF_UNIX) as stream:stream.bind(p.name)
                    finally:os.chdir(previous)
                else:os.symlink('../controls/target.bin',p)
            if mixed:(root/('right' if side=='left' else 'left')/'node.bin').touch()
        fixed=int(datetime.datetime(2002,3,4,5,6,7,tzinfo=datetime.timezone.utc).timestamp())*1000000000
        capability=dict(platform=sys.platform,followSymlinksSupported=os.utime in os.supports_follow_symlinks,
            windowsGuardedDefault=os.name=='nt',macNoFollow=sys.platform=='darwin',checkedPaths=[])
        if os.name=='nt':
            state=root.lstat()
            assert stat.S_ISDIR(state.st_mode) and not stat.S_ISLNK(state.st_mode) and not state.st_file_attributes&1024,'special root regular directory without reparse'
        for s in ['left','right','controls']:
            for p in sorted((root/s).rglob('*'))+[root/s]:
                if os.name=='nt':
                    state=p.lstat()
                    assert p.resolve().is_relative_to(root) and (stat.S_ISREG(state.st_mode) or stat.S_ISDIR(state.st_mode)) and not stat.S_ISLNK(state.st_mode) and not state.st_file_attributes&1024,'guarded Windows utime regular entry'
                    capability['checkedPaths'].append(p.relative_to(root).as_posix())
                    os.utime(p,ns=(fixed,fixed))
                else:os.utime(p,ns=(fixed,fixed),follow_symlinks=False)
        (root/'utime-capability.json').write_text(json.dumps(capability,indent=2),encoding='utf-8')
        facts=special_snapshot(root);(root/'special-before.json').write_text(json.dumps(facts,indent=2),encoding='utf-8')
        print(json.dumps(dict(action=action,facts=facts,utimeCapability=capability,aliasAvailable=(root/'LEFT').is_dir() if kind=='case-alias' else None)))
    elif action=='snapshot':
        facts=special_snapshot(root);(root/'special-after.json').write_text(json.dumps(facts,indent=2),encoding='utf-8');print(json.dumps(dict(action=action,facts=facts)))
    elif action=='release':
        assert sys.platform=='darwin' and kind in ('fifo','socket')
        before=read(root/'special-before.json');after=read(root/'special-after.json');assert before==after==special_snapshot(root),'special node changed'
        released=[]
        for p in nodes:
            assert p.parent in [root/'left',root/'right'] and p.name=='node.bin' and p.lstat() and (stat.S_ISFIFO(p.lstat().st_mode) if kind=='fifo' else stat.S_ISSOCK(p.lstat().st_mode))
            os.unlink(p);released.append(str(p));assert not p.exists()
        print(json.dumps(dict(action=action,independentBeforeAfterEqual=True,released=released,retained=special_snapshot(root))))
    else:raise ValueError(action)
    return 0

def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))

def digest(path):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(65536), b''): h.update(chunk)
    return h.hexdigest().upper()

def inventory(root):
    result = {}
    for path in sorted(pathlib.Path(root).rglob('*')):
        state = path.lstat()
        assert not stat.S_ISLNK(state.st_mode), 'unexpected link ' + str(path)
        directory = stat.S_ISDIR(state.st_mode)
        assert directory or stat.S_ISREG(state.st_mode), 'unexpected nonregular ' + str(path)
        result[path.relative_to(root).as_posix()] = dict(kind='directory' if directory else 'file',
            size=0 if directory else state.st_size, sha=None if directory else digest(path),
            ticks=state.st_mtime_ns // 100 + 116444736000000000,
            attributes=getattr(state, 'st_file_attributes', None), mode=stat.S_IMODE(state.st_mode))
    return result

def captured(path):
    return {e['Path']: e for e in read(path)}

def compare_capture(root, before, after, source_side, source_expected):
    actual = inventory(pathlib.Path(root) / source_side)
    prefix = source_side + '/'
    previous = {p[len(prefix):]: v for p, v in before.items() if p.startswith(prefix)}
    current = {p[len(prefix):]: v for p, v in after.items() if p.startswith(prefix)}
    assert previous == current, 'source snapshot changed'
    assert set(actual) == set(previous), 'source membership changed'
    for name, state in actual.items():
        sample = previous[name]
        assert (state['kind'],state['size'],state['sha'],state['ticks']) == (sample['Kind'],sample['Length'],sample['Sha256'],sample['LastWriteFileTime']), name
        if os.name == 'nt': assert state['attributes'] == sample['Attributes'], name + ' source attributes'
        else: assert state['mode'] == sample['UnixMode'], name + ' source mode'

def literals(root, side, group):
    actual = inventory(pathlib.Path(root) / side)
    files = group[side + 'Files']
    directories = set(group[side + 'Dirs'])
    assert set(actual) == set(files) | directories, side + ' exact membership'
    for name, state in actual.items():
        if name in directories:
            assert state['kind'] == 'directory', name
        else:
            expected = bytes.fromhex(files[name])
            assert state['kind'] == 'file' and state['size'] == len(expected) and state['sha'] == hashlib.sha256(expected).hexdigest().upper(), name + ' literal bytes'
            readonly = side + '/' + name in group.get('readonly', [])
            if os.name == 'nt': assert bool(state['attributes'] & 1) == readonly, name + ' readonly'
            else: assert bool(state['mode'] & 0o200) != readonly, name + ' readonly mode'
    return actual

def cli(fixture, work):
    expected = read(fixture / 'copy-expectations.json')
    assert read(work / 'expectations.json') == expected, 'fixed expectations replaced'
    records = read(work / 'commands.json')
    assert [r['id'] for r in records] == [c['id'] for c in expected['cases']] + ['large-stream'], 'command coverage'
    fixed_ticks = int(datetime.datetime.fromisoformat(expected['fixedMtimeUtc'].replace('Z','+00:00')).timestamp()) * 10000000 + 116444736000000000
    destination_ticks = int(datetime.datetime.fromisoformat(expected['fixedDestinationDirectoryMtimeUtc'].replace('Z','+00:00')).timestamp()) * 10000000 + 116444736000000000
    for case, record in zip(expected['cases'], records):
        root = pathlib.Path(record['work']).resolve()
        assert root == (work / 'cases' / case['id']).resolve(), 'wrong workroot'
        command = record['command']
        assert command['ExitCode'] == case['exit'] and command['Pid'] > 0 and command['CreationUtc'] and command['ExitObservedUtc'], case['id'] + ' process'
        before, after = captured(root / 'before.json'), captured(root / 'after.json')
        for path, sample in before.items():
            baseline = destination_ticks if sample['Kind']=='directory' and (path==case['destination'] or path.startswith(case['destination']+'/')) else fixed_ticks
            assert sample['LastWriteFileTime']==baseline, case['id']+' fixed before metadata baseline '+path
        assert before[case['source']]==after[case['source']], case['id']+' source root metadata retained'
        controls=inventory(root/'controls')
        assert set(controls)==set(expected['controls']),'control membership'
        for name,literal in expected['controls'].items():
            assert controls[name]['sha']==hashlib.sha256(literal.encode('utf-8')).hexdigest().upper(),'control fixed bytes'
            assert before['controls/'+name]==after['controls/'+name],'control retained'
        for side in ('left','right'): literals(root, side, case['after'])
        compare_capture(root, before, after, case['source'], case['before'])
        destination = literals(root, case['destination'], case['after'])
        for name, state in destination.items():
            full = case['destination'] + '/' + name
            if state['kind'] == 'file':
                assert state['ticks'] == fixed_ticks, name + ' file mtime'
                if name in case['operations']:
                    source = before[case['source'] + '/' + name]
                    if os.name == 'nt': assert state['attributes'] == source['Attributes'], name + ' file attributes'
                    else: assert state['mode'] == source['UnixMode'], name + ' file mode'
            elif name in case['emptyDirectoryMtimes']:
                assert state['ticks'] == fixed_ticks, name + ' copied empty directory source mtime'
            elif full not in before:
                assert state['ticks'] > destination_ticks, name + ' new populated directory natural mtime'
            elif name not in case['changedDirectories']:
                assert after[full] == before[full], name + ' unchanged directory full metadata'
            else:
                assert state['ticks']==before[full]['LastWriteFileTime'] or state['ticks']>destination_ticks, name + ' existing populated directory retained or natural mtime'
                assert state['ticks']!=fixed_ticks, name + ' populated directory source mtime not forced'
        if case['exit'] != 0:
            assert before == after and command['Stdout'] == '', case['id'] + ' zero mutation rejection'
        elif case['json']:
            result = json.loads(command['Stdout'])
            assert result['succeeded'] and not result['cancelled'], case['id']
            assert [e['path'] for e in result['entries']] == case['operations'], case['id'] + ' manifest'
            assert all(e['status'] == 'Published' and e['published'] for e in result['entries']), case['id']
            assert result['published'] == len(case['operations']) and result['mutationOccurred'] == bool(case['operations']), case['id']
            logical = sum(len(bytes.fromhex(case['before'][case['source']+'Files'][p])) for p in case['operations'] if p in case['before'][case['source']+'Files'])
            destination_bytes = sum(len(bytes.fromhex(case['before'][case['destination']+'Files'][p])) for p in case['operations'] if p in case['before'][case['destination']+'Files'])
            assert (result['logicalBytes'],result['readBytes'],result['writeBytes']) == (logical,5*logical+3*destination_bytes,logical), case['id'] + ' IO budget'
    large = work / 'large'
    before, after = captured(large/'before.json'), captured(large/'after.json')
    compare_capture(large, before, after, 'left', {})
    size = expected['largeFile']['size']; block = bytes(range(256)) * 256
    for side in ('left','right'):
        path = large / side / 'payload.bin'
        assert path.stat().st_size == size and digest(path) == expected['largeFile']['sha256'], 'large SHA'
        offset = 0
        with path.open('rb') as stream:
            for data in iter(lambda: stream.read(65536), b''):
                assert data == block[:len(data)], 'large full pattern'
                offset += len(data)
        assert offset == size and path.stat().st_mtime_ns//100 + 116444736000000000 == fixed_ticks
    assert records[-1]['command']['ExitCode'] == 0
    plans=read(work/'plan-commands.json')
    planned=[case for case in expected['cases'] if 'planCandidates' in case]
    assert [r['id'] for r in plans]==[c['id'] for c in planned], 'additional plan command coverage'
    original=read(fixture/'model-expectations.json')
    for case,record in zip(planned,plans):
        command=record['command'];root=pathlib.Path(record['work']).resolve()
        assert root==(work/'cases'/case['id']).resolve(),'plan workroot'
        assert command['ExitCode']==0 and command['Pid']>0 and command['CreationUtc'] and command['ExitObservedUtc'] and command['Stderr']=='','plan actual process'
        assert command['Arguments'][0]=='--folder-plan','separate plan command'
        document=json.loads(command['Stdout'])
        candidates=[c['path'] for c in document['candidates']]
        original_case=next(c for c in original['cases'] if c['id']==case['id'])
        assert candidates==sorted(case['planCandidates'],key=lambda p:(p.count('/'),p)) and case['planCandidates']==original_case['candidates'],'original model candidate literal retained'
        assert captured(root/'before.json')==captured(root/'after-plan.json')==captured(root/'after.json'),'plan and physical conflict execution zero mutation'
        conflict=next(c for c in document['candidates'] if c['path']=='reverse-conflict')
        assert conflict['source']['kind']=='File' and conflict['destination']['kind']=='Directory' and conflict['destination']['filtered'] and not conflict['physicalDirectory'],'filtered model preserves destination physical kind'
    return dict(cases=len(expected['cases']),additionalPlanCommands=len(plans),largeBytes=size,fullSourceAndDestination=True)

def main():
    fixture, work = map(lambda p: pathlib.Path(p).resolve(), sys.argv[1:3])
    provenance = read(fixture/'copy-provenance.json')
    for name, sha in provenance['fixedFiles'].items(): assert digest(fixture/name) == sha, 'fixed reader/expectations replaced ' + name
    result = dict(accepted=False, failures=[])
    try:
        result['cli'] = cli(fixture,work)
        result['gui'] = gui(fixture,work)
        result['special'] = special(fixture,work)
        reviewer=review_reader(fixture)
        result['review'] = dict(cli=reviewer.regular(fixture,work),mac=reviewer.mac(fixture,work))
        result['accepted'] = True
    except Exception as error:
        result['failures'].append(type(error).__name__ + ': ' + str(error))
    (work/'verify-copy-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False))
    return 0 if result['accepted'] else 2

def special(fixture,work):
    records=read(work/'special-commands.json');expected=read(fixture/'copy-expectations.json')['special']
    fixed=expected['windows'] if os.name=='nt' else expected['mac'] if sys.platform=='darwin' else []
    ids=[c['id'] if os.name=='nt' else c['kind']+'-'+c['side']+'-'+c.get('mode','content')+('-mixed' if c.get('mixed') else '') for c in fixed]
    assert [r['id'] for r in records]==ids,'special coverage'
    total=0
    for case,r in zip(fixed,records):
        root=pathlib.Path(r['work']).resolve();assert root==(work/'special'/r['id']).resolve(),'special wrong root'
        for helper in [r['before'],r['after']]+([r['release']] if r['release'] else []):
            assert helper['ExitCode']==0 and helper['Pid']>0 and helper['CreationUtc'] and helper['ExitObservedUtc'] and helper['Stderr']=='','special helper process'
        before=json.loads(r['before']['Stdout'])['facts'];after=json.loads(r['after']['Stdout'])['facts']
        capability=json.loads(r['before']['Stdout'])['utimeCapability']
        assert read(root/'utime-capability.json')==capability,'utime capability evidence replaced'
        assert read(root/'special-before.json')==before and read(root/'special-after.json')==after,'special evidence replaced'
        if os.name=='nt':
            assert capability['platform']=='win32' and capability['windowsGuardedDefault'] and not capability['macNoFollow'],'Windows explicit guarded utime scope'
            assert set(capability['checkedPaths'])==set(before),'all special Windows entries checked'
            assert all(v['kind'] in ('file','directory') and not v['attributes']&1024 for v in before.values()),'special regular no reparse'
            for name in before:
                if name.startswith('left/') or name=='controls':assert before[name]==after[name],'special source retained'
            source=root/'left'/'payload.bin';dest=root/'right'/'payload.bin'
            assert source.stat().st_size==dest.stat().st_size==case['size'] and digest(source)==digest(dest),'special full logical bytes'
            for path in [source,dest]:
                offset=0
                with path.open('rb') as stream:
                    for data in iter(lambda:stream.read(65536),b''):
                        if case['pattern']=='periodic-256':assert data==(bytes(range(256))*256)[:len(data)]
                        else:
                            expected_data=bytearray(len(data))
                            for where,value in [(0,0xaa),(1,0xbb),(2,0xcc),(3,0xdd),(4194303,0x10),(4194304,0x20)]:
                                if offset<=where<offset+len(data):expected_data[where-offset]=value
                            assert data==expected_data,'sparse all zero spans and fixed head/tail'
                        offset+=len(data)
                assert offset==case['size']
            assert before['left/payload.bin']['attributes']&case['sourceAttribute'],'source attribute actually set'
            assert after['right/payload.bin']['mtime']==before['left/payload.bin']['mtime'],'special file mtime'
            assert after['right/payload.bin']['attributes']& (512|2048)==0,'normal destination attribute scope'
            assert special_snapshot(root)==after,'special actual final state'
            assert r['commands'][0]['ExitCode']==0 and json.loads(r['commands'][0]['Stdout'])=={'copied':'payload.bin'},'legacy exact JSON object'
            total+=case['size']
        elif r['kind']=='case-alias':
            assert capability['platform']=='darwin' and capability['macNoFollow'] and not capability['windowsGuardedDefault'],'Mac no-follow retained'
            available=json.loads(r['before']['Stdout'])['aliasAvailable']
            assert before==after==special_snapshot(root),'alias input retained'
            assert r['release'] is None
            if available:
                assert len(r['commands'])==2 and all(c['ExitCode']==2 and c['Stdout']=='' and c['Pid']>0 and c['CreationUtc'] and c['ExitObservedUtc'] for c in r['commands']),'alias same-root rejected'
            else:assert r['commands']==[] and r['skipped']=='Case-sensitive filesystem: Directory.Exists(LEFT) is false','case alias actual unavailable scope'
        else:
            assert capability['platform']=='darwin' and capability['macNoFollow'] and not capability['windowsGuardedDefault'],'Mac special no-follow retained'
            assert before==after,'Mac source/destination before after unchanged'
            directory=r['commands'][0];model=json.loads(directory['Stdout']);row=model['entries'][0]
            if r['kind']=='link':assert row['kind']=='SymbolicLink' and directory['ExitCode']==(0 if r['side']=='both' else 1) and row['status']==('Equal' if r['side']=='both' else 'LeftOnly' if r['side']=='left' else 'RightOnly')
            else:assert directory['ExitCode']==2 and row['status']=='Error' and row['error'],'Mac nonregular opening blocked'
            for call in r['commands'][1:]:assert call['ExitCode']==2 and call['Stdout']=='','Mac copy zero mutation rejection'
            for call in r['commands']:assert call['Pid']>0 and call['CreationUtc'] and call['ExitObservedUtc'],'Mac actual process termination'
            if r['kind']=='link':assert special_snapshot(root)==after and r['release'] is None,'link retained diagnostic'
            else:
                release=json.loads(r['release']['Stdout']);assert release['independentBeforeAfterEqual'],'Mac independent lifecycle check'
                nodes=[root/s/'node.bin' for s in (['left','right'] if r['side']=='both' else [r['side']])]
                assert release['released']==[str(p) for p in nodes] and all(not p.exists() for p in nodes),'Mac node lifetime released'
                actual=special_snapshot(root);assert actual==release['retained'],'Mac retained final metadata'
                assert set(actual)==set(before)-{p.relative_to(root).as_posix() for p in nodes},'Mac only created node released'
                for name,state in actual.items():
                    if name not in [p.parent.relative_to(root).as_posix() for p in nodes]:assert state==before[name],'Mac other input retained'
    return dict(cases=len(records),logicalBytes=total,macActual=sys.platform=='darwin',windowsActual=os.name=='nt',advancedMetadataGuarantee=False)

def cancellations(fixture, work, parent):
    path = parent / 'folder-cancellation-observations.json'
    report = read(path)
    assert report['complete'], 'cancellation observations incomplete'
    rows = report['cases']
    names = ['cancel-button', 'partial-output-cancel', 'cancel-preflight', 'cancel-refresh']
    assert [r['observation']['name'] for r in rows] == names, 'cancellation exact coverage'
    fixed = read(fixture / 'copy-expectations.json')['gui']
    fixed_ticks = 10152183670000000 + 116444736000000000
    entries = 0
    for row in rows:
        sample = row['observation']; name = sample['name']
        copied = [] if name in ('cancel-button', 'cancel-preflight') else ['a.bin']
        assert sample['toRight'] and sample['all'] and sample['confirm'] and sample['copied'] == copied
        source = pathlib.Path(sample['source']).resolve(); destination = pathlib.Path(sample['destination']).resolve()
        assert source == (parent / name / 'left').resolve() and destination == (parent / name / 'right').resolve()
        status = row['status']
        if name == 'cancel-button':
            assert status == 'フォルダーコピーを中止しました。' and sample['results'] == []
            assert not any(k in sample for k in ('succeeded', 'mutation', 'published'))
        else:
            assert status.startswith('コピー結果: 公開 ' + str(len(copied)) + ' 件')
            assert sample['mutation'] == bool(copied) and sample['published'] == len(copied)
            assert sample['succeeded'] == (name == 'cancel-refresh')
            expected_status = ['Cancelled', 'NotExecuted', 'NotExecuted'] if name == 'cancel-preflight' else ['Published'] if name == 'cancel-refresh' else ['Published', 'Cancelled', 'NotExecuted']
            assert [r['path'] for r in sample['results']] == (['a.bin'] if name == 'cancel-refresh' else ['a.bin', 'b.bin', 'same.bin'])
            assert [r['status'] for r in sample['results']] == expected_status
            assert [r['published'] for r in sample['results']] == [s == 'Published' for s in expected_status]
        if name == 'cancel-refresh': assert '再比較を完了できませんでした:' in status
        assert row['png'] == 'folder-' + name + '.png'
        assert (work / 'gui' / row['png']).read_bytes().startswith(b'\x89PNG\r\n\x1a\n')
        before_metadata = {r['Path']: r for r in row['beforeMetadata']}
        after_metadata = {r['Path']: r for r in row['afterMetadata']}
        expected_metadata = set()
        for side, root in [('left', source), ('right', destination)]:
            expected = dict(fixed[side + 'Files'])
            if side == 'right':
                for p in copied: expected[p] = fixed['leftFiles'][p]
            actual = inventory(root)
            assert set(actual) == set(expected) | set(fixed['directories']), name + ' exact input/output membership'
            before = {r['path']: r for r in sample['beforeSource' if side == 'left' else 'beforeDestination']}
            after = {r['path']: r for r in sample['afterSource' if side == 'left' else 'afterDestination']}
            assert set(before) == set(after) == set(actual)
            for p, literal in fixed[side + 'Files'].items():
                payload = bytes.fromhex(literal)
                assert (before[p]['size'], before[p]['sha256']) == (len(payload), hashlib.sha256(payload).hexdigest().upper())
            for p, literal in expected.items(): assert (root / p).read_bytes() == bytes.fromhex(literal), name + ' independent full bytes'
            for p in ['.', *actual]:
                key = side if p == '.' else side + '/' + p
                expected_metadata.add(key)
                state = (root if p == '.' else root / p).lstat()
                fact = after_metadata[key]; original = before_metadata[key]
                assert (fact['Directory'], fact['Size'], fact['Mtime']) == (stat.S_ISDIR(state.st_mode), 0 if stat.S_ISDIR(state.st_mode) else state.st_size, state.st_mtime_ns // 100 + 116444736000000000)
                if os.name == 'nt': assert fact['Attributes'] == state.st_file_attributes
                else: assert fact['Mode'] == stat.S_IMODE(state.st_mode)
                assert original['Mtime'] == fixed_ticks, name + ' literal original timestamp'
                if side == 'left' or p not in ['.', *copied]: assert original == fact, name + ' unchanged metadata'
                elif p == '.' and not copied: assert original == fact, name + ' refused root retained'
                elif p == '.':
                    assert all(original[k] == fact[k] for k in original if k != 'Mtime') and fact['Mtime'] >= original['Mtime']
                else:
                    source_fact = before_metadata['left/' + p]
                    assert (fact['Size'], fact['Mtime'], fact['Attributes']) == (source_fact['Size'], source_fact['Mtime'], source_fact['Attributes'])
                    if os.name != 'nt': assert fact['Mode'] == source_fact['Mode']
                if p != '.':
                    captured = after[p]
                    assert (captured['directory'], captured['size'], captured['sha256'], captured['mtime']) == (actual[p]['kind'] == 'directory', actual[p]['size'], actual[p]['sha'], actual[p]['ticks'])
                    if os.name == 'nt': assert captured['attributes'] == actual[p]['attributes']
                    else: assert captured['mode'] == actual[p]['mode']
                entries += 1
            if side == 'left' or not copied: assert before == after, name + ' snapshot retained'
        assert set(before_metadata) == set(after_metadata) == expected_metadata
    return dict(cases=4, entries=entries, fullBytesAndMetadata=True, terminalStatus=True)

def screenshot_size(path):
    data = path.read_bytes()
    assert len(data) <= 16 * 1024 * 1024 and data[:8] == b'\x89PNG\r\n\x1a\n', 'GUI actual PNG'
    offset = 8; header = None; compressed = bytearray(); ended = False
    while offset < len(data):
        assert offset + 12 <= len(data), 'PNG chunk header'
        length = struct.unpack('>I', data[offset:offset+4])[0]
        kind = data[offset+4:offset+8]; end = offset + length + 12
        assert end <= len(data), 'PNG chunk bounds'
        body = data[offset+8:end-4]
        assert zlib.crc32(kind + body) & 0xffffffff == struct.unpack('>I', data[end-4:end])[0], 'PNG CRC'
        if kind == b'IHDR':
            assert offset == 8 and header is None and length == 13, 'PNG IHDR'
            header = struct.unpack('>IIBBBBB', body)
        elif kind == b'IDAT':
            assert header is not None and not ended, 'PNG data order'
            compressed.extend(body)
        elif kind == b'IEND':
            assert length == 0 and end == len(data), 'PNG IEND'
            ended = True
        offset = end
    assert header is not None and ended and compressed, 'PNG complete'
    width, height, depth, color, compression, filtering, interlace = header
    assert 0 < width <= 8192 and 0 < height <= 8192 and depth == 8 and color in (2, 6), 'screenshot format'
    assert (compression, filtering, interlace) == (0, 0, 0), 'screenshot encoding'
    stride = 1 + width * (3 if color == 2 else 4); expected = stride * height
    assert expected <= 32 * 1024 * 1024, 'screenshot decoded bound'
    decoder = zlib.decompressobj(); raw = decoder.decompress(compressed, expected + 1)
    assert decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail and len(raw) == expected, 'PNG full decoded scanlines'
    assert all(raw[row * stride] <= 4 for row in range(height)), 'PNG row filters'
    return width, height

def gui(fixture, work):
    reports = list((work/'gui').rglob('folder-copy-observations.json'))
    assert len(reports) == 1, 'actual GUI observations missing or ambiguous'
    report = read(work/'gui'/'ui-report.json')
    assert report['assertions'] and all(a['passed'] for a in report['assertions']), 'GUI assertions failed'
    observations = read(reports[0])
    assert observations['complete'], 'GUI observations not complete'
    fixed=read(fixture/'copy-expectations.json')['gui']
    expected={c['name']:c for c in fixed['cases']}
    observed={c['name']:c for c in observations['cases']}
    assert set(expected)==set(observed), 'GUI exact case coverage'
    entries=files=byte_count=0
    for name,case in expected.items():
        sample=observed[name]
        source=pathlib.Path(sample['source']).resolve();destination=pathlib.Path(sample['destination']).resolve()
        assert source.parent.parent==reports[0].parent.resolve() and destination.parent==source.parent, 'GUI wrong root'
        assert source.name==case['source'] and sample['copied']==case['copied'], name+' direction/expected copy'
        source_literals=dict(fixed[case['source']+'Files'])
        before_source={e['path']:e for e in sample['beforeSource']}
        after_source={e['path']:e for e in sample['afterSource']}
        before_dest={e['path']:e for e in sample['beforeDestination']}
        after_dest={e['path']:e for e in sample['afterDestination']}
        destination_literals=fixed[('right' if case['source']=='left' else 'left')+'Files']
        assert set(before_dest)==set(destination_literals)|set(fixed['directories']),name+' destination original membership'
        for path,literal in destination_literals.items():
            if path=='a.bin' and name in ('all-tab-archive-protection','workspace-protection'):continue
            assert before_dest[path]['sha256']==hashlib.sha256(bytes.fromhex(literal)).hexdigest().upper() and before_dest[path]['size']==len(bytes.fromhex(literal)),name+' destination original bytes'
        if name=='source-readonly':
            assert before_source['a.bin']['attributes']&1 if os.name=='nt' else not before_source['a.bin']['mode']&0o200,name+' readonly source actually present'
        assert set(before_source)==set(source_literals)|set(fixed['directories']), name+' original membership'
        for path,hex_data in source_literals.items():
            payload=bytes.fromhex(hex_data)
            assert before_source[path]['sha256']==hashlib.sha256(payload).hexdigest().upper() and before_source[path]['size']==len(payload),name+' original literal'
        if case['sourceChange']:
            path,hex_data=case['sourceChange'].split('=');source_literals[path]=hex_data
            for existing,fact in before_source.items():
                if existing==path:assert all(fact[k]==after_source[existing][k] for k in ['directory','size','mtime','attributes']),name+' same-size/metadata source replacement'
                else:assert fact==after_source[existing],name+' only declared source change'
        actual_source,actual_dest=inventory(source),inventory(destination)
        assert set(actual_source)==set(source_literals)|set(fixed['directories']),name+' source final membership'
        for path,hex_data in source_literals.items():
            payload=bytes.fromhex(hex_data)
            assert actual_source[path]['size']==len(payload) and actual_source[path]['sha']==hashlib.sha256(payload).hexdigest().upper(),name+' source literal'
        for root,actual,capture in [(source,actual_source,after_source),(destination,actual_dest,after_dest)]:
            assert set(actual)==set(capture),name+' observed membership'
            for path,state in actual.items():
                fact=capture[path]
                assert (state['kind']=='directory',state['size'],state['sha'],state['ticks'])==(fact['directory'],fact['size'],fact['sha256'],fact['mtime']),name+' actual snapshot'
                if os.name=='nt':assert state['attributes']==fact['attributes'],name+' actual attributes'
                else:assert state['mode']==fact['mode'],name+' actual mode'
                entries+=1;files+=state['kind']=='file';byte_count+=state['size']
        if not case['sourceChange']:assert before_source==after_source,name+' source retained'
        expected_files=dict(fixed[('right' if case['source']=='left' else 'left')+'Files'])
        for path in case['copied']:expected_files[path]=source_literals[path]
        for path,hex_data in expected_files.items():
            if path=='a.bin' and name in ('all-tab-archive-protection','workspace-protection'):
                assert after_dest[path]==before_dest[path],name+' protected control bytes'
            else:
                payload=bytes.fromhex(hex_data)
                assert actual_dest[path]['size']==len(payload) and actual_dest[path]['sha']==hashlib.sha256(payload).hexdigest().upper(),name+' final destination literal'
        assert set(actual_dest)==set(expected_files)|set(fixed['directories']),name+' destination exact set'
        if not case['copied']:assert before_dest==after_dest,name+' output retained'
        for path in case['copied']:
            assert after_dest[path]['mtime']==before_source[path]['mtime'],name+' copied mtime'
            if os.name=='nt':assert after_dest[path]['attributes']==before_source[path]['attributes'],name+' copied attrs'
            else:assert after_dest[path]['mode']==before_source[path]['mode'],name+' copied mode'
        if case['partial']:
            assert sample['succeeded'] is False and sample['mutation'] and sample['published']==1,name+' partial aggregate'
            rows=sample['results']
            assert [r['path'] for r in rows]==['a.bin','b.bin','same.bin'] and rows[0]['published'] and not rows[1]['published'] and rows[2]['status']=='NotExecuted',name+' partial individual'
        elif case['copied']:assert sample['succeeded'],name+' completed'
        if name in ('same-mtime-source-change','unknown-member'):assert sample['mutation'] is False and sample['published']==0,name+' preflight zero mutation'
        if name=='all-tab-archive-protection':
            import zipfile
            with zipfile.ZipFile(destination/'a.bin') as archive:
                assert archive.testzip() is None and archive.namelist()==['leaf.bin'] and archive.read('leaf.bin')==b'\x88'
    pngs=list((work/'gui').glob('folder-copy-*.png'))
    names = ['normal', 'minimum', 'minimum-many-tabs']
    assert {p.name for p in pngs} == {'folder-copy-' + name + suffix + '.png' for name in names for suffix in ('', '-toolbar')}, 'GUI captured PNG coverage'
    for name in ['normal','minimum','minimum-many-tabs']:
        bounds=read(reports[0].parent/(name+'-bounds.json'))
        assert bounds['listHeight']>=100 and bounds['firstRowHeight']>0 and bounds['firstRowY']>=bounds['listY'] and bounds['firstRowY']+bounds['firstRowHeight']<=bounds['listY']+bounds['listHeight'],'actual first row geometry'
        if name=='minimum-many-tabs':assert bounds['tabs']==45 and bounds['width']==850 and bounds['height']==550
        for suffix in ('', '-toolbar'):
            assert screenshot_size(work/'gui'/('folder-copy-' + name + suffix + '.png')) == (bounds['width'], bounds['height']), 'GUI decoded PNG dimensions'
        toolbar = read(reports[0].parent/(name+'-toolbar-bounds.json'))
        assert [row['control'] for row in toolbar] == ['left input', 'right input', 'compare', 'excludes', 'filter'], 'toolbar exact operation coverage'
        for row in toolbar:
            assert row['reachable'] is True and row['height'] > 0 and row['viewportHeight'] > 0, 'toolbar operation visible'
            assert row['y'] >= -.01 and row['y'] + row['height'] <= row['viewportHeight'] + .01, 'toolbar actual viewport geometry'
    reviews=review_reader(fixture).gui(fixture,work)
    cancelled=cancellations(fixture,work,reports[0].parent)
    return dict(assertions=len(report['assertions']),observations=len(observations['cases']),reviewObservations=reviews,cancellationObservations=cancelled,entries=entries,files=files,bytes=byte_count,pngs=6,toolbarOperations=15,fullBytesAndMetadata=True)

if __name__ == '__main__': sys.exit(special_helper() if len(sys.argv)>1 and sys.argv[1]=='--special' else main())
