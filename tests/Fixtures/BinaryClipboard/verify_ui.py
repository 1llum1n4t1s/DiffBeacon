"""製品DTO/codecを使わず入力、全保存byte、相対asset、ZIP全entry/CRCを確認する。"""
import ctypes, hashlib, io, json, sys, zipfile
from pathlib import Path
root=Path(sys.argv[1]); paths=list(root.glob('fixtures/*/binary-clipboard/facts.json')); assert len(paths)==1,paths
folder=paths[0].parent; facts=json.loads(paths[0].read_bytes())
original=[bytes(range(side*10,side*10+8)) for side in range(3)]
edited=[bytes([side*10,65,0,side*10+3,65,0])+bytes(range(side*10+4,side*10+8)) for side in range(3)]
def sha(data): return hashlib.sha256(data).hexdigest().upper()
for side,name in enumerate(['left','middle','right']):
 assert (folder/(name+'.bin')).read_bytes()==original[side]
 data=(folder/(name+'.zip')).read_bytes(); assert sha(data)==facts['rootSha256'][side]
 with zipfile.ZipFile(io.BytesIO(data)) as archive:
  assert archive.testzip() is None and archive.namelist()==['leaf.bin'] and archive.read('leaf.bin')==original[side]
for side in range(3): assert (folder/f'three-{side}.bin').read_bytes()==edited[side]
for side in (0,2): assert (folder/f'two-{side}.bin').read_bytes()==edited[side]
assert (folder/'history64-redo.bin').read_bytes()==b'\xaa'*(16*1024*1024)
assert (folder/'unclosed-repeat.bin').read_bytes()==b'<bh:'*262144
processing=json.loads((folder/'processing-boundary.json').read_bytes()); assert processing['inputBytes']==1048576 and processing['maximumTokenLookahead']==54 and processing['passes']==2 and processing['longNumericPrefixRejected']
budget=json.loads((folder/'maximum-format-budget.json').read_bytes()); assert budget['rawBytes']==16*1024*1024+8 and budget['textCharacters']==112*1024*1024 and budget['totalLimit']==464*1024*1024+332 and budget['platformOrders']==2 and budget['windowsOrders']==4 and budget['oemPresentAndAbsent'] and not budget['osClipboardUsed']
if sys.platform=='win32':
 native=ctypes.WinDLL('user32'); native.CharToOemBuffA.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_uint]
 source=ctypes.create_string_buffer(b'\xe9\0'); target=ctypes.create_string_buffer(1)
 assert native.CharToOemBuffA(source,target,1)!=0
 literal=target.raw
else: literal='é'.encode('cp437')
assert (folder/'oem-ui.bin').read_bytes()==literal+original[1][2:]
def validate(entry, read, archive):
 for side,(pathkey,inputkey) in enumerate(zip(['leftPath','basePath','rightPath'],['leftArchiveInput','baseArchiveInput','rightArchiveInput'])):
  if archive:
   item=entry[inputkey]; container=read(item['rootPath']); assert sha(container)==facts['rootSha256'][side]
   with zipfile.ZipFile(io.BytesIO(container)) as originalzip:
    assert originalzip.testzip() is None and originalzip.namelist()==['leaf.bin'] and originalzip.read('leaf.bin')==original[side]
   saves=item['workingTexts']; assert len(saves)==1 and saves[0]['kind']=='Binary'
   saved=saves[0]; payload=read(saved['snapshotPath']); assert payload==edited[side] and sha(payload)==saved['sha256']
  else: assert read(entry[pathkey])==edited[side]
def workspace(path, archive):
 path=Path(path); doc=json.loads(path.read_bytes()); entries=doc['entries'] if 'entries' in doc else [doc]; assert len(entries)==1 and entries[0]['mode']=='Binary'
 validate(entries[0], lambda p:(path.parent/Path(p)).read_bytes(),archive)
def package(path, archive):
 with zipfile.ZipFile(path) as z:
  assert z.testzip() is None
  # 全entryを復号し、CRCを独立readerで検査する。
  for name in z.namelist(): z.read(name)
  doc=json.loads(z.read('project.json')); assert len(doc['entries'])==1
  validate(doc['entries'][0],z.read,archive)
for name in ['normal','archive']:
 workspace(facts[name+'Workspace'],name=='archive'); package(facts[name+'Package'],name=='archive')
 if len(sys.argv)>2:
  work=Path(sys.argv[2]); workspace(work/(name+'-copied.json'),name=='archive'); workspace(work/(name+'-reloaded.json'),name=='archive'); package(work/(name+'-cli.zip'),name=='archive')
if len(sys.argv)>2:
 work=Path(sys.argv[2]); assert (work/'unclosed-repeat.txt').read_bytes()==(work/'unclosed-repeat.bin').read_bytes()==b'<bh:'*262144
 assert (work/'long-numeric.txt').read_bytes()==b'<bh:'+b'1'*1048576 and (work/'long-numeric.bin').read_bytes()==b'KEEP'
layout=json.loads((folder/'layout.json').read_bytes()); assert layout['tabs']>=45 and layout['hexHeight']>=100 and layout['asciiHeight']>=40
assert -1<=layout['hexY']<=layout['viewportHeight']-24 and -1<=layout['asciiY']<=layout['viewportHeight']-24 and -1<=layout['saveY']<layout['viewportHeight']
report=json.loads((root/'ui-report.json').read_bytes()); assert report['assertions'] and all(x['passed'] for x in report['assertions'])
print(json.dumps(dict(success=True,fullBytes=True,allZipEntryCrc=True,originalInputsRetained=True,**{k:v for k,v in facts.items() if k!='rootSha256'})))
