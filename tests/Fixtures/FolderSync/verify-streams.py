"""Windows専用・独立stdlib reader。product helper/DTOをimportしない。"""
import ctypes, hashlib, json, os, pathlib, stat, sys
from ctypes import wintypes as w
SRC={'::$DATA':bytes.fromhex('0102030405'), ':empty:$DATA':b'',
     ':trailing :$DATA':bytes.fromhex('10111213141516171819'),
     ':日本😀:$DATA':bytes.fromhex('202122232425262728')}
DST={'::$DATA':bytes.fromhex('A0A1A2A3A4A5A6'), ':named:$DATA':bytes.fromhex('B0B1'), ':old:$DATA':bytes.fromhex('C0C1C2')}
IDS=['overwrite','fresh','empty-main','plain','directory','long-path']
GUI_IDS=IDS+['io179','io180','source-sha-change','destination-sha-change','cancel']
FIXED_NS=1015218367000000000
DEST_DIRECTORY_NS=1301983628000000000
PORTABLE_ATTRIBUTES=0x27  # ReadOnly/Hidden/System/Archive。圧縮やsparseは転写契約外。
class StreamData(ctypes.Structure):
    _fields_=[('size',ctypes.c_int64),('name',ctypes.c_uint16*296)]
assert ctypes.sizeof(StreamData)==600 and StreamData.name.offset==8

def extended(path):
    value=os.path.abspath(path)
    return value if value.startswith('\\\\?\\') else '\\\\?\\UNC\\'+value[2:] if value.startswith('\\\\') else '\\\\?\\'+value

def streams(path):
    assert os.name=='nt', 'Windows実行が必要'
    api=ctypes.WinDLL('kernel32',use_last_error=True)
    api.FindFirstStreamW.argtypes=[w.LPCWSTR,ctypes.c_int,ctypes.POINTER(StreamData),w.DWORD];api.FindFirstStreamW.restype=w.HANDLE
    api.FindNextStreamW.argtypes=[w.HANDLE,ctypes.POINTER(StreamData)];api.FindNextStreamW.restype=w.BOOL
    api.FindClose.argtypes=[w.HANDLE];api.FindClose.restype=w.BOOL
    data=StreamData();handle=api.FindFirstStreamW(extended(path),0,ctypes.byref(data),0)
    invalid=ctypes.c_void_p(-1).value; result={}
    if handle==invalid:
        error=ctypes.get_last_error()
        assert error==38 and pathlib.Path(path).is_dir(), 'FindFirstStreamW '+str(error)
        return result
    assert handle is not None
    primary=None
    try:
        while True:
            raw=list(data.name); assert 0 in raw,'unterminated UTF16'
            suffix=bytes().join(v.to_bytes(2,'little') for v in raw[:raw.index(0)]).decode('utf-16-le')
            assert suffix.startswith(':') and suffix.endswith(':$DATA') and data.size>=0 and suffix not in result
            with open(extended(path)+(suffix if suffix!='::$DATA' else ''),'rb') as f: payload=f.read()
            assert len(payload)==data.size,'enum length differs from all bytes'
            result[suffix]={'size':len(payload),'hex':payload.hex().upper(),'sha256':hashlib.sha256(payload).hexdigest().upper()}
            if not api.FindNextStreamW(handle,ctypes.byref(data)):
                assert ctypes.get_last_error()==38,'FindNextStreamW non EOF';break
        assert len(result)<=16,'fixed fixture unexpected descriptor count'
    except BaseException as error:
        primary=error;raise
    finally:
        if not api.FindClose(handle):
            close_error=ctypes.get_last_error()
            if primary is not None: primary.add_note('FindClose '+str(close_error))
            else: raise OSError(close_error,'FindClose')
    return result

def relative(case):
    return '/'.join(['l'*40]*8+['payload.bin']) if case=='long-path' else 'tree/payload.bin' if case=='directory' else 'payload.bin'

