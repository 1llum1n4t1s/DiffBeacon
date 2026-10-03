"""静的overlay原本核をMSVCで採取する。期待計算は別verify-reference.py。"""
import argparse
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess

ROOT=Path(__file__).resolve().parent
REV='da639cdfaeca87aaad0eaceec509afa11ad61421'
SOURCE_SHA='7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28'
IMAGE_SHA='173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609'
CL=Path('C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe')
def sha(data):return hashlib.sha256(data).hexdigest().upper()
def save(path,value):path.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

# BGRA字句: 非対称2x3、透明hiddenRGB、.3/.5のtrunc境界、255端値。
LITERALS=[
 [(1,2,3,255),(254,127,0,128),(3,9,11,0),(9,101,201,255),(15,17,19,128),(63,65,67,0)],
 [(2,3,4,0),(1,128,255,255),(11,13,17,128),(110,12,14,0),(21,23,25,255),(71,73,75,128)],
 [(7,8,9,128),(250,251,252,0),(19,23,29,255),(31,37,41,128),(43,47,53,0),(79,83,89,255)],
]

def cases():
    result=[]
    def add(name,n,mode,alpha=.3,layout='full',show=False,highlight=.7,selected=-1,wipe=0,position=0,kind='conflict'):
        ims=[]
        for pane in range(n):
            w,h=(2,3) if layout=='full' else ((1,2),(2,3),(2,1))[pane]
            ox,oy=((0,0),(1,0),(0,1))[pane] if layout=='offset' else (0,0)
            pixels=LITERALS[pane] if kind=='conflict' else LITERALS[0 if pane!=0 else 1]
            data=[b for y in range(h) for x in range(w) for b in pixels[y*2+x]]
            ims.append({'width':w,'height':h,'offsetX':ox,'offsetY':oy,'bgraBytes':data})
        result.append({'name':name,'mode':mode,'overlayAlpha':alpha,'showDifferences':show,'highlightAlpha':highlight,'selectedDiffIndex':selected,'wipeMode':wipe,'wipePosition':position,'blockSize':1,'threshold':0,'images':ims})
    for n in (2,3):
        for mode in ('none','xor','alpha'):
            add(f'{n}-{mode}-full-default',n,mode)
            add(f'{n}-{mode}-padding',n,mode,layout='padding')
            add(f'{n}-{mode}-offset',n,mode,layout='offset')
        for alpha in (0,.5,1):add(f'{n}-alpha{alpha}-rounding',n,'alpha',alpha)
        for highlight in (0,.7,1):
            add(f'{n}-highlight{highlight}-normal',n,'alpha',show=True,highlight=highlight)
            add(f'{n}-highlight{highlight}-selected',n,'xor',show=True,highlight=highlight,selected=0)
        for wipe,pos in ((1,0),(1,1),(1,3),(2,0),(2,1),(2,2)):
            add(f'{n}-wipe{wipe}-position{pos}',n,'alpha',show=True,selected=0,wipe=wipe,position=pos)
    add('3-left-only-selected',3,'alpha',show=True,selected=0,kind='left-only')
    return result

