"""固定原本を無改変抽出し、既存MSVCでstage1画素核だけを実行する。"""
import argparse
import base64
import hashlib
import gzip
import io
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parent
REVISION = 'da639cdfaeca87aaad0eaceec509afa11ad61421'
SOURCE_SHA = '7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28'
IMAGE_SHA = '173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609'
CL = Path('C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe')

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

# 入力はBGRA字句。alpha=0のhiddenRGBもpaneごとに異なる。
PIXELS = [
    [(1,11,21,255),(2,12,22,128),(3,13,23,0),(4,14,24,0),(5,15,25,255),(6,16,26,128),(7,17,27,128),(8,18,28,0),(9,19,29,255),(10,20,30,255),(11,21,31,128),(12,22,32,0)],
    [(41,51,61,0),(42,52,62,255),(43,53,63,128),(44,54,64,128),(45,55,65,0),(46,56,66,255),(47,57,67,255),(48,58,68,128),(49,59,69,0),(50,60,70,0),(51,61,71,255),(52,62,72,128)],
    [(81,91,101,128),(82,92,102,0),(83,93,103,255),(84,94,104,255),(85,95,105,128),(86,96,106,0),(87,97,107,0),(88,98,108,255),(89,99,109,128),(90,100,110,128),(91,101,111,0),(92,102,112,255)],
]

def inputs():
    cases = []
    for panes in (2, 3):
        for mode, extent in (('vertical', 4), ('horizontal', 3)):
            for padding in (False, True):
                images = []
                for pane in range(panes):
                    w, h = ((2,3),(3,4),(1,2))[pane] if padding else (3,4)
                    pixels = [PIXELS[pane][y*3+x] for y in range(h) for x in range(w)]
                    images.append({'width':w,'height':h,'bgraBytes':[b for p in pixels for b in p]})
                for initial in (-2, 0, 1, extent, extent+2):
                    positions = [initial, initial, extent, 1, 0, extent+2, -2, extent, 0, extent]
                    cases.append({'name':f'{panes}-{mode}-padding{padding}-initial{initial}', 'mode':mode, 'initialOldPosition':2147483647, 'images':images, 'actions':[{'position':p} for p in positions]})
    return cases

def canvas(images):
    # 原本前処理ではなくadapter境界の透明padding。
    out = []
    for im in images:
        data = bytearray(3*4*4)
        for y in range(im['height']):
            data[y*12:y*12+im['width']*4] = bytes(im['bgraBytes'][y*im['width']*4:(y+1)*im['width']*4])
        out.append(bytes(data))
    return out

