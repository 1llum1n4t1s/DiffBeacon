"""無改変原本＋namespace clock facade の採取。期待値計算は別実装。"""
import argparse,base64,copy,gzip,hashlib,io,json,os,pathlib,subprocess
ROOT=pathlib.Path(__file__).resolve().parent
SOURCE_SHA='7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28'
IMAGE_SHA='173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609'
CL='C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe'
LITERALS=[[(1,2,3,255),(254,127,0,128),(3,9,11,0),(9,101,201,255),(15,17,19,128),(63,65,67,0)],[(2,3,4,0),(1,128,255,255),(11,13,17,128),(110,12,14,0),(21,23,25,255),(71,73,75,128)],[(7,8,9,128),(250,251,252,0),(19,23,29,255),(31,37,41,128),(43,47,53,0),(79,83,89,255)]]
def sha(raw):return hashlib.sha256(raw).hexdigest().upper()
def save(p,v):p.write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
def cases():
 result=[]
 def add(name,n,epochs,mode='anim',alpha=.3,show=False,blink=False,P=1000,B=800,highlight=.7,selected=-1,wipe=0,pos=0,repeat=None):
  ims=[{'width':2,'height':3,'offsetX':0,'offsetY':0,'bgraBytes':[v for pixel in LITERALS[i] for v in pixel]} for i in range(n)]
  result.append({'name':name,'mode':mode,'overlayAlpha':alpha,'showDifferences':show,'blinkDifferences':blink,'animationPeriod':P,'blinkPeriod':B,'highlightAlpha':highlight,'selectedDiffIndex':selected,'wipeMode':wipe,'wipePosition':pos,'blockSize':1,'threshold':0,'images':ims,'states':[{'epochs':values} for values in (repeat or [epochs])]})
 for n in (2,3):
  reads=2 if n==2 else 4
  for t in (0,1,100,199,200,201,499,500,501,600,699,700,701,999,1000):add(f'{n}-anim-boundary-{t}',n,[t]*reads)
  for t in (0,399,400,401,799,800):add(f'{n}-anim-blink-{t}',n,[100]*reads+[t],show=True,blink=True)
  for mode in ('none','xor','alpha'):add(f'{n}-{mode}-blink-on',n,[400],mode=mode,show=True,blink=True)
  add(f'{n}-show-false-no-blink-read',n,[600]*reads,blink=True)
  for alpha in (.1,.9):add(f'{n}-anim-stored-alpha-{alpha}',n,[100]*reads,alpha=alpha)
  add(f'{n}-final-blend-blink-cross',n,[199]*(reads-1)+[399,400],show=True,blink=True,selected=0)
  for hi in (0,.7,1):add(f'{n}-highlight-{hi}-wipe',n,[100]*reads+[400],show=True,blink=True,highlight=hi,selected=0 if hi!=.7 else -1,wipe=1 if hi!=1 else 2,pos=1)
  sequence=[[0]*reads,[200]*reads,[0]*reads,[200]*reads]
  add(f'{n}-refresh-no-accumulation',n,sequence[0],repeat=sequence)
 for t in (0,400,401,1003,1004,1005,1405,1406,2008,2009):add(f'3-period-2009-{t}',3,[t]*4,P=2009)
 add('3-middle-two-trunc-skew',3,[200,100,60,500])
 add('3-middle-boundary-skew',3,[100,199,200,100])
 add('3-clock-order-skew',3,[201,500,499,700])
 return result
def encode(inputs):
 words=[str(len(inputs))]
 for c in inputs:
  words.extend(map(str,[len(c['images']),{'none':0,'xor':1,'alpha':2,'anim':3}[c['mode']],c['overlayAlpha'],int(c['showDifferences']),c['highlightAlpha'],c['selectedDiffIndex'],c['wipeMode'],c['wipePosition'],c['animationPeriod'],c['blinkPeriod'],int(c['blinkDifferences']),len(c['states'])]))
  for im in c['images']:words.extend(map(str,[im['width'],im['height'],im['offsetX'],im['offsetY'],*im['bgraBytes']]))
  for state in c['states']:words.extend(map(str,[len(state['epochs']),*state['epochs']]))
 return (' '.join(words)+'\n').encode('ascii')
def reject_boundary(exe,out):
 base=cases()[0];tests=[]
 def add(name,edit,error):
  case=copy.deepcopy(base);edit(case);tests.append((name,encode([case]),error))
 for field in ['animationPeriod','blinkPeriod']:
  for value in [0,-1,199,8001]:add(field+'-'+str(value),lambda c,f=field,v=value:c.update({f:v}),'header')
 add('negative-epoch',lambda c:c['states'][0].update(epochs=[-1,0]),'negative epoch')
 add('queue-short',lambda c:c['states'][0].update(epochs=[0]),'clock queue exhausted')
 add('queue-surplus',lambda c:c['states'][0].update(epochs=[0,0,1]),'clock queue surplus')
 add('show-false-blink-surplus',lambda c:(c.update(blinkDifferences=True),c['states'][0].update(epochs=[0,0,400])),'clock queue surplus')
 add('pane-one',lambda c:c.update(images=c['images'][:1]),'header')
 add('pane-four',lambda c:c.update(images=c['images']*2),'header')
 add('byte256',lambda c:c['images'][0]['bgraBytes'].__setitem__(0,256),'byte')
 add('dimension-zero',lambda c:c['images'][0].update(width=0),'canvas dimension')
 tests.append(('truncated-input',b' '.join(encode([base]).split()[:-1])+b'\n','input'))
 tests.append(('trailing-input',encode([base])+b'bad\n','trailing input'))
 results=[]
 for name,raw,error in tests:
  run=subprocess.run([str(exe)],input=raw,capture_output=True);(out/(name+'.input.txt')).write_bytes(raw);(out/(name+'.stderr.log')).write_bytes(run.stderr)
  results.append({'name':name,'exitCode':run.returncode,'stderr':run.stderr.decode('utf8'),'inputSha256':sha(raw),'passed':run.returncode==2 and error in run.stderr.decode('utf8')})
 save(out/'adapter-rejections.json',{'checks':len(results),'failed':sum(not r['passed'] for r in results),'results':results})
 if any(not r['passed'] for r in results):raise ValueError('adapter rejection boundary')
