"""固定literal期待とstdlibで、別process CLIモデル/計画/全入力を照合する。製品DTO不使用。"""
import datetime, hashlib, json, pathlib, sys, traceback
fixture=pathlib.Path(sys.argv[1]).resolve();work=pathlib.Path(sys.argv[2]).resolve()
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda b:hashlib.sha256(b).hexdigest().upper()
expect_bytes=(fixture/'model-expectations.json').read_bytes()
provenance=load(fixture/'provenance.json')
assert sha(expect_bytes)==provenance['expectationsSha256']
assert sha(pathlib.Path(__file__).read_bytes())==provenance['readerSha256']
assert (work/'expectations.json').read_bytes()==expect_bytes
expected=json.loads(expect_bytes);inputs=work/'inputs';records=load(work/'commands.json')
before=load(work/'source-before.json');after=load(work/'source-after.json')
assert before==after
by_path={r['Path']:r for r in before};assert len(by_path)==len(before)
literal={'.':None,'controls':None}
for name,text in expected['controls'].items():literal['controls/'+name]=text.encode('utf-8')
for group,definition in expected['groups'].items():
 literal[group]=None
 for side in ('left','right'):
  prefix=group+'/'+side;literal[prefix]=None
  for name in definition[side+'Dirs']:literal[prefix+'/'+name]=None
  for name,value in definition[side+'Files'].items():literal[prefix+'/'+name]=bytes.fromhex(value)
assert set(literal)==set(by_path),'Exact source entry set including empty directories'
actual={'.'}
for p in inputs.rglob('*'):
 assert not p.is_symlink();actual.add(p.relative_to(inputs).as_posix())
