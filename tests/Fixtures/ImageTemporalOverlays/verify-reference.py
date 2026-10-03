"""原本ループを使わない piecewise alpha／各画素閉式の独立照合。"""
import argparse,base64,gzip,hashlib,json,pathlib
def sha(raw):return hashlib.sha256(raw).hexdigest().upper()
def animated(epoch,P):
 t=epoch%P;a=2*P//10;b=5*P//10;c=7*P//10
 return t/a if t<a else 1.0 if t<b else (a-(t-b))/a if t<c else 0.0
def expected(case,state,mutant=None):
 n=len(case['images']);mode=case['mode'];epochs=list(state['epochs']);reads=2 if n==2 else 4
 if mode=='anim':
  alphas=[animated(t,case['animationPeriod']) for t in epochs[:reads]]
  if mutant=='single-clock':alphas=[alphas[0]]*reads
  if mutant=='stored-amplitude':alphas=[a*case['overlayAlpha'] for a in alphas]
 else:alphas=[case['overlayAlpha']]*reads
 def mix(dst,src,a,channel,trunc=True):
  if mode=='none':return dst
  if mode=='xor':return dst if channel==3 else dst^src
  value=dst*(1-a)+src*a
  return int(value) if trunc else value
 frames=[im['bgraBytes'] for im in case['images']];w=2;h=3
 outputs=[[] for _ in frames]
 for pixel in range(w*h):
  for k in range(4):
   p=[frame[pixel*4+k] for frame in frames]
   outputs[0].append(mix(p[0],p[1],alphas[0],k))
   mid=mix(p[1],p[0],alphas[1],k,trunc=mutant!='skip-middle-trunc' or n==2)
   outputs[1].append(mid if n==2 else mix(mid,p[2],alphas[2],k))
   if n==3:outputs[2].append(mix(p[2],p[1],alphas[3],k))
 highlighted=[list(v) for v in outputs];mark=case['showDifferences']
 if mark and case['blinkDifferences']:
  blinkEpoch=epochs[reads if mode=='anim' else 0]
  mark=blinkEpoch%case['blinkPeriod']>=case['blinkPeriod']//2
 if mark:
  classified=case['expected']['classificationBefore'];regions={r['id']:r for r in classified['regions']}
  for pane in range(n):
   for y in range(h):
    for x in range(w):
     region=classified['regionIds'][y][x]
     if not region:continue
     op=regions[region]['op']
     if (pane==0 and op==3) or (pane==2 and op==1):continue
     color=(64,64,255) if region-1==case['selectedDiffIndex'] else (64,255,255)
     i=(y*w+x)*4;pixel=outputs[pane][i:i+4];alpha=case['highlightAlpha']
     highlighted[pane][i:i+4]=[int(pixel[k]*(1-alpha)+color[k]*alpha) for k in range(3)]+[pixel[3]] if pixel[3] else [*color,int(255*alpha)]
 final=[list(v) for v in highlighted];pos=case['wipePosition'];old=2147483647
 if case['wipeMode']:
  pos=max(0,min(h if case['wipeMode']==1 else w,pos));old=pos;mapping=(1,0) if n==2 else (1,2,0)
  for y in range(h):
   for x in range(w):
    if (y if case['wipeMode']==1 else x)<pos:continue
    i=(y*w+x)*4
    for pane in range(n):final[pane][i:i+4]=highlighted[mapping[pane]][i:i+4]
 return final,pos,old
