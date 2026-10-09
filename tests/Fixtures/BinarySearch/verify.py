import argparse, hashlib, io, json, math, os, pathlib, struct, sys, time, zipfile, zlib
P=pathlib.Path
MAX=16777216
PREFIXES=['two/0','two/2','three/0','three/1','three/2']
SUFFIXES=['no-word','selection-add','selection-clear','find','next','previous','no-match','utf16','ascii-case','big-endian','oem-token','invalid-correct','readonly','dirty-search','undo','redo','save','page']+['stale-'+x for x in ['cancel','selection','page','readonly','owner-input','observer-throw','observer-reentry']]
CORE=['core/overlap/'+str(b)+'/'+str(c) for b in [False,True] for c in range(5)]+['core/ascii','core/nonascii','core/empty','core/no-pattern','core/caret-negative','core/canceled','core/over-input','core/over-pattern']
IDS={p+'/'+s for p in PREFIXES for s in SUFFIXES}|set(CORE)|{'empty/'+str(s) for s in range(3)}|{'archive/'+str(s)+'/readonly' for s in range(3)}|{'large/cache16MiB','large/previous16MiB','large/selection4682','large/dialog-reject4682','large/dialog-accept4681-cancel'}
assert len(IDS)==154
FIXED=bytearray([0xcc])*8194;FIXED[:8]=b'ABABABaA';FIXED[8:14]=bytes([0,0x12,0x34,0x34,0x12,0xff]);FIXED[4094:4098]=bytes.fromhex('DEADBEEF');FIXED[8190:8194]=bytes.fromhex('DEADBEEF');FIXED=bytes(FIXED)
def sha(b):return hashlib.sha256(b).hexdigest().upper()
def require(v,msg):
 if not v:raise ValueError(msg)
def nearest(b,p,c,back,match):
 if not match:
  table=bytes(x+32 if 65<=x<=90 else x for x in range(256));b=b.translate(table);p=p.translate(table)
 last=len(b)-len(p)
 if back:
  start=min(c-1,last)
  return b.rfind(p,0,start+len(p)) if start>=0 else -1
 start=c+1
 return b.find(p,start) if start<=last else -1
def png(p,dims):
 b=p.read_bytes(); require(b[:8]==b'\x89PNG\r\n\x1a\n','PNG signature'); pos=8; ids=[]; wh=None; ended=False
 while pos<len(b):
  require(pos+12<=len(b),'PNG truncated chunk'); n=struct.unpack('>I',b[pos:pos+4])[0]; t=b[pos+4:pos+8]; d=b[pos+8:pos+8+n]; require(pos+12+n<=len(b),'PNG truncation')
  require(zlib.crc32(t+d)&0xffffffff==struct.unpack('>I',b[pos+8+n:pos+12+n])[0],'PNG CRC'); pos+=12+n
  if t==b'IHDR': wh=struct.unpack('>IIBBBBB',d); require(wh[:2]==dims and wh[2]==8 and wh[3] in (2,6) and wh[4:]==(0,0,0),'PNG dimensions/format')
  elif t==b'IDAT': ids.append(d)
  elif t==b'IEND': require(n==0 and pos==len(b),'PNG IEND/trailing'); ended=True; break
 require(ended and wh and ids,'PNG required chunks'); w,h,_,ct,*_=wh; ch=4 if ct==6 else 3; stride=w*ch
 dec=zlib.decompressobj(); raw=dec.decompress(b''.join(ids), (stride+1)*h+1)+dec.flush(); require(dec.eof and not dec.unused_data and len(raw)==(stride+1)*h,'PNG complete pixels')
 pixels=bytearray(); prev=bytearray(stride)
 for y in range(h):
  f=raw[y*(stride+1)]; row=bytearray(raw[y*(stride+1)+1:(y+1)*(stride+1)]); require(f<=4,'PNG filter')
  for x in range(stride):
   a=row[x-ch] if x>=ch else 0; up=prev[x]; c=prev[x-ch] if x>=ch else 0
   if f==1: q=a
   elif f==2: q=up
   elif f==3: q=(a+up)//2
   elif f==4:
    pr=a+up-c; ds=[abs(pr-a),abs(pr-up),abs(pr-c)]; q=(a,up,c)[ds.index(min(ds))]
   else: q=0
   row[x]=(row[x]+q)&255
  pixels.extend(row); prev=row
 return {'sha256':sha(b),'pixelSha256':sha(pixels),'width':w,'height':h,'decodedBytes':len(pixels)}