def source(case):
    result=dict(SRC)
    if case=='empty-main':result['::$DATA']=b''
    if case=='plain':result={'::$DATA':SRC['::$DATA']}
    if case=='source-sha-change':result[':日本😀:$DATA']=bytes.fromhex('303132333435363738')
    return result

def snapshot(root,include_root=False):
    result={}
    paths=([pathlib.Path(root)] if include_root else [])+sorted(pathlib.Path(root).rglob('*'))
    for path in paths:
        state=path.lstat(); assert not stat.S_ISLNK(state.st_mode) and not state.st_file_attributes&1024
        assert stat.S_ISDIR(state.st_mode) or stat.S_ISREG(state.st_mode)
        result[path.relative_to(root).as_posix()]={'kind':'directory' if path.is_dir() else 'file','mtime':state.st_mtime_ns,
            'size':state.st_size if stat.S_ISREG(state.st_mode) else 0,'attributes':state.st_file_attributes,'streams':streams(path)}
    return result

def metadata(samples):
    # snapshotにはrunのstdout等もあるため、左右rootとsubtreeだけを選ぶ。
    return {path:dict(kind=value['kind'],size=value['size'],mtime=value['mtime']//100+116444736000000000,attributes=value['attributes'])
            for path,value in samples.items() if path in ['left','right'] or path.startswith(('left/','right/'))}

def capture_metadata(rows):
    result={}
    for row in rows:
        path=row['Path'];assert path not in result
        assert path in ['left','right'] or path.startswith(('left/','right/')),'capture outside input roots'
        result[path]=dict(kind='directory' if row['Directory'] else 'file',size=row['Size'],mtime=row['Mtime'],attributes=row['Attributes'])
    assert 'left' in result and 'right' in result,'both root metadata required'
    return result

def check_metadata(before,after,case,success):
    left=lambda values:{p:v for p,v in values.items() if p=='left' or p.startswith('left/')}
    right=lambda values:{p:v for p,v in values.items() if p=='right' or p.startswith('right/')}
    assert left(before)==left(after),'source root/subtree metadata changed (same-size ADS changes restore metadata)'
    if not success:
        assert before==after,'rejection/cancel must preserve both root/subtree size/mtime/attributes'
        return
    rel=relative(case);source_file='left/'+rel;dest_file='right/'+rel
    src=before[source_file];dest=after[dest_file]
    assert dest['kind']=='file' and dest['size']==src['size'] and dest['mtime']==src['mtime'],'published main size/mtime must match source'
    assert dest['attributes']&PORTABLE_ATTRIBUTES==src['attributes']&PORTABLE_ATTRIBUTES,'published portable attributes must match source'
    # sole copied leaf以外のmembershipは固定。freshのみ新leafを許す。
    expected=set(right(before))|{dest_file};assert set(right(after))==expected,'destination metadata exact membership'
    mutable_parent=dest_file.rsplit('/',1)[0]
    for path,previous in right(before).items():
        if path==dest_file:continue
        actual=after[path];assert actual['kind']==previous['kind'] and actual['size']==previous['size'] and actual['attributes']==previous['attributes'],'untouched/directory metadata kind/size/attributes'
        if path!=mutable_parent:
            assert actual==previous,'nonmutated destination ancestor metadata changed'
        else:
            assert actual['mtime']==previous['mtime'] or actual['mtime']>previous['mtime'],'populated destination directory retained or natural mtime'
            source_directory='left'+path[len('right'):]
            assert actual['mtime']!=before[source_directory]['mtime'],'source directory time must not be forced onto populated destination'

def facts(payloads):
    return {name:dict(size=len(data),hex=data.hex().upper(),sha256=hashlib.sha256(data).hexdigest().upper()) for name,data in payloads.items()}

def disk(root,case,success=True):
    rel=relative(case); expected_source=source(case)
    expected_dest=dict(expected_source if success else DST)
    if case=='destination-sha-change':expected_dest[':old:$DATA']=bytes.fromhex('D0D1D2')
    assert streams(root/'left'/rel)==facts(expected_source),'source all names/bytes/SHA'
    assert streams(root/'right'/rel)==facts(expected_dest),'dest all names/bytes/SHA including old ADS removal'
    for side in ['left','right']:
        actual=snapshot(root/side,include_root=True)
        components=rel.split('/'); names={'/'.join(components[:i]) for i in range(1,len(components)+1)}
        assert set(actual)==names|{'.'},'exact root/subtree membership/no temp'
    if case=='directory':
        assert streams(root/'left'/'tree')==facts({':source-dir:$DATA':bytes.fromhex('41424344')}),'source directory stream unchanged'
        assert streams(root/'right'/'tree')==facts({':keep-dir:$DATA':bytes.fromhex('515253')}),'dest directory retained only'
    if case=='long-path':assert len(str(root/'left'/rel))>300
    return snapshot(root)

def prepare(root,case):
    assert case in IDS and not root.exists(),'new dedicated run root'
    for side in ['left','right']:
        path=root/side/relative(case);path.parent.mkdir(parents=True)
        if side=='right' and case=='fresh':continue
        for suffix,data in (source(case) if side=='left' else DST).items():
            with open(extended(path)+(suffix if suffix!='::$DATA' else ''),'wb') as f:f.write(data)
    if case=='directory':
        for side,suffix,data in [('left',':source-dir:$DATA',bytes.fromhex('41424344')),('right',':keep-dir:$DATA',bytes.fromhex('515253'))]:
            with open(extended(root/side/'tree')+suffix,'wb') as f:f.write(data)
    for path in sorted(root.rglob('*')):os.utime(extended(path),ns=(FIXED_NS,FIXED_NS))
    for path in [root/'right']+sorted((root/'right').rglob('*')):
        if path.is_dir():os.utime(extended(path),ns=(DEST_DIRECTORY_NS,DEST_DIRECTORY_NS))
    data=snapshot(root);(root/'before-streams.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    return dict(case=case,before=data,abiBytes=600,nameOffset=8)

def read(path):return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))