def main():
    p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--golden');a=p.parse_args()
    out=Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True)
    source_path=ROOT.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp';image_path=ROOT.parent/'ImageRegions/reference-source/image.hpp'
    source=source_path.read_bytes();image=image_path.read_bytes()
    if sha(source)!=SOURCE_SHA or sha(image)!=IMAGE_SHA:raise ValueError('source SHA')
    extraction=[]
    specs=[('original-types.inc',source,[(33,161)]),('original-modes.inc',source,[(479,487)]),('original-color.inc',image,[(384,395)]),('original-functions.inc',source,[(1166,1218),(1663,1700),(1702,1964),(1966,2053),(2055,2125)])]
    for name,data,ranges in specs:
        lines=data.splitlines(keepends=True);excerpt=b''.join(b''.join(lines[start-1:end]) for start,end in ranges)
        (out/name).write_bytes(excerpt);extraction.append({'file':name,'source':'image.hpp' if data is image else 'ImgDiffBuffer.hpp','lineRanges':ranges,'sha256':sha(excerpt),'unaltered':True})
    save(out/'extraction.json',extraction);inputs=cases();save(out/'probe-inputs.json',inputs)
    words=[str(len(inputs))]
    for c in inputs:
        words.extend(map(str,[len(c['images']),{'none':0,'xor':1,'alpha':2}[c['mode']],c['overlayAlpha'],int(c['showDifferences']),c['highlightAlpha'],c['selectedDiffIndex'],c['wipeMode'],c['wipePosition']]))
        for im in c['images']:words.extend(map(str,[im['width'],im['height'],im['offsetX'],im['offsetY'],*im['bgraBytes']]))
    raw=(' '.join(words)+'\n').encode('ascii');(out/'probe-input.txt').write_bytes(raw)
    exe=out/'image-overlay-probe.exe';cmd=[str(CL),'/nologo','/std:c++17','/EHsc','/Y-','/utf-8','/W3','/Od','/Fe'+str(exe),'/Fo'+str(out)+os.sep,'/I'+str(out),str(ROOT/'probe-adapter.cpp')]
    build=subprocess.run(cmd,capture_output=True);(out/'compiler.log').write_bytes(build.stdout+build.stderr)
    if build.returncode:raise RuntimeError('compile failed '+str(out/'compiler.log'))
    run=subprocess.run([str(exe)],input=raw,capture_output=True);(out/'probe-output.jsonl').write_bytes(run.stdout);(out/'probe-stderr.log').write_bytes(run.stderr)
    if run.returncode:raise RuntimeError('probe failed')
    results=[json.loads(line) for line in run.stdout.splitlines()]
    if len(results)!=len(inputs):raise ValueError('case count')
    total=0
    for c,r in zip(inputs,results):
        for field in ('classificationBefore','classificationAfter'):
            r[field+'Sha256']=sha(json.dumps(r[field],sort_keys=True,separators=(',',':')).encode('ascii'))
        for field in ('rawBefore','rawAfter','baseCanvas','processed'):
            for im in r[field]:
                data=bytes(im['bytes']);im['bgraBase64']=__import__('base64').b64encode(data).decode('ascii');im['sha256']=sha(data)
                if field=='processed':total+=len(data)
        c['expected']=r
    golden={'schemaVersion':1,'sourceVersion':'WinIMerge v1.0.54','sourceRevision':REV,'sourceSha256':SOURCE_SHA,'imageSourceSha256':IMAGE_SHA,'sourceExcerpts':extraction,'license':'GPL-2.0-or-later; ../ImageRegions/LICENSE.txt','adapterBoundary':'preprocessed BGRA input; zero canvas setSize; transparency cache pixel-neutral stub; static overlay only','cases':inputs}
    payload=(json.dumps(golden,ensure_ascii=False,indent=2)+'\n').encode('utf-8');buffer=io.BytesIO()
    with gzip.GzipFile(filename='',mode='wb',fileobj=buffer,mtime=0,compresslevel=9) as f:f.write(payload)
    packed=buffer.getvalue();(out/'winimerge-image-overlay-golden.json.gz').write_bytes(packed)
    if a.golden:Path(a.golden).write_bytes(packed)
    if sha(source_path.read_bytes())!=SOURCE_SHA or sha(image_path.read_bytes())!=IMAGE_SHA:raise ValueError('source changed')
    meta={'cases':len(inputs),'fullBgraBytes':total,'payloadSha256':sha(payload),'gzipSha256':sha(packed),'gzipBytes':len(packed),'sourceSha256':SOURCE_SHA,'imageSourceSha256':IMAGE_SHA,'adapterSha256':sha((ROOT/'probe-adapter.cpp').read_bytes()),'buildExit':build.returncode,'probeExit':run.returncode,'inputSha256':sha(raw),'outputSha256':sha(run.stdout),'sourceUnchanged':True,'compiler':str(CL),'command':cmd,'include':os.environ.get('INCLUDE'),'lib':os.environ.get('LIB')}
    save(out/'metadata.json',meta);print(json.dumps({k:v for k,v in meta.items() if k not in ('command','include','lib')}))

if __name__=='__main__':main()