def independent_expected(c):
    # 列挙済み絶対領域maskとpane対応literalから算出。原本のswap/oldPosition分岐を再実装しない。
    masks = {'vertical':[(1,1,1,1),(0,1,1,1),(0,0,1,1),(0,0,0,1),(0,0,0,0)], 'horizontal':[(1,1,1),(0,1,1),(0,0,1),(0,0,0)]}
    unchanged = {2:(0,1),3:(0,1,2)}
    changed = {2:(1,0),3:(1,2,0)}
    src = canvas(c['images']); n = len(src); result = []
    for action in c['actions']:
        p = max(0,min(len(masks[c['mode']])-1,action['position']))
        pane_data = []
        for pane in range(n):
            data = bytearray()
            for y in range(4):
                for x in range(3):
                    axis = y if c['mode']=='vertical' else x
                    mapping = changed[n] if masks[c['mode']][p][axis] else unchanged[n]
                    i = (y*3+x)*4
                    data.extend(src[mapping[pane]][i:i+4])
            pane_data.append(list(data))
        result.append({'position':p,'oldPosition':p,'paneBgraBytes':pane_data})
    return result

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);parser.add_argument('--golden');args=parser.parse_args()
    out=Path(args.output).resolve();out.mkdir(parents=True,exist_ok=True)
    source_path=ROOT.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp'
    source=source_path.read_bytes()
    if sha(source)!=SOURCE_SHA or sha((ROOT.parent/'ImageRegions/reference-source/image.hpp').read_bytes())!=IMAGE_SHA:
        raise ValueError('原本SHA不一致')
    excerpts=[];lines=source.splitlines(keepends=True)
    for name,a,b in [('original-mode.inc',485,487),('original-wipe.inc',1966,2053)]:
        data=b''.join(lines[a-1:b]);(out/name).write_bytes(data)
        excerpts.append({'file':name,'lineRange':[a,b],'sha256':sha(data),'unaltered':True})
    save(out/'extraction.json',excerpts)
    cases=inputs();save(out/'probe-inputs.json',cases)
    words=[str(len(cases))]
    for c in cases:
        words.extend(map(str,[len(c['images']),1 if c['mode']=='vertical' else 2,len(c['actions'])]))
        for im in c['images']:words.extend(map(str,[im['width'],im['height'],*im['bgraBytes']]))
        words.extend(str(a['position']) for a in c['actions'])
    raw=(' '.join(words)+'\n').encode('ascii');(out/'probe-input.txt').write_bytes(raw)
    exe=out/'image-wipe-probe.exe'
    command=[str(CL),'/nologo','/std:c++17','/EHsc','/Y-','/utf-8','/W3','/Od','/Fe'+str(exe),'/Fo'+str(out)+os.sep,'/I'+str(out),str(ROOT/'probe-adapter.cpp')]
    build=subprocess.run(command,capture_output=True);(out/'compiler.log').write_bytes(build.stdout+build.stderr)
    if build.returncode:raise RuntimeError('compile failed: '+str(out/'compiler.log'))
    run=subprocess.run([str(exe)],input=raw,capture_output=True);(out/'probe-output.jsonl').write_bytes(run.stdout);(out/'probe-stderr.log').write_bytes(run.stderr)
    if run.returncode:raise RuntimeError('probe failed')
    results=[json.loads(r) for r in run.stdout.splitlines()]
    if len(results)!=len(cases):raise ValueError('case count')
    independent=[];assertions=[];byte_count=0
    for c,result in zip(cases,results):
        expected=independent_expected(c);independent.append({'name':c['name'],'literalExpected':expected})
        if len(result['states'])!=len(expected):raise ValueError('state count')
        for i,(state,e) in enumerate(zip(result['states'],expected)):
            passed=state['position']==e['position'] and state['oldPosition']==e['oldPosition'] and [im['bytes'] for im in state['processed']]==e['paneBgraBytes'] and all(im['width']==3 and im['height']==4 for im in state['processed'])
            assertions.append({'case':c['name'],'actionIndex':i,'fullBgraAndStateMatch':passed})
            for im in state['processed']:
                pixels=bytes(im['bytes']);byte_count+=len(pixels);im['bgraBase64']=base64.b64encode(pixels).decode('ascii');im['sha256']=sha(pixels)
        c['initialCanvasBgraBytes']=[list(b) for b in canvas(c['images'])]
        c['expected']=result
    save(out/'independent-literal-expectations.json',{'basis':'absolute region masks and literal pane maps, independent of incremental original swap implementation','cases':independent})
    save(out/'assertions.json',{'checks':len(assertions),'failed':sum(not a['fullBgraAndStateMatch'] for a in assertions),'assertions':assertions})
    if not all(a['fullBgraAndStateMatch'] for a in assertions):raise ValueError('literal expectation mismatch')
    if sha(source_path.read_bytes())!=SOURCE_SHA:raise ValueError('source modified')
    golden={'schemaVersion':1,'sourceVersion':'WinIMerge v1.0.54','sourceRevision':REVISION,'sourceSha256':SOURCE_SHA,'imageSourceSha256':IMAGE_SHA,'sourceExcerpts':excerpts,'license':'GPL-2.0-or-later; ../ImageRegions/LICENSE.txt','adapterBoundary':'common transparent BGRA canvas only; no decoding/preprocessing/overlay/GUI/OS operations','cases':cases}
    data=(json.dumps(golden,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    buffer=io.BytesIO()
    with gzip.GzipFile(filename='',mode='wb',fileobj=buffer,mtime=0,compresslevel=9) as compressed:
        compressed.write(data)
    packed=buffer.getvalue();(out/'winimerge-image-wipe-golden.json.gz').write_bytes(packed)
    if args.golden:Path(args.golden).write_bytes(packed)
    metadata={'sourceSha256':SOURCE_SHA,'goldenSha256':sha(data),'gzipSha256':sha(packed),'gzipBytes':len(packed),'inputSha256':sha(raw),'outputSha256':sha(run.stdout),'adapterSha256':sha((ROOT/'probe-adapter.cpp').read_bytes()),'cases':len(cases),'states':len(assertions),'fullBgraBytes':byte_count,'compiler':str(CL),'command':command,'buildExit':build.returncode,'probeExit':run.returncode,'checks':len(assertions),'failed':0,'sourceUnchanged':True,'include':os.environ.get('INCLUDE'),'lib':os.environ.get('LIB')}
    save(out/'metadata.json',metadata);print(json.dumps({k:v for k,v in metadata.items() if k not in ('include','lib','command')}))

if __name__=='__main__':main()