# 新caseだけの固定期待。既存6CLI/11GUIのIDSとliteralは維持する。
BUDGET_CASES={'descriptors14':(14,4000000,True),'descriptors13':(13,4000000,False),
              'names150':(200000,150,True),'names149':(200000,149,False)}
BUDGET_PATHS=['alpha.bin','beta.bin']

def verify_stream_budgets(work,reports):
    budget_reports=list((work/'gui').rglob('folder-stream-budget-observations.json'))
    assert len(budget_reports)==1 and budget_reports[0].parent==reports[0].parent
    rows=read(budget_reports[0]);assert [r['Id'] for r in rows]==list(BUDGET_CASES)
    utf16=lambda name:len(name.encode('utf-16-le'))//2
    # product JSONで期待を作らず、0byte ADSとサロゲートを含むliteralから照合。
    assert len(SRC)==4 and len(DST)==3 and SRC[':empty:$DATA']==b''
    assert utf16(':日本😀:$DATA')==11 and len(':日本😀:$DATA')==10
    assert (sum(map(len,SRC.values())),sum(map(len,DST.values())))==(24,12)
    assert 2*(len(SRC)+len(DST))==14
    assert 2*(sum(map(utf16,SRC))+sum(map(utf16,DST)))==150
    receipts=[]
    for row in rows:
        case=row['Id'];descriptors,names,success=BUDGET_CASES[case]
        root=pathlib.Path(row['Work']).resolve()
        assert root==(budget_reports[0].parent/'stream-budgets'/case).resolve()
        assert row['MaximumStreamDescriptors']==descriptors and row['MaximumStreamNameCharacters']==names
        assert row['Clicked'] and row['Confirmed']==success and row['ExpectedSuccess']==success
        assert row['PlanPresent']==success and row['ResultPresent']==success
        assert row['Succeeded']==success and row['Mutation']==success and row['Published']==(2 if success else 0)
        if success:
            assert tuple(row[k] for k in ['Descriptors','NameCharacters','Logical','Destination','Io','Read','Write'])==(14,150,48,24,360,312,48)
            assert row['Paths']==BUDGET_PATHS
        else:
            assert all(row[k] is None for k in ['Descriptors','NameCharacters','Logical','Destination','Io','Read','Write'])
            assert row['Paths']==[]
        for side in ['left','right']:
            sample=snapshot(root/side,include_root=True)
            assert set(sample)=={'.',*BUDGET_PATHS},'exact root/subtree/no temporary publication'
            assert sample['.']['streams']=={},'fixture root has no ADS'
            expected=SRC if side=='left' or success else DST
            for path in BUDGET_PATHS:
                assert sample[path]['streams']==facts(expected),'all native stream names/lengths/bytes/SHA including zero-byte ADS'
        after=snapshot(root)
        before_metadata=capture_metadata(row['BeforeMetadata']);after_metadata=capture_metadata(row['AfterMetadata'])
        assert before_metadata==capture_metadata(read(root/'before-metadata.json'))
        assert after_metadata==capture_metadata(read(root/'after-metadata.json'))==metadata(after)
        assert set(before_metadata)==set(after_metadata)=={'left','right',*(side+'/'+p for side in ['left','right'] for p in BUDGET_PATHS)}
        for p in ['left',*('left/'+p for p in BUDGET_PATHS)]:
            assert before_metadata[p]==after_metadata[p],'entire source metadata unchanged'
        for side in ['left','right']:
            assert before_metadata[side]['kind']=='directory' and before_metadata[side]['size']==0
            assert before_metadata[side]['mtime']==(FIXED_NS if side=='left' else DEST_DIRECTORY_NS)//100+116444736000000000
            for p in BUDGET_PATHS:
                b=before_metadata[side+'/'+p]
                assert b['kind']=='file' and b['size']==(5 if side=='left' else 7)
                assert b['mtime']==FIXED_NS//100+116444736000000000
        if success:
            for p in BUDGET_PATHS:
                src=before_metadata['left/'+p];dst=after_metadata['right/'+p]
                assert (dst['kind'],dst['size'],dst['mtime'])==(src['kind'],src['size'],src['mtime'])
                assert dst['attributes']&PORTABLE_ATTRIBUTES==src['attributes']&PORTABLE_ATTRIBUTES
            b=before_metadata['right'];a=after_metadata['right']
            assert (a['kind'],a['size'],a['attributes'])==(b['kind'],b['size'],b['attributes'])
            assert a['mtime']>=b['mtime'] and a['mtime']!=before_metadata['left']['mtime']
        else:
            assert before_metadata==after_metadata,'refused plan preserves both root/subtree metadata'
        assert len(list((work/'gui').rglob('folder-stream-budget-'+case+'.png')))==1
        (root/'after-streams.json').write_text(json.dumps(after,ensure_ascii=False,indent=2),encoding='utf-8')
        receipts.append(dict(case=case,accepted=True,expectedDescriptors=14,expectedUtf16NameUnits=150,allBytesAndSha=True))
    return receipts