class Reader:
 def __init__(self,root):self.root=root.resolve();self.cache={};self.count=0
 def blob(self,j):
  require(type(j) is dict and set(j)=={'path','length','sha256'},'full bytes ref required');p=P(j['path']).resolve(strict=True);require(p.is_relative_to(self.root) and p.is_file(),'bytes path outside run');require(type(j['length']) is int and 0<=j['length']<=MAX+1,'bytes ref bound')
  if str(p) not in self.cache:self.cache[str(p)]=p.read_bytes()
  b=self.cache[str(p)];require(len(b)==j['length'] and sha(b)==j['sha256'],'full byte SHA/size');return b
 def side(self,state,side):
  rows=state['sides'];require(len({x['side'] for x in rows})==len(rows),'unique sides');return next(x for x in rows if x['side']==side)
 def gate(self,s):
  b=self.blob(s['bytes']);sel=s['selected'] and 0<=s['anchor']<len(b) and 0<=s['caret']<len(b);prev=s['previous'];cache=s['cache']
  prior=False
  if prev['present']:
   if prev['fromSelection']:prior=cache is not None and 0<len(self.blob(cache))<=MAX
   else:prior=type(prev['text']) is str and 0<len(prev['text'].encode('utf-16-le'))//2<=32768
  require(s['findEnabled'] is bool(b),'Find gate actual length');require(s['nextEnabled'] is (bool(b) and (sel or prior)) and s['previousEnabled'] is (bool(b) and (sel or prior)),'NextPrev independent endpoints/accepted real cache')
 def state(self,state):
  require(state['sessionToken']>0 and state['panelToken']>0,'actual identity');require(len(state['sides']) in (2,3),'actual pair/three')
  for s in state['sides']:self.gate(s);require(type(s['revision']) is int and s['revision']>=0,'actual revision')
 def unchanged(self,b,a,selection=False):
  require(b['sessionToken']==a['sessionToken'] and b['panelToken']==a['panelToken'],'identity retained');require(b['canUndo']==a['canUndo'] and b['canRedo']==a['canRedo'],'history retained')
  for x in b['sides']:
   y=self.side(a,x['side'])
   for k in ('revision','dirty','readOnly','sessionReadOnly'):require(x[k]==y[k],'search retained '+k)
   require(self.blob(x['bytes'])==self.blob(y['bytes']),'search all bytes retained')
   if selection:
    for k in ('anchor','caret','selected','oem','previous','cache'):require(x[k]==y[k],'retained endpoint/previous '+k)
 def decode(self,o,before):
  target=self.side(before,o['side']);text=o['text']
  if o['fromSelection']:
   if target['selected']:
    source=self.blob(target['bytes']);start=min(target['anchor'],target['caret']);n=abs(target['anchor']-target['caret'])+1;return source[start:start+n]
   require(target['previous']['present'] and target['previous']['fromSelection'] and target['cache'] is not None,'real prior raw cache');return self.blob(target['cache'])
  if o['utf16']:return text.encode('utf-16-le',errors='strict')
  out=bytearray();at=0
  while at<len(text):
   if text[at]=='<' and text.find('>',at)>at:
    end=text.index('>',at);tok=text[at+1:end];kind,value=tok.split(':');require(kind in ('bh','wh','lh'),'fixed token oracle unsupported');width={'bh':1,'wh':2,'lh':4}[kind];out+=int(value,16).to_bytes(width,'big' if o['bigEndian'] else 'little');at=end+1
   else:require(ord(text[at])<128,'OEM nonASCII needs independent OEM reference, cannot silently assume');out.append(ord(text[at]));at+=1
  return bytes(out)
 def core(self,c):
  b=self.blob(c['input']);p=self.blob(c['pattern']);expected_error=None
  if c['id']=='core/canceled':expected_error='System.OperationCanceledException';require(c['cancellationRequested'] is True,'fixed canceled request')
  elif len(b)>MAX or len(p)>MAX:expected_error='System.IO.InvalidDataException'
  elif c['caret']<0 or c['caret']>len(b):expected_error='System.ArgumentOutOfRangeException'
  elif not p:expected_error='System.ArgumentException'
  if c['id'].startswith('core/overlap/'):
   require(b==b'AAAA' and p==b'AA' and c['caret']==int(c['id'].split('/')[-1]) and c['backwards'] is (c['id'].split('/')[-2]=='True') and c['matchCase'] is True,'fixed overlap literals')
  elif c['id']=='core/ascii': require(b==bytes([0x61,0x41,0xc0,0xe0,0x61]) and p==b'A' and c['caret']==1 and c['matchCase'] is False,'fixed ASCII literal')
  elif c['id']=='core/nonascii': require(b==bytes([0xc0,0xe0]) and p==bytes([0xc0]) and c['matchCase'] is False,'fixed nonASCII literal')
  elif c['id']=='core/over-input': require(len(b)==MAX+1 and not any(b) and p==bytes([0]),'shared oversized input')
  elif c['id']=='core/over-pattern': require(b==bytes([0]) and len(p)==MAX+1 and not any(p),'shared oversized pattern')
  require(c['exceptionType']==expected_error,'independent core refusal')
  if expected_error:require(c['found'] is None,'refusal cannot return match')
  else:require(c['found']==nearest(b,p,c['caret'],c['backwards'],c['matchCase']),'independent nearest overlap/no-wrap core result')
 def case(self,c):
  if c['kind']=='core':self.core(c);return
  b=c['before'];a=c['after'];self.state(b);self.state(a);side=c['side'];x=self.side(b,side);y=self.side(a,side);o=c['search'];events=c['uiEvents'];require(c['limitMilliseconds']==30000 and 0<=c['elapsedMilliseconds']<=30000,'shared case deadline')
  for v in b['sides']:
   data=self.blob(v['bytes']); require((data==FIXED or data==b'Z'+FIXED[1:] or not data or (len(data)==MAX and not any(data))),'fixed/shared UI bytes cannot be arbitrary fixture')
  if o is not None:
   require(self.blob(o['input'])==self.blob(x['bytes']) and o['caret']==x['caret'],'actual observed input/caret')
   pattern=self.blob(o['pattern']); require(pattern==self.decode(o,b),'actual decoded bytes independent of producer')
   require(o['found']==nearest(self.blob(o['input']),pattern,o['caret'],o['backwards'],o['matchCase']),'actual worker result independently recalculated')
  if c['kind']=='stale':
   require(c['hookReached'] is True and o is not None and c['returnAdopted'] is False and c['searchTaskStatus']=='RanToCompletion','actual stale hook/task rejected adoption')
   expected=c['eventState'] or b;self.unchanged(expected,a,True); require(a['modalCount']==0,'canceled/rejected dialog closed')
   if c['id'].endswith('stale-cancel'): require(any(e.get('content')=='取消' for e in events),'actual cancel pointer')
   if c['id'].endswith('stale-selection'): require(self.side(expected,side)['caret']==1 and self.side(expected,side)['selected'] is False,'actual later selection')
   if c['id'].endswith('stale-page'): require(expected['pageStart']==4096 and self.side(expected,side)['caret']==4096,'actual later page')
   if c['id'].endswith('stale-readonly'): require(self.side(expected,side)['readOnly'] is True,'actual later readonly')
   if c['id'].endswith('observer-throw'):require(c['observationFailure']=='System.IO.IOException','observer throw observed safely')
   else:require(c['observationFailure'] is None,'unexpected observer error')
   return
  require(c['observationFailure'] is None,'unexpected observation failure')
  if c['kind']=='search':
   require(o is not None and c['searchTaskToken']>0 and c['searchTaskStatus']=='RanToCompletion' and c['returnAdopted'] is True,'actual completed adopted search')
   input_=self.blob(o['input']);pattern=self.blob(o['pattern']);require(input_==self.blob(x['bytes']),'actual search snapshot bytes');require(o['caret']==x['caret'] and pattern==self.decode(o,b) and 0<len(pattern)<=MAX,'decoded actual pattern/caret')
   f=nearest(input_,pattern,o['caret'],o['backwards'],o['matchCase']);require(o['found']==f,'independent nearest overlap/no-wrap UI');self.unchanged(b,a)
   if f>=0:
    require(y['selected'] is True and y['caret']==f and y['anchor']==f+len(pattern)-1,'full found selection/caret');page=b['pageStart'] if b['pageStart']<=f<b['pageStart']+4096 else f//4096*4096;require(a['pageStart']==page,'found page reveal')
   else:
    for k in ('anchor','caret','selected'):require(x[k]==y[k],'no-match endpoint retained')
    require(a['pageStart']==b['pageStart'],'no-match page retained')
   require(y['previous']['present'] and y['previous']['fromSelection']==o['fromSelection'],'only completed request adopted')
   if o['fromSelection']:require(y['cache'] is not None and self.blob(y['cache'])==pattern,'full real selected cache')
   else:require(y['cache'] is None and y['previous']['text']==o['text'] and y['previous']['utf16']==o['utf16'] and y['previous']['bigEndian']==o['bigEndian'],'previous decoded request flags')
   if c['id'].endswith('/readonly'):require(x['readOnly'] and x['sessionReadOnly'] and y['readOnly'],'readonly searching permitted')
   if c['id'].endswith('/dirty-search'):require(x['dirty'] and self.blob(x['bytes'])[0]==90 and y['dirty'],'real dirty bytes maintained')
  elif c['kind'] in ('no-word','empty','dialog-cap','dialog-cancel'):
   require(o is None,'no unexpected worker');self.unchanged(b,a,True);require(a['modalCount']==0,'no modal remains')
   if c['kind']=='no-word':require(not x['nextEnabled'] and not x['previousEnabled'] and x['findEnabled'],'first no-word eligibility')
   if c['kind']=='empty':require(not self.blob(x['bytes']) and not x['findEnabled'],'empty Find disabled')
   if c['kind']=='dialog-cap':require(x['selected'] and abs(x['anchor']-x['caret'])+1==4682,'fixed selected capacity refusal')
   if c['kind']=='dialog-cancel':require(any(e.get('phase')=='opened-boundary' and len(e['textCodeUnits'])==32767 for e in events),'exact 4681*7+NUL boundary modal')
  elif c['kind']=='selection':
   self.unchanged(b,a);require(x['previous']==y['previous'] and x['cache']==y['cache'],'selection cannot make previous word');require(y['selected'] is c['id'].endswith('selection-add'),'actual selection notification')
  elif c['kind']=='history':
   v=self.blob(y['bytes']);require(v[0]==(65 if c['id'].endswith('/undo') else 90),'real UndoRedo bytes');require(y['dirty'] is c['id'].endswith('/redo'),'actual saved-point dirty');require(a['canRedo'] is c['id'].endswith('/undo'),'actual redo availability')
  elif c['kind']=='save':
   require(c['savedBytes'] is not None and P(c['savedPath']).resolve().is_relative_to(self.root),'saved file actual run path');saved=P(c['savedPath']).read_bytes();require(saved==self.blob(c['savedBytes'])==self.blob(y['bytes']) and y['dirty'] is False,'actual saved bytes and savepoint')
  else:raise ValueError('unknown case kind')
  if c['id'].endswith('/find'):
   require(any(e.get('phase')=='opened' and e['matchCase'] is False for e in events),'actual first default false')
  if c['id'].endswith('/invalid-correct'):
   es={e['phase']:e for e in events if e.get('event')=='dialog'}
   for k in ('invalid-result','invalid-surrogate-result'):require(es[k]['visible'] and es[k]['acceptEnabled'] and es[k]['textEnabled'] and es[k]['status'],'actual invalid dialog remains correctable');self.unchanged(es['opened']['state'],es[k]['state'],True)
   require(es['invalid-input']['textCodeUnits']==[] and es['invalid-surrogate-input']['textCodeUnits']==[0xd800],'exact invalid dialog UTF16 codeunits')
  if c['kind']=='search':require(any(e.get('event') in ('pointer','key') for e in events),'actual pointer/key route evidence')
