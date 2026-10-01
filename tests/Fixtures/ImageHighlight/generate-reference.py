"""原本抜粋をMSVCで実行する。期待値計算はC++原本だけで行う。"""
import argparse, base64, hashlib, json, os, random, subprocess
from pathlib import Path

ROOT=Path(__file__).resolve().parent
REVISION='da639cdfaeca87aaad0eaceec509afa11ad61421'
SOURCE_SHA='7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28'
IMAGE_SHA='173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609'
def sha(data): return hashlib.sha256(data).hexdigest().upper()
def image(w,h,color=(0,0,0,255),changes=()):
    data=bytearray(color*(w*h))
    for x,y,pixel in changes: data[(y*w+x)*4:(y*w+x+1)*4]=bytes(pixel)
    return {'width':w,'height':h,'bgraBase64':base64.b64encode(data).decode('ascii')}
def inputs():
    cases=[];w=17;h=9
    opaque=(13,29,41,255);changed=(211,73,109,255)
    a=image(w,h,opaque)
    left=image(w,h,opaque,[(0,0,changed)])
    right=image(w,h,opaque,[(16,8,changed)])
    mixed=image(w,h,opaque,[(8,8,changed)])
    transparent=image(w,h,(19,47,83,0),[(0,0,(7,99,153,0)),(8,0,(211,77,103,128)),(16,8,(11,57,123,255))])
    semi=image(w,h,(23,59,101,128),[(0,0,(193,71,113,128)),(16,8,(37,67,131,0))])
    scenarios=[
        ('two-separated',[a,image(w,h,opaque,[(0,0,changed),(16,8,changed)])]),
        ('left-only',[left,a,a]),('middle-only',[a,left,a]),('right-only',[a,a,right]),
        ('all-conflict',[image(w,h,(1,3,7,255)),a,image(w,h,(251,127,63,255))]),
        ('adjacent-mixed',[left,a,mixed]),('transparent-semitransparent',[transparent,semi]),
        ('padding-three',[image(9,9,(5,17,37,128)),image(17,9,(11,23,47,0)),image(9,1,(19,31,59,255))])]
    for name,ims in scenarios:
        for alpha in (0,.3,.7,1):
            for selected in (-1,0):
                cases.append({'name':f'{name}-alpha{alpha}-selected{selected}','blockSize':8,'threshold':0,'highlightAlpha':alpha,'selectedDiffIndex':selected,'images':ims})
        cases.append({'name':f'{name}-alpha0.7-selectedLast','blockSize':8,'threshold':0,'highlightAlpha':.7,'selectedDiffIndex':'last','images':ims})
    return cases

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);parser.add_argument('--golden');args=parser.parse_args()
    out=Path(args.output).resolve();out.mkdir(parents=True,exist_ok=True)
    source=(ROOT.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp').read_bytes()
    if sha(source)!=SOURCE_SHA: raise ValueError('原本SHA不一致')
    if sha((ROOT.parent/'ImageRegions/reference-source/image.hpp').read_bytes())!=IMAGE_SHA: raise ValueError('image.hpp原本SHA不一致')
    lines=source.splitlines(keepends=True)
    extraction=[]
    ranges_list=[('original-types.inc',[(33,161)]),('original-functions.inc',[(1663,1693),(1702,1886),(1888,1916),(1918,1964)]),('original-mode.inc',[(479,481)])]
    for name,ranges in ranges_list:
        data=b''.join(b''.join(lines[a-1:b]) for a,b in ranges)
        (out/name).write_bytes(data);extraction.append({'file':name,'lineRanges':ranges,'sha256':sha(data),'unaltered':True})
    image_source=(ROOT.parent/'ImageRegions/reference-source/image.hpp').read_bytes().splitlines(keepends=True)
    color_data=b''.join(image_source[383:395]);(out/'original-color.inc').write_bytes(color_data)
    extraction.append({'file':'original-color.inc','source':'image.hpp','lineRanges':[(384,395)],'sha256':sha(color_data),'unaltered':True})
    (out/'extraction.json').write_text(json.dumps(extraction,indent=2)+'\n',encoding='utf-8')
    cases=inputs();(out/'probe-inputs.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8')
    words=[str(len(cases))]
    for c in cases:
        words += [str(len(c['images'])),str(c['blockSize']),str(c['threshold']),str(c['highlightAlpha']),str(-2 if c['selectedDiffIndex']=='last' else c['selectedDiffIndex'])]
        for im in c['images']: words += [str(im['width']),str(im['height'])]+[str(b) for b in base64.b64decode(im['bgraBase64'])]
    raw=(' '.join(words)+'\n').encode('ascii');(out/'probe-input.txt').write_bytes(raw)
    msvc=Path('C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231')
    sdk=Path('C:/Program Files (x86)/Windows Kits/10');ver='10.0.28000.0';cl=msvc/'bin/Hostx64/x64/cl.exe'
    env=dict(os.environ);env['INCLUDE']=';'.join(map(str,[msvc/'include',sdk/f'Include/{ver}/ucrt']))
    env['LIB']=';'.join(map(str,[msvc/'lib/x64',sdk/f'Lib/{ver}/ucrt/x64',sdk/f'Lib/{ver}/um/x64']))
    env['PATH']=str(cl.parent)+';'+env['PATH']
    exe=out/'image-highlight-probe.exe'
    command=[str(cl),'/nologo','/std:c++17','/EHsc','/Y-','/utf-8','/W3','/Od','/Fe'+str(exe),'/Fo'+str(out)+os.sep,'/I'+str(out),str(ROOT/'probe-adapter.cpp')]
    build=subprocess.run(command,env=env,capture_output=True)
    (out/'compiler.log').write_bytes(build.stdout+build.stderr)
    if build.returncode: raise RuntimeError('原本compile失敗: '+str(out/'compiler.log'))
    run=subprocess.run([str(exe)],input=raw,capture_output=True)
    (out/'probe-output.jsonl').write_bytes(run.stdout);(out/'probe-stderr.log').write_bytes(run.stderr)
    if run.returncode: raise RuntimeError('原本probe失敗')
    results=[json.loads(row) for row in run.stdout.splitlines()]
    if len(results)!=len(cases):raise ValueError('ケース数不一致')
    for c,r in zip(cases,results):
        for processed in r['processed']:
            pixels=bytes(processed.pop('bytes'));processed['bgraBase64']=base64.b64encode(pixels).decode('ascii');processed['sha256']=sha(pixels)
        c['expected']=r
    golden={'schemaVersion':1,'sourceRevision':REVISION,'sourceSha256':SOURCE_SHA,'colors':{'normalRgb':[255,255,64],'selectedRgb':[255,64,64]},'cases':cases}
    data=(json.dumps(golden,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    (out/'winimerge-image-highlight-golden.json').write_bytes(data)
    if args.golden:Path(args.golden).write_bytes(data)
    metadata={'sourceRevision':REVISION,'sourceSha256':SOURCE_SHA,'goldenSha256':sha(data),'inputSha256':sha(raw),'outputSha256':sha(run.stdout),'adapterSha256':sha((ROOT/'probe-adapter.cpp').read_bytes()),'cases':len(cases),'compiler':str(cl),'arguments':command,'buildExit':build.returncode,'probeExit':run.returncode,'sourceUnchanged':sha((ROOT.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp').read_bytes())==SOURCE_SHA}
    (out/'metadata.json').write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf-8');print(json.dumps(metadata))
if __name__=='__main__':main()
