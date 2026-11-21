"""標準libによる独立scalar式。原本incremental scanline/pane loopは移植しない。"""
import argparse
import base64
import gzip
import hashlib
import json
from pathlib import Path

def sha(data):return hashlib.sha256(data).hexdigest().upper()

def expected(c):
    ims=c['images'];n=len(ims)
    w=max(im['width']+im['offsetX'] for im in ims);h=max(im['height']+im['offsetY'] for im in ims)
    def raw(pane,x,y):
        im=ims[pane];x-=im['offsetX'];y-=im['offsetY']
        if x<0 or y<0 or x>=im['width'] or y>=im['height']:return None
        i=(y*im['width']+x)*4;return tuple(im['bgraBytes'][i:i+4])
    def mix(dst,src,channel):
        if src is None or c['mode']=='none':return dst
        if c['mode']=='xor':return dst if channel==3 else dst^src
        return int(dst*(1-c['overlayAlpha'])+src*c['overlayAlpha'])
    base=[[] for _ in ims];overlaid=[[] for _ in ims]
    for y in range(h):
        for x in range(w):
            p0,p1=raw(0,x,y),raw(1,x,y);p2=raw(2,x,y) if n==3 else None
            srcs=(p0,p1,p2)
            for pane in range(n):base[pane].extend(srcs[pane] or (0,0,0,0))
            for channel in range(4):
                d0=(p0 or (0,0,0,0))[channel];d1=(p1 or (0,0,0,0))[channel]
                s0=None if p0 is None else p0[channel];s1=None if p1 is None else p1[channel]
                # 絶対画素ごとの閉じた式。sourceは更新済みout0/out1を参照しない。
                out0=mix(d0,s1,channel)
                out1=mix(d1,s0,channel) if n==2 else mix(mix(d1,s0,channel),None if p2 is None else p2[channel],channel)
                overlaid[0].append(out0);overlaid[1].append(out1)
                if n==3:overlaid[2].append(mix((p2 or (0,0,0,0))[channel],s1,channel))
    # 分類は原本観測として使用。分類算法の独立再実装・検証とは主張しない。
    classified=c['expected']['classificationBefore'];regions={r['id']:r for r in classified['regions']}
    highlighted=[list(b) for b in overlaid]
    if c['showDifferences']:
        for pane in range(n):
            for y in range(h):
                for x in range(w):
                    region=classified['regionIds'][y][x]
                    if not region:continue
                    op=regions[region]['op']
                    if (pane==0 and op==3) or (pane==2 and op==1):continue
                    color=(64,64,255) if region-1==c['selectedDiffIndex'] else (64,255,255)
                    i=(y*w+x)*4;pixel=overlaid[pane][i:i+4];alpha=c['highlightAlpha']
                    replacement=[int(pixel[k]*(1-alpha)+color[k]*alpha) for k in range(3)]+[pixel[3]] if pixel[3] else [*color,int(255*alpha)]
                    highlighted[pane][i:i+4]=replacement
    final=[list(b) for b in highlighted]
    pos=c['wipePosition'];old=2147483647
    if c['wipeMode']:
        pos=max(0,min(h if c['wipeMode']==1 else w,pos));old=pos
        mapping=(1,0) if n==2 else (1,2,0)
        for y in range(h):
            for x in range(w):
                if (y if c['wipeMode']==1 else x)<pos:continue
                i=(y*w+x)*4
                for pane in range(n):final[pane][i:i+4]=highlighted[mapping[pane]][i:i+4]
    return {'width':w,'height':h,'baseCanvas':base,'overlayBeforeHighlight':overlaid,'highlightBeforeWipe':highlighted,'processed':final,'position':pos,'oldPosition':old}

def main():
    p=argparse.ArgumentParser();p.add_argument('first');p.add_argument('second');p.add_argument('--output',required=True);a=p.parse_args()
    out=Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True)
    packed=Path(a.first).read_bytes();packed2=Path(a.second).read_bytes();payload=gzip.decompress(packed);payload2=gzip.decompress(packed2);g=json.loads(payload)
    checks=[];literal=[];count=0
    def check(name,passed):checks.append({'name':name,'passed':passed})
    check('gzip-bytes-two-runs-equal',packed==packed2);check('payload-bytes-two-runs-equal',payload==payload2)
    check('gzip-header-mtime0-no-filename',packed[3]==0 and packed[4:8]==bytes(4))
    for c in g['cases']:
        e=expected(c);literal.append({'name':c['name'],'scalarExpected':e});r=c['expected'];name=c['name']
        check(name+'/classification-observed-unchanged',r['classificationBefore']==r['classificationAfter'] and r['classificationBeforeSha256']==r['classificationAfterSha256'])
        for key in ('classificationBefore','classificationAfter'):check(name+'/'+key+'-sha',sha(json.dumps(r[key],sort_keys=True,separators=(',',':')).encode('ascii'))==r[key+'Sha256'])
        check(name+'/raw-observed-unchanged',r['rawBefore']==r['rawAfter'])
        for field in ('rawBefore','rawAfter','baseCanvas','processed'):
            check(name+'/'+field+'-pane-count',len(r[field])==len(c['images']))
            for pane,im in enumerate(r[field]):
                raw=base64.b64decode(im['bgraBase64'],validate=True)
                check(name+f'/{field}/{pane}/sha-base64-byte',sha(raw)==im['sha256'] and list(raw)==im['bytes'])
                expected_bytes=c['images'][pane]['bgraBytes'] if field in ('rawBefore','rawAfter') else e[field][pane]
                dim=c['images'][pane] if field in ('rawBefore','rawAfter') else e
                check(name+f'/{field}/{pane}/all-BGRA',list(raw)==expected_bytes and im['width']==dim['width'] and im['height']==dim['height'])
                if field=='processed':count+=len(raw)
        check(name+'/wipe-after-highlight',r['position']==e['position'] and r['oldPosition']==e['oldPosition'])
        check(name+'/pixel-neutral-cache-calls',r['cacheCalls']==2)
    # 抜粋SHAと原本保持も別実行で照合する。
    root=Path(__file__).resolve().parent
    for source_name,key in (('ImgDiffBuffer.hpp','sourceSha256'),('image.hpp','imageSourceSha256')):
        source=(root.parent/'ImageRegions/reference-source'/source_name).read_bytes();check(source_name+'/canonical-sha',sha(source)==g[key])
        lines=source.splitlines(keepends=True)
        for item in g['sourceExcerpts']:
            if item['source']!=source_name:continue
            excerpt=b''.join(b''.join(lines[start-1:end]) for start,end in item['lineRanges']);check(item['file']+'/excerpt-sha',sha(excerpt)==item['sha256'])
    result={'cases':len(g['cases']),'fullBgraBytes':count,'checks':len(checks),'failed':sum(not c['passed'] for c in checks),'payloadSha256':sha(payload),'gzipSha256':sha(packed),'gzipBytes':len(packed),'twoRunsPayloadEqual':payload==payload2,'twoRunsGzipEqual':packed==packed2,'basis':'closed scalar overlay expressions, straight double/trunc, original observed classification masks, absolute pane permutation for wipe','assertions':checks}
    (out/'independent-scalar-expectations.json').write_text(json.dumps(literal,indent=2)+'\n',encoding='utf-8')
    (out/'independent-assertions.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k!='assertions'}))
    if result['failed']:raise SystemExit(1)

if __name__=='__main__':main()
