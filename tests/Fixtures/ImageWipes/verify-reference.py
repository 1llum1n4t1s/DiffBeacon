"""Python標準libだけで固定期待literal・全BGRA・再採取bytesを照合する。"""
import argparse
import base64
import hashlib
import gzip
import json
from pathlib import Path

def main():
    p=argparse.ArgumentParser();p.add_argument('first');p.add_argument('second');p.add_argument('--output',required=True);a=p.parse_args()
    packed=Path(a.first).read_bytes();packed2=Path(a.second).read_bytes()
    first=gzip.decompress(packed);second=gzip.decompress(packed2);g=json.loads(first)
    checks=[{'name':'two-independent-native-runs-bytes-equal','passed':first==second},{'name':'two-gzip-files-bytes-equal','passed':packed==packed2}]
    # 絶対領域の画素番号。原本の差分更新・swapループは使わない。
    vertical=((0,1,2,3,4,5,6,7,8,9,10,11),(3,4,5,6,7,8,9,10,11),(6,7,8,9,10,11),(9,10,11),())
    horizontal=((0,1,2,3,4,5,6,7,8,9,10,11),(1,2,4,5,7,8,10,11),(2,5,8,11),())
    mappings={2:(1,0),3:(1,2,0)}
    states=0;total=0
    for c in g['cases']:
        original=[]
        for im in c['images']:
            pixels=[(0,0,0,0)]*12
            for y in range(im['height']):
                for x in range(im['width']):
                    i=(y*im['width']+x)*4;pixels[y*3+x]=tuple(im['bgraBytes'][i:i+4])
            original.append(pixels)
        masks=vertical if c['mode']=='vertical' else horizontal
        for i,(action,s) in enumerate(zip(c['actions'],c['expected']['states'])):
            pos=max(0,min(len(masks)-1,action['position']));states+=1
            checks.append({'name':f"{c['name']}/{i}/clamped-state",'passed':s['position']==pos and s['oldPosition']==pos})
            for pane,im in enumerate(s['processed']):
                expected=bytes(b for pixel in range(12) for b in original[mappings[len(original)][pane] if pixel in masks[pos] else pane][pixel])
                actual=base64.b64decode(im['bgraBase64'],validate=True);total+=len(actual)
                checks.append({'name':f"{c['name']}/{i}/{pane}/full-BGRA",'passed':actual==expected and list(actual)==im['bytes'] and hashlib.sha256(actual).hexdigest().upper()==im['sha256'] and im['width']==3 and im['height']==4})
        checks.append({'name':c['name']+'/state-count','passed':len(c['actions'])==len(c['expected']['states'])})
    result={'goldenSha256':hashlib.sha256(first).hexdigest().upper(),'gzipSha256':hashlib.sha256(packed).hexdigest().upper(),'gzipBytes':len(packed),'cases':len(g['cases']),'states':states,'fullBgraBytes':total,'checks':len(checks),'failed':sum(not c['passed'] for c in checks),'twoRunsByteEqual':first==second,'twoGzipRunsByteEqual':packed==packed2,'basis':'literal absolute pixel-index regions and pane mappings; no incremental swap code','assertions':checks}
    Path(a.output).write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k!='assertions'}))
    if result['failed']:raise SystemExit(1)

if __name__=='__main__':main()