def main():
 parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);parser.add_argument('--minimal',action='store_true');args=parser.parse_args();out=pathlib.Path(args.output).resolve();out.mkdir(parents=True,exist_ok=True)
 source_path=ROOT.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp';image_path=ROOT.parent/'ImageRegions/reference-source/image.hpp';source=source_path.read_bytes();image=image_path.read_bytes();assert sha(source)==SOURCE_SHA and sha(image)==IMAGE_SHA
 specs=[('original-types.inc',source,[(33,161)]),('original-modes.inc',source,[(479,487)]),('original-color.inc',image,[(384,395)]),('original-functions.inc',source,[(1166,1218),(1663,1700),(1702,1964),(1966,2053),(2055,2125)])];extraction=[]
 for name,data,ranges in specs:
  lines=data.splitlines(keepends=True);raw=b''.join(b''.join(lines[a-1:b]) for a,b in ranges);(out/name).write_bytes(raw);extraction.append({'file':name,'source':'image.hpp' if data is image else 'ImgDiffBuffer.hpp','lineRanges':ranges,'sha256':sha(raw),'unaltered':True})
 save(out/'extraction.json',extraction);inputs=cases()[:1] if args.minimal else cases();save(out/'probe-inputs.json',inputs);raw=encode(inputs);(out/'probe-input.txt').write_bytes(raw)
 exe=out/'temporal-overlay-probe.exe';cmd=[CL,'/nologo','/std:c++17','/EHsc','/Y-','/utf-8','/W3','/Od','/Fe'+str(exe),'/Fo'+str(out)+os.sep,'/I'+str(out),str(ROOT/'probe-adapter.cpp')]
 build=subprocess.run(cmd,capture_output=True);(out/'compiler.log').write_bytes(build.stdout+build.stderr)
 if build.returncode:raise RuntimeError('compile failed: '+str(out/'compiler.log'))
 run=subprocess.run([str(exe)],input=raw,capture_output=True);(out/'probe-output.jsonl').write_bytes(run.stdout);(out/'probe-stderr.log').write_bytes(run.stderr)
 if run.returncode:raise RuntimeError('probe failed: '+run.stderr.decode())
 reject_boundary(exe,out)
 results=[json.loads(line) for line in run.stdout.splitlines()];assert len(results)==len(inputs);total=0
 for c,r in zip(inputs,results):
  for field in ('classificationBefore','classificationAfter'):r[field+'Sha256']=sha(json.dumps(r[field],sort_keys=True,separators=(',',':')).encode('ascii'))
  frames=[*r['rawBefore'],*r['rawAfter'],*r['baseCanvas']]
  for state in r['states']:frames.extend(state['processed']);total+=sum(len(im['bytes']) for im in state['processed'])
  for im in frames:data=bytes(im['bytes']);im['bgraBase64']=base64.b64encode(data).decode('ascii');im['sha256']=sha(data)
  c['expected']=r
 golden={'schemaVersion':1,'sourceVersion':'WinIMerge v1.0.54','sourceRevision':'da639cdfaeca87aaad0eaceec509afa11ad61421','sourceSha256':SOURCE_SHA,'imageSourceSha256':IMAGE_SHA,'sourceExcerpts':extraction,'license':'GPL-2.0-or-later; ../ImageRegions/LICENSE.txt','adapterBoundary':'preprocessed literal BGRA; standard-clock dependency mock in adapter namespace; no GUI timer/OS time proof; pixel-neutral transparency-cache stub','cases':inputs}
 payload=(json.dumps(golden,ensure_ascii=False,indent=2)+'\n').encode('utf8');buffer=io.BytesIO()
 with gzip.GzipFile(filename='',mode='wb',fileobj=buffer,mtime=0,compresslevel=9) as f:f.write(payload)
 packed=buffer.getvalue();(out/'golden.json.gz').write_bytes(packed)
 assert sha(source_path.read_bytes())==SOURCE_SHA and sha(image_path.read_bytes())==IMAGE_SHA
 meta={'cases':len(inputs),'states':sum(len(c['states']) for c in inputs),'fullBgraBytes':total,'payloadSha256':sha(payload),'gzipSha256':sha(packed),'gzipBytes':len(packed),'sourceSha256':SOURCE_SHA,'imageSourceSha256':IMAGE_SHA,'adapterSha256':sha((ROOT/'probe-adapter.cpp').read_bytes()),'buildExit':build.returncode,'probeExit':run.returncode,'inputSha256':sha(raw),'outputSha256':sha(run.stdout),'sourceUnchanged':True,'compiler':CL,'command':cmd};save(out/'metadata.json',meta);print(json.dumps({k:v for k,v in meta.items() if k!='command'}))
if __name__=='__main__':main()