def verify(work):
    records=read(work/'windows-stream-commands.json');assert [r['id'] for r in records]==IDS
    results=[]
    for record in records:
        case=record['id'];root=pathlib.Path(record['work']).resolve();assert root==(work/'windows-streams'/case).resolve()
        command=record['command'];assert command['ExitCode']==0 and command['Stderr']=='' and command['Pid']>0 and command['CreationUtc'] and command['ExitObservedUtc']
        args=command['Arguments'];assert args==['--folder-sync',str(root/'left'),str(root/'right'),'--direction','left-to-right','--copy','all','--select','tree' if case=='directory' else relative(case)]
        before=read(root/'before-streams.json');after=disk(root,case)
        assert {p:v for p,v in before.items() if p=='left' or p.startswith('left/')}=={p:v for p,v in after.items() if p=='left' or p.startswith('left/')},'source root/subtree bytes/metadata unchanged'
        check_metadata(metadata(before),metadata(after),case,True)
        S=sum(map(len,source(case).values()));D=0 if case=='fresh' else 12
        result=json.loads(command['Stdout']);assert result['succeeded'] and not result['cancelled'] and result['mutationOccurred']
        assert (result['logicalBytes'],result['destinationBytes'],result['plannedIoBytes'],result['readBytes'],result['writeBytes'])==(S,D,6*S+3*D,5*S+3*D,S)
        assert [e['path'] for e in result['entries']]==[relative(case)]
        assert all(e['published'] and e['status']=='Published' for e in result['entries']) and result['published']==1
        (root/'after-streams.json').write_text(json.dumps(after,ensure_ascii=False,indent=2),encoding='utf-8')
        results.append(dict(case=case,logical=S,destination=D,io=6*S+3*D,read=5*S+3*D,write=S))
    gui_process=read(work/'gui-process.json');assert gui_process['ExitCode']==0 and gui_process['Pid']>0 and gui_process['Stderr']==''
    reports=list((work/'gui').rglob('folder-stream-observations.json'));assert len(reports)==1
    gui=read(reports[0]);assert [r['Id'] for r in gui]==GUI_IDS
    ui=read(work/'gui'/'ui-report.json');assert ui['assertions'] and all(a['passed'] for a in ui['assertions'])
    for row in gui:
        case=row['Id'];root=pathlib.Path(row['Work']).resolve();assert root==(reports[0].parent/'streams'/case).resolve()
        success=case not in ['io179','source-sha-change','destination-sha-change','cancel']
        assert row['Clicked'] and row['Confirmed']==(case!='io179') and row['ExpectedSuccess']==success
        assert (row['Succeeded'] is True)==success and row['Mutation']==success
        assert row['Published']==(1 if success else 0)
        if success:
            S=sum(map(len,source(case).values()));D=0 if case=='fresh' else 12
            assert (row['Logical'],row['Read'],row['Write'])==(S,5*S+3*D,S)
            assert row['Paths']==[relative(case)]
        after=disk(root,case,success)
        before_metadata=capture_metadata(row['BeforeMetadata']);after_metadata=capture_metadata(row['AfterMetadata'])
        assert before_metadata==capture_metadata(read(root/'before-metadata.json')),'GUI saved before metadata differs'
        assert after_metadata==capture_metadata(read(root/'after-metadata.json'))==metadata(after),'GUI ordinary System.IO capture must match independent lstat actual metadata'
        check_metadata(before_metadata,after_metadata,case,success)
        source_file='left/'+relative(case);dest_file='right/'+relative(case)
        assert after[source_file]['mtime']//100+116444736000000000==row['SourceMtime'],'source restored mtime'
        if not success:assert after[dest_file]['mtime']//100+116444736000000000==row['DestinationMtime'],'destination restored mtime'
        assert len(list((work/'gui').rglob('folder-stream-'+case+'.png')))==1,'GUI PNG missing'
    budgets=verify_stream_budgets(work,reports)
    result=dict(accepted=True,abiBytes=600,nameOffset=8,cli=results,guiCases=len(gui),streamBudgetGui=budgets,allBytesAndSha=True)
    (work/'verify-streams-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');return result

if __name__=='__main__':
    assert os.name=='nt','Windows only, no fabricated acceptance'
    if sys.argv[1]=='prepare':result=prepare(pathlib.Path(sys.argv[2]).resolve(),sys.argv[3])
    elif sys.argv[1]=='verify':
        work=pathlib.Path(sys.argv[2]).resolve()
        try:result=verify(work)
        except BaseException as error:
            result=dict(accepted=False,failure=type(error).__name__+': '+str(error))
            (work/'verify-streams-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
            print(json.dumps(result,ensure_ascii=False));raise
    else:raise ValueError(sys.argv[1])
    print(json.dumps(result,ensure_ascii=False))
