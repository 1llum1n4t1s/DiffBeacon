import argparse,hashlib,json,struct,zlib,math
from pathlib import Path
F=('showDifferences','zoom','blockSize','highlightAlpha','threshold','insertionDeletionMode')
FIXED=dict(zip(F,(True,1,8,.7,0,0)))
GLOBAL=dict(zip(F,(False,2,3,.25,11.125,2)))
def sha(b):return hashlib.sha256(b).hexdigest().upper()
def six(d):return {k:d.get(k,FIXED[k]) for k in F}
def png(path):
 b=path.read_bytes(); assert b[:8]==b'\x89PNG\r\n\x1a\n'; p=8; chunks=[]; data=[]; end=False
 while p<len(b):
  n=struct.unpack_from('>I',b,p)[0]; typ=b[p+4:p+8]; raw=b[p+8:p+8+n]; crc=struct.unpack_from('>I',b,p+8+n)[0]
  assert n<=32*1024*1024 and zlib.crc32(typ+raw)&0xffffffff==crc
  chunks.append(typ)
  if typ==b'IHDR':w,h,bits,color,comp,filt,inter=struct.unpack('>IIBBBBB',raw); assert 0<w<=4096 and 0<h<=4096 and bits==8 and comp==filt==inter==0
  if typ==b'PLTE':palette=raw
  if typ==b'tRNS':trans=raw
  if typ==b'IDAT':data.append(raw)
  p+=n+12
  if typ==b'IEND':end=True;break
 assert end and p==len(b) and chunks[0]==b'IHDR' and chunks[-1]==b'IEND'
 bpp={0:1,2:3,3:1,4:2,6:4}[color];stride=w*bpp;limit=(stride+1)*h
 dec=zlib.decompressobj(); raw=dec.decompress(b''.join(data),limit+1)
 assert len(raw)==limit and dec.eof and not dec.unused_data and not dec.unconsumed_tail
 prev=bytes(stride); pixels=bytearray();pos=0
 for y in range(h):
  ft=raw[pos];pos+=1;row=bytearray(raw[pos:pos+stride]);pos+=stride;assert ft in range(5)
  for i in range(stride):
   a=row[i-bpp] if i>=bpp else 0;c=prev[i-bpp] if i>=bpp else 0;b=prev[i]
   if ft==1:pred=a
   elif ft==2:pred=b
   elif ft==3:pred=(a+b)//2
   elif ft==4:
    q=a+b-c; pa,pb,pc=abs(q-a),abs(q-b),abs(q-c);pred=a if pa<=pb and pa<=pc else b if pb<=pc else c
   else:pred=0
   row[i]=(row[i]+pred)&255
  for i in range(0,stride,bpp):
   if color==6:pixels.extend(row[i:i+4])
   elif color==2:pixels.extend(row[i:i+3]+b'\xff')
   elif color==0:pixels.extend(bytes((row[i],row[i],row[i],255)))
   elif color==4:pixels.extend(bytes((row[i],row[i],row[i],row[i+1])))
   else:
    k=row[i];pixels.extend(palette[k*3:k*3+3]+bytes((trans[k] if 'trans' in locals() and k<len(trans) else 255,)))
  prev=row
 assert len(pixels)==w*h*4
 return w,h,bytes(pixels)
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--folder',type=Path);ap.add_argument('--gui',type=Path);ap.add_argument('--receipt',type=Path,required=True);ap.add_argument('--cli-stdout',type=Path);a=ap.parse_args()
 if bool(a.folder)==bool(a.gui):raise ValueError('exactly one gui/folder input required')
 if a.gui:
  gui=a.gui.resolve(strict=True);report=json.loads((gui/'ui-report.json').read_bytes());fixtures=Path(report['fixtures']).resolve(strict=True)
  assert fixtures.is_relative_to((gui/'fixtures').resolve(strict=True)) and fixtures!=(gui/'fixtures').resolve(strict=True)
  assert report['scope'] in ('all','image-defaults-only')
  folder=fixtures/'image-application-defaults'
 else:folder=a.folder.resolve(strict=True)
 rows=[json.loads(x) for x in (folder/'observations.ndjson').read_text(encoding='utf-8-sig').splitlines() if x.strip()]
 names=[r['name'] for r in rows];assert len(names)==len(set(names));by={r['name']:r for r in rows};checks=[];pngs=[]
 def test(n,v,detail=''):checks.append({'name':n,'passed':bool(v),'detail':detail})
 def local(name):
  assert isinstance(name,str) and Path(name).name==name and '/' not in name and '\\' not in name and name not in ('.','..')
  p=folder/name;assert p.is_file() and not p.is_symlink();return p
 def expected(name,store=None,display=None):
  r=by[name]
  if store is not None:test(name+'/store',six(r['store'])==store,repr(six(r['store'])))
  if display is not None:test(name+'/display',six(r['display']['imageSettings'])==display,repr(six(r['display']['imageSettings'])))
 for i,r in enumerate(rows,1):
  test(r['name']+'/sequence',r['sequence']==i)
  if 'settingsFile' in r:
   b=local(r['settingsFile']).read_bytes();test(r['name']+'/settings-sha',sha(b)==r['settingsSHA256']);d=json.loads(b);test(r['name']+'/settings-current',d==r['store']);test(r['name']+'/bounded',len(b)<=4096)
   p=local(r['png']);test(r['name']+'/png-sha',sha(p.read_bytes())==r['pngSHA256']);w,h,pixels=png(p);pngs.append({'name':r['name'],'width':w,'height':h,'rgbaSHA256':sha(pixels),'pngSHA256':sha(p.read_bytes())})
   if 'controls' in r:
    controls={c['name']:c for c in r['controls']};test(r['name']+'/six-controls',len(controls)==6)
    for c in controls.values():test(r['name']+'/'+c['name']+'/bounds',all(math.isfinite(c[x]) for x in ('x','y','width','height')) and c['width']>0 and c['height']>0)
  else:test(r['name']+'/raw-sha',sha(local(r['rawFile']).read_bytes())==r['rawSHA256'])
 # Fixed expectations are reader-owned constants, independent of producer flags/expected fields.
 g=GLOBAL.copy();aa=GLOBAL.copy();bb=GLOBAL.copy();expected('new-defaults',g,aa);expected('peer-before',g,bb)
 g['threshold']=aa['threshold']=19.375;expected('threshold-change',g,aa);expected('peer-after-threshold',g,bb)
 g['blockSize']=bb['blockSize']=5;expected('peer-block-merge',g,bb);expected('first-after-peer',g,aa)
 g['zoom']=aa['zoom']=2.5;expected('idle-zoom',g,aa)
 g['showDifferences']=aa['showDifferences']=True;expected('show-change',g,aa)
 g['highlightAlpha']=aa['highlightAlpha']=.6;expected('alpha-change',g,aa)
 g['insertionDeletionMode']=aa['insertionDeletionMode']=1;expected('mode-change',g,aa);expected('peer-after-six',g,bb)
 aa['threshold']=23.5;expected('save-failed',g,aa)
 test('save-failed/bytes-preserved',by['save-failed']['settingsSHA256']==by['mode-change']['settingsSHA256'])
 g['threshold']=23.5;expected('explicit-retry',g,aa);expected('cancel-before',g,aa)
 expected('cancel-pending',g,aa);expected('cancel-after',g,aa)
 test('cancel/bytes-preserved',by['cancel-after']['settingsSHA256']==by['explicit-retry']['settingsSHA256'])
 expected('stale-pending',g,aa)
 g['threshold']=aa['threshold']=31.5;expected('stale-newest-adopted',g,aa);expected('stale-after',g,aa)
 for n in ('candidate-before-reject','rejected-candidate','decode-failed'):expected(n,g,aa)
 for n in ('rejected-candidate','decode-failed'):test(n+'/bytes-preserved',by[n]['settingsSHA256']==by['stale-after']['settingsSHA256'])
 test('decode-failed/actual-error',bool(by['decode-failed']['lastCompareError']))
 for n in ('save-inflight-before','save-inflight-zoom'):test(n+'/actual-saving',by[n]['saving'])
 expected('save-inflight-before',g,bb);bb['zoom']=3
 for n in ('save-inflight-zoom','save-complete'):expected(n,g,bb)
 g['zoom']=3;expected('save-zoom-explicit-retry',g,bb)
 aa['threshold']=43.5;expected('peer-output-guard',g,aa)
 g['threshold']=43.5;expected('peer-guard-explicit-retry',g,aa);expected('minimum-layout',g,bb);expected('reload-before',g,bb)
 g['threshold']=37.5;expected('reload-six-only',g,bb)
 before=by['reload-before'];after=by['reload-six-only']
 for field in ('notifications','clockReads','displayCandidatesStarted','displayCandidatesAdopted','revision','generation'):test('reload-six-only/no-'+field,after[field]==before[field],str((before[field],after[field])))
 # Compare actual captured pixels/opaque identities and committed model stamps, not producer success flags.
 for r in rows:
  if 'inputFiles' in r:
   for f in r['inputFiles']:
    raw=local(f['bytesFile']).read_bytes();test(r['name']+'/input/'+f['name'],len(raw)==f['size'] and sha(raw)==f['sha256'] and raw==local(f['name']).read_bytes())
  for group in ('originalFrames','renderedFrames','bitmaps'):
   for frame in r.get(group,[]):
    raw=local(frame['bgraFile']).read_bytes();test(r['name']+'/'+group+'/full-pixels',len(raw)==frame['width']*frame['height']*4 and sha(raw)==frame['bgraSHA256'])
  if 'originalFrames' in r:
   test(r['name']+'/original-independent-pixels',len(r['originalFrames'])==2 and all(local(f['bgraFile']).read_bytes()==bytes((shade,shade,shade,255))*768 for f,shade in zip(r['originalFrames'],(0,200))))
 def committed_same(beforeName,afterName):
  x,y=by[beforeName],by[afterName]
  positive=lambda v:isinstance(v,int) and not isinstance(v,bool) and v>0
  for row in (x,y):
   for group in ('renderedFrames','bitmaps'):
    frames=row.get(group);test(row['name']+'/'+group+'/critical-exact-two',isinstance(frames,list) and len(frames)==2)
    if not isinstance(frames,list) or len(frames)!=2:raise ValueError(row['name']+'/'+group+'/critical frame evidence absent')
    test(row['name']+'/'+group+'/critical-positive-identities',all(positive(frame.get('identity')) for frame in frames))
   test(row['name']+'/critical-positive-committed-identity',positive(row.get('adoptedDisplayIdentity')))
  for f in ('generation','revision','adoptedDisplayIdentity','historyIndex','historyCount','dirty','modified'):
   test(afterName+'/committed-preserved/'+f,x[f]==y[f])
  for group in ('renderedFrames','bitmaps'):
   def stamp(r):return [(f.get('name'),f['width'],f['height'],f['bgraSHA256'],f.get('identity') if group=='bitmaps' else None) for f in r[group]]
   test(afterName+'/'+group+'/all-bytes-identity-preserved',stamp(x)==stamp(y))
  test(afterName+'/input-all-bytes-preserved',x['inputFiles']==y['inputFiles'])
  if 'originalFrames' in x and 'originalFrames' in y:test(afterName+'/original-all-bytes-preserved',[f['bgraSHA256'] for f in x['originalFrames']]==[f['bgraSHA256'] for f in y['originalFrames']])
 committed_same('cancel-before','cancel-pending');committed_same('cancel-before','cancel-after')
 test('cancel/actual-new-request',by['cancel-pending']['requestTaskIdentity']!=by['cancel-before']['requestTaskIdentity'])
 test('cancel/committed-generation-not-request-counter',by['cancel-after']['generation']==by['cancel-before']['generation'] and by['cancel-after']['requestTaskIdentity']==by['cancel-pending']['requestTaskIdentity'])
 committed_same('stale-newest-adopted','stale-after')
 test('stale/newest-generation-adopted',by['stale-newest-adopted']['generation']>by['stale-pending']['generation'])
 committed_same('candidate-before-reject','rejected-candidate');committed_same('rejected-candidate','decode-failed')
 reached={'ImageZoom','ImageBlockSize','ImageThreshold','ImageHighlightAlpha','ImageShowDifferences','ImageInsertionDeletionMode'}
 namesReached={r.get('reachedControl') for r in rows if r.get('reachedControl')};test('minimum/exact-six-reached',namesReached==reached)
 for name in reached:
  r=by['minimum-reach-'+name];minimumStore=g.copy();minimumStore['threshold']=43.5;expected(r['name'],minimumStore,bb);c=next(c for c in r['controls'] if c['name']==name);v=r['viewport'];w,h,pixels=png(local(r['png']))
  test(name+'/minimum-PNG',(w,h)==(850,550));test(name+'/viewport-clipped-to-window',v['x']>=0 and v['y']>=0 and v['x']+v['width']<=w+.01 and v['y']+v['height']<=h+.01)
  test(name+'/actually-reached-full-bounds',c['width']>0 and c['height']>0 and c['x']>=v['x']-.01 and c['y']>=v['y']-.01 and c['x']+c['width']<=v['x']+v['width']+.01 and c['y']+c['height']<=v['y']+v['height']+.01)
 for version in range(1,7):
  expected('project-v%d-omitted'%version,g,FIXED)
  explicit=dict(zip(F,(True,1.5,7,.9,9.25,1)));expected('project-v%d-explicit'%version,g,explicit)
 for name in ('project-pending-before','project-pending-candidate','project-pending-cancelled'):
  expected(name,g,explicit);test(name+'/capture-adopted-settings',six(by[name]['project']['imageSettings'])==explicit)
 committed_same('project-pending-before','project-pending-candidate');committed_same('project-pending-before','project-pending-cancelled')
 test('project-pending-cancelled/actual-cancellation','OperationCanceledException' in (by['project-pending-cancelled']['lastCompareError'] or ''))
 restored=dict(zip(F,(False,2.25,11,.6,4.25,2)));expected('project-pending-retry',g,restored)
 test('project-pending-retry/capture-new-adopted-settings',six(by['project-pending-retry']['project']['imageSettings'])==restored)
 test('project-pending-retry/new-display-identity',by['project-pending-retry']['adoptedDisplayIdentity']!=by['project-pending-before']['adoptedDisplayIdentity'])
 for name in ('project-stale-before','project-stale-candidate','project-stale-rejected'):
  expected(name,g,restored);test(name+'/capture-adopted-settings',six(by[name]['project']['imageSettings'])==restored)
 committed_same('project-stale-before','project-stale-candidate');committed_same('project-stale-before','project-stale-rejected')
 test('project-stale-rejected/actual-stale-cancellation','OperationCanceledException' in (by['project-stale-rejected']['lastCompareError'] or ''))
 newest=dict(zip(F,(True,1.75,9,.8,6.25,1)));expected('project-stale-retry',g,newest)
 test('project-stale-retry/capture-new-adopted-settings',six(by['project-stale-retry']['project']['imageSettings'])==newest)
 test('project-stale-retry/new-display-identity',by['project-stale-retry']['adoptedDisplayIdentity']!=by['project-stale-before']['adoptedDisplayIdentity'])
 test('capture-plain-project/fixed',six(by['capture-plain-project']['project']['imageSettings'])==FIXED)
 expected('legacy-options',FIXED);test('legacy-options/reload-success',by['legacy-options']['result'])
 for n in ('unknown-options','oversize-options','invalid-zoom','invalid-threshold','invalid-blockSize','invalid-highlightAlpha','invalid-insertionDeletionMode','invalid-set-nan','invalid-set-infinity'):
  expected(n,GLOBAL);test(n+'/reject',not by[n]['result']);test(n+'/diagnostic',bool(by[n]['diagnostic']))
 expected('roundtrip',GLOBAL);test('roundtrip/exact-six-json',six(json.loads(local(by['roundtrip']['rawFile']).read_bytes()))==GLOBAL)
 for n,shade in (('left.png',0),('right.png',200)):
  w,h,pixels=png(local(n));test(n+'/independent-input-pixels',(w,h,pixels)==(32,24,bytes((shade,shade,shade,255))*768))
 if a.cli_stdout:
  cli=json.loads(a.cli_stdout.read_bytes());test('cli/fixed-threshold',cli['threshold']==0);test('cli/fixed-alpha',cli['highlightAlpha']==.7);test('cli/fixed-mode',cli.get('insertionDeletionMode',0)==0)
 result={'scope':'independent raw image-defaults reader; compile and actual process qualification require separate receipts','observations':len(rows),'inputSource':'gui-report binding' if a.gui else 'explicit folder','checks':checks,'pngFullDecode':pngs,'failed':sum(not x['passed'] for x in checks),'passed':sum(x['passed'] for x in checks),'qualified':False}
 with a.receipt.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2)
 print(json.dumps({'observations':len(rows),'inputSource':'gui-report binding' if a.gui else 'explicit folder','passed':result['passed'],'failed':result['failed'],'png':len(pngs)}));return int(result['failed']>0)
if __name__=='__main__':raise SystemExit(main())