def main():
 p=argparse.ArgumentParser();p.add_argument('--gui');p.add_argument('--receipt',required=True);a=p.parse_args();start=time.time();fail=[];proof={}
 def test(n,f):
  try:proof[n]=f()
  except Exception as e:fail.append(n+': '+str(e))
 if a.gui:
  gui=P(a.gui).resolve();reader=None;j=None
  def locate():
   nonlocal reader,j
   report=gui/'ui-report.json';b=report.read_bytes();rep=json.loads(b);fixture=P(rep['fixtures']).resolve(strict=True);require(fixture.is_relative_to((gui/'fixtures').resolve(strict=True)) and fixture!=(gui/'fixtures').resolve(),'raw report fixture binding');root=fixture/'binary-search';reader=Reader(root);j=json.loads((root/'assertions.json').read_bytes());require(j['schemaVersion']==1 and j['failureType'] is None,'producer completed raw');ids=[x['id'] for x in j['cases']];require(len(ids)==len(set(ids)) and set(ids)==IDS,'fixed complete154 cases, no unknown/duplicates');require(rep['scope'] in ('all','binary-search-only') and all(x['passed'] is True for x in rep['assertions']),'final aggregate complete/no failed assertion');return {'uiReportSha256':sha(b),'rawSha256':sha((root/'assertions.json').read_bytes()),'fixtures':str(fixture),'caseCount':len(ids)}
  test('root/fixed coverage',locate)
  if reader is not None and j is not None:
   for c in j['cases']:test(c['id'],lambda c=c:reader.case(c) or {'independentlyChecked':True})
   def end():
    end=j['ownedEnd'];require(end['expectedPanes']==6 and end['disposedCallbacks']==6 and end['visible'] is False and end['ownedWindows']==0,'all owned panes closed');require(end['clipboardReads']==end['clipboardWrites']==0,'no OS clipboard calls');tokens=[t['token'] for t in end['tasks']];require(tokens and len(tokens)==len(set(tokens)),'actual tasks unique');require(all(t['completed'] is True and t['status']=='RanToCompletion' and t['canceled'] is False and t['faulted'] is False for t in end['tasks']),'actual finite UI task terminal');return {'taskCount':len(tokens)}
   test('owned/clipboard',end)
   def archive():
    b=reader.blob(j['archiveRoot']);path=P(j['archiveRootPath']).resolve(strict=True);require(path.is_relative_to(reader.root) and path.read_bytes()==b,'original archive bytes preserved');z=zipfile.ZipFile(io.BytesIO(b));require(z.namelist()==['leaf.bin'] and z.testzip() is None and z.read('leaf.bin')==FIXED,'all original ZIP entry/CRC/bytes');return {'sha256':sha(b)}
   test('archive readonly root',archive)
   def images():
    names={'binary-search-'+p.replace('/','-')+'.png' for p in PREFIXES}; require({p.name for p in gui.glob('binary-search-*.png')}==names,'exact five actual pane screenshots'); return {n:png(gui/n,(1280,850)) for n in names}
   test('all PNG CRC/full pixels',images)
 else:fail.append('runtime GUI input absent; no App/runtime executed')
 receipt={'readerPid':os.getpid(),'readerSha256':sha(P(__file__).read_bytes()),'startedUnix':start,'endedUnix':time.time(),'artifactConsistencyPassed':not fail,'qualified':False,'osTerminalQualified':False,'scope':'fixed data/controls/search consistency; OS pointer/clipboard/native dialogs unverified','failures':fail,'proof':proof,'exitCode':2 if fail else 0};target=P(a.receipt);require(not target.exists(),'fresh receipt required');target.write_text(json.dumps(receipt,ensure_ascii=False,indent=2),encoding='utf8');print(json.dumps({'readerPid':os.getpid(),'artifactConsistencyPassed':not fail,'failureCount':len(fail),'receipt':str(target)},ensure_ascii=False));return receipt['exitCode']
if __name__=='__main__':sys.exit(main())