assert actual==set(literal),'Actual input entry set'
fixed=datetime.datetime.fromisoformat(expected['fixedMtimeUtc'].replace('Z','+00:00'))
ns=int(fixed.timestamp())*1_000_000_000;filetime=(ns//100)+116444736000000000
files=0;total=0;empty=[]
for relative,data in literal.items():
 p=inputs if relative=='.' else inputs/relative;row=by_path[relative];stat=p.stat()
 assert row['LastWriteFileTime']==filetime and stat.st_mtime_ns==ns,relative
 if hasattr(stat,'st_file_attributes'):assert stat.st_file_attributes==row['Attributes'],relative
 if data is None:
  assert p.is_dir() and row['Kind']=='directory' and row['Length']==0 and row['Sha256'] is None and row['Hex'] is None
  if not list(p.iterdir()):empty.append(relative)
 else:
  assert p.is_file() and p.read_bytes()==data and row['Hex']==data.hex().upper() and row['Sha256']==sha(data) and row['Length']==len(data),relative
  files+=1;total+=len(data)
assert len(records)==len(expected['cases']) and {r['id'] for r in records}=={c['id'] for c in expected['cases']}
record_by_id={r['id']:r for r in records};failures=[];runs=[];skipped=[]

def side_info(group,side,path):
 d=expected['groups'][group]
 if path in d[side+'Files']:return 'File',len(bytes.fromhex(d[side+'Files'][path]))
 if path in d[side+'Dirs']:return 'Directory',0
 return None
def verify_side(observed,group,side,path,filtered=False,scan=None):
 info=side_info(group,side,path)
 if info is None:assert observed is None;return
 kind,length=info;assert observed is not None
 assert pathlib.Path(observed['path']).resolve()==(inputs/group/side/path).resolve()
 assert observed['kind']==kind and observed['size']==length and observed['filtered']==filtered
 observed_time=datetime.datetime.fromisoformat(observed['lastWriteTimeUtc'].replace('Z','+00:00'));assert observed_time==fixed
 assert observed['scan']==(scan if kind=='Directory' else 'NotApplicable')

for case in expected['cases']:
 record=record_by_id[case['id']]
 if record['skipped']:
  assert sys.platform!='win32' and case.get('platform')=='windows' and record['reason'];skipped.append(case['id']);continue
 try:
  command=record['command'];assert command['ExitCode']==case['exit']
  assert isinstance(command['Pid'],int) and command['Pid']>0 and command['CreationUtc'] and command['LaunchUtc'] and command['ExitObservedUtc']
  for suffix,key in [('stdout','Stdout'),('stderr','Stderr')]:assert (work/(case['id']+'.'+suffix+'.txt')).read_bytes()==command[key].encode('utf-8')
  if case.get('reject'):
   assert command['Stdout']=='' and command['Stderr'].strip();runs.append(dict(id=case['id'],exit=2,rejected=True));continue
  doc=json.loads(command['Stdout']);group=case['group']
  if 'model' in case:
   model=expected['models'][case['model']];states=model['statuses'];left_filter=set(model.get('filteredLeft',[]));right_filter=set(model.get('filteredRight',[]))
   def filtered_node(path):return (side_info(group,'left',path) is None or path in left_filter) and (side_info(group,'right',path) is None or path in right_filter)
   visible={p for p in states if not model.get('hideFiltered') or not filtered_node(p)}
   entries={r['path']:r for r in doc['entries']};assert len(entries)==len(doc['entries']) and set(entries)==visible
   assert doc['allEntryCount']==len(states)
   for path,row in entries.items():
    assert row['status']==states[path] and row['filtered']==filtered_node(path),(path,row['status'],states[path])
    representative=(side_info(group,'right',path) if path in left_filter and path not in right_filter and side_info(group,'right',path) else side_info(group,'left',path) or side_info(group,'right',path))
    assert row['kind']==representative[0]
    children=sorted(p for p in states if p.rsplit('/',1)[0]==path and '/' in p);assert row['children']==children
    for side,filtered in [('left',left_filter),('right',right_filter)]:
     scan='Unscanned' if model.get('nonrecursive') or path in filtered else 'Scanned'
     verify_side(row[side],group,side,path,path in filtered,scan)
    if states[path]=='Error':assert row['error']
   runs.append(dict(id=case['id'],exit=command['ExitCode'],modelEntries=len(entries),allEntryCount=doc['allEntryCount']))
  else:
   source_side='left' if case['direction']=='left-to-right' else 'right';dest_side='right' if source_side=='left' else 'left'
   assert pathlib.Path(doc['sourceRoot']).resolve()==(inputs/group/source_side).resolve()
   assert pathlib.Path(doc['destinationRoot']).resolve()==(inputs/group/dest_side).resolve()
   assert doc['direction']==('LeftToRight' if source_side=='left' else 'RightToLeft') and doc['copy']==('All' if case['copy']=='all' else 'DifferencesOnly')
   assert doc['selected']==case['selected']
   paths=[c['path'] for c in doc['candidates']];golden=sorted(case['candidates'],key=lambda p:(p.count('/'),p))
   assert paths==golden,(paths,golden)
   for candidate in doc['candidates']:
    path=candidate['path'];kind=side_info(group,source_side,path)[0]
    assert candidate['physicalDirectory']==(kind=='Directory')
    args=case['args'];filtered=('--exclude' in args and path=='filtered-dir') or ('{typeFilter}' in args and source_side=='left' and path in ('conflict','reverse-conflict')) or ('{sizeFilter}' in args and source_side=='right' and path=='size-side.bin')
    scan='Unscanned' if '--no-recursive' in args or filtered else 'Scanned'
    verify_side(candidate['source'],group,source_side,path,filtered,scan)
    dest_filtered=('--exclude' in args and path=='filtered-dir') or ('{typeFilter}' in args and dest_side=='left' and path in ('conflict','reverse-conflict')) or ('{sizeFilter}' in args and dest_side=='right' and path=='size-side.bin')
    verify_side(candidate['destination'],group,dest_side,path,dest_filtered,'Unscanned' if '--no-recursive' in args or dest_filtered else 'Scanned')
   runs.append(dict(id=case['id'],exit=0,candidates=paths,physicalDirectory=[c['path'] for c in doc['candidates'] if c['physicalDirectory']]))
 except Exception:
  failures.append(dict(id=case['id'],detail=traceback.format_exc()))
result=dict(accepted=not failures,cases=len(expected['cases']),verified=len(runs),skipped=skipped,failures=failures,inputEntries=len(literal),inputFiles=files,inputBytes=total,emptyDirectories=empty,sourceUnchanged=True,fullBytes=True,mtimeAndAttributesRetained=True,runs=runs,scope='Actual separate app process model/plan stdout and unchanged inputs; self-authored literal expectations/static original evidence; no Copy/GUI/partial mutation/mtime output save/copy stamp/CLI cancellation/full E2E/UI/AOT proof')
(work/'verify-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False));sys.exit(0 if not failures else 2)