def main():
 p=argparse.ArgumentParser();p.add_argument('first');p.add_argument('second');p.add_argument('--output',required=True);a=p.parse_args();out=pathlib.Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True)
 packed=pathlib.Path(a.first).read_bytes();packed2=pathlib.Path(a.second).read_bytes();payload=gzip.decompress(packed);payload2=gzip.decompress(packed2);g=json.loads(payload);checks=[];full=0;states=0;mutants={k:[] for k in ['single-clock','stored-amplitude','skip-middle-trunc']}
 def check(name,value):checks.append({'name':name,'passed':bool(value)})
 check('two-gzip-bytes',packed==packed2);check('two-payload-bytes',payload==payload2);check('gzip-mtime0-no-filename',packed[3]==0 and packed[4:8]==bytes(4))
 for c in g['cases']:
  r=c['expected'];name=c['name'];n=len(c['images'])
  check(name+'/classification-kept',r['classificationBefore']==r['classificationAfter']);check(name+'/raw-kept',r['rawBefore']==r['rawAfter']);check(name+'/states',len(c['states'])==len(r['states']))
  for key in ['classificationBefore','classificationAfter']:check(name+'/'+key+'-sha',sha(json.dumps(r[key],sort_keys=True,separators=(',',':')).encode('ascii'))==r[key+'Sha256'])
  for field in ['rawBefore','rawAfter','baseCanvas']:
   for index,frame in enumerate(r[field]):
    raw=base64.b64decode(frame['bgraBase64'],validate=True);check(name+'/'+field+str(index),list(raw)==frame['bytes']==c['images'][index]['bgraBytes'] and sha(raw)==frame['sha256'] and (frame['width'],frame['height'])==(2,3))
  for index,(s,observed) in enumerate(zip(c['states'],r['states'])):
   states+=1;want,pos,old=expected(c,s);readCount=(2 if n==2 else 4) if c['mode']=='anim' else 0;readCount+=int(c['showDifferences'] and c['blinkDifferences'])
   check(name+f'/{index}/clock-order-and-count',observed['clockReads']==s['epochs'] and len(s['epochs'])==readCount)
   check(name+f'/{index}/wipe-position',observed['position']==pos and observed['oldPosition']==old);check(name+f'/{index}/cache',observed['cacheCalls']==index+2)
   for pane,frame in enumerate(observed['processed']):
    raw=base64.b64decode(frame['bgraBase64'],validate=True);full+=len(raw);check(name+f'/{index}/{pane}/fullBGRA',list(raw)==want[pane]==frame['bytes'] and sha(raw)==frame['sha256'] and (frame['width'],frame['height'])==(2,3))
   for mutant in mutants:
    if expected(c,s,mutant)[0]!=want:mutants[mutant].append(name+'/'+str(index))
 for mutant,detected in mutants.items():check('reject-mutant/'+mutant,bool(detected))
 for n in (2,3):
  a1=next(c for c in g['cases'] if c['name']==f'{n}-anim-stored-alpha-0.1');a2=next(c for c in g['cases'] if c['name']==f'{n}-anim-stored-alpha-0.9')
  check(f'{n}/ANIM-independent-of-stored-alpha',a1['expected']['states']==a2['expected']['states'])
 root=pathlib.Path(__file__).resolve().parent
 for sourceName,key in [('ImgDiffBuffer.hpp','sourceSha256'),('image.hpp','imageSourceSha256')]:
  source=(root.parent/'ImageRegions/reference-source'/sourceName).read_bytes();check(sourceName+'/canonical-sha',sha(source)==g[key]);lines=source.splitlines(keepends=True)
  for item in g['sourceExcerpts']:
   if item['source']==sourceName:check(item['file']+'/unchanged-excerpt',sha(b''.join(b''.join(lines[a-1:b]) for a,b in item['lineRanges']))==item['sha256'])
 result={'cases':len(g['cases']),'states':states,'fullBgraBytes':full,'checks':len(checks),'failed':sum(not c['passed'] for c in checks),'mutantsDetected':mutants,'twoRunsPayloadEqual':payload==payload2,'twoRunsGzipEqual':packed==packed2,'gzipSha256':sha(packed),'payloadSha256':sha(payload),'gzipBytes':len(packed),'basis':'independent piecewise alpha; per-pixel closed expressions; intermediate byte truncation; original observed classification mask; absolute cyclic wipe','assertions':checks};(out/'independent-proof.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf8');print(json.dumps({k:v for k,v in result.items() if k not in ['assertions','mutantsDetected']}))
 if result['failed']:raise SystemExit(1)
if __name__=='__main__':main()
