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
    cases=[]
    def add(name,ims,block=1,threshold=0):
        cases.append({'name':name,'blockSize':block,'threshold':threshold,'images':ims})
    z=(0,0,0,255); red=(0,0,255,255); blue=(255,0,0,255)
    for block in (1,2,8):
        a=image(17,17)
        for n in (2,3): add(f'equal-{n}-block{block}',[a]*n,block)
        l=image(17,17,changes=[(0,0,red)])
        r=image(17,17,changes=[(16,16,blue)])
        add(f'left-only-block{block}',[l,a,a],block)
        add(f'middle-only-block{block}',[a,l,a],block)
        add(f'right-only-block{block}',[a,a,r],block)
        add(f'all-different-block{block}',[image(17,17,red),a,image(17,17,blue)],block)
        add(f'separated-left-right-block{block}',[l,a,r],block)
        d=image(17,17,changes=[(0,0,red),(block,block,red)])
        add(f'diagonal-touch-block{block}',[a,d],block)
        add(f'diagonal-three-mixed-block{block}',[l,a,image(17,17,changes=[(block,block,blue)])],block)
        add(f'boundary-bottom-right-block{block}',[a,r],block)
        add(f'same-block-mixed-block{block}',[image(17,17,changes=[(0,0,red)]),a,image(17,17,changes=[(min(block-1,1),0,blue)])],block)
    for channel in range(4):
        for value,threshold,suffix in ((5,5,'exact'),(6,5,'above'),(5,4.999,'fractional-below'),(5,5.001,'fractional-above')):
            pixel=list(z);pixel[channel]=value if channel<3 else 255-value
            add(f'channel{channel}-threshold-{suffix}',[image(1,1),image(1,1,tuple(pixel))],1,threshold)
    for delta,threshold in (((3,4,0,0),5),((3,4,0,1),5),((1,2,2,4),5),((1,2,2,5),5),((255,255,255,255),510)):
        p=tuple(z[i]-delta[i] if i==3 else delta[i] for i in range(4))
        add(f'multichannel-{delta}',[image(1,1),image(1,1,p)],1,threshold)
    add('nontransitive-01-21-equal-02-different',[image(1,1,(0,0,0,255)),image(1,1,(5,0,0,255)),image(1,1,(10,0,0,255))],1,5)
    add('nontransitive-priority-left',[image(1,1,(0,0,0,255)),image(1,1,(10,0,0,255)),image(1,1,(5,0,0,255))],1,5)
    for block in (1,2,8):
        for dims in (((1,1),(2,1)),((3,2),(2,3)),((9,9),(8,8)),((2,2),(2,2),(3,3)),((2,3),(3,2),(1,1)),((1,1),(1,1),(9,17))):
            add('dimensions-'+str(dims)+'-block'+str(block),[image(*d) for d in dims],block)
    rng=random.Random(1054)
    for i in range(90):
        n=2+i%2;w=rng.randrange(1,34);h=rng.randrange(1,27);base=bytearray(rng.randrange(256) for _ in range(w*h*4));ims=[]
        for pane in range(n):
            data=bytearray(base)
            for _ in range(rng.randrange(0, max(2,w*h//4))):
                j=rng.randrange(len(data));data[j]=rng.randrange(256)
            ims.append({'width':w,'height':h,'bgraBase64':base64.b64encode(data).decode('ascii')})
        add(f'seed1054-random-{i:03}',ims,(1,2,8)[i%3],(0,1,5,32,128,510)[i%6])
    return cases

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);parser.add_argument('--golden');args=parser.parse_args()
    out=Path(args.output).resolve();out.mkdir(parents=True,exist_ok=True)
    source=(ROOT/'reference-source/ImgDiffBuffer.hpp').read_bytes()
    if sha(source)!=SOURCE_SHA: raise ValueError('原本SHA不一致')
    if sha((ROOT/'reference-source/image.hpp').read_bytes())!=IMAGE_SHA: raise ValueError('image.hpp原本SHA不一致')
    lines=source.splitlines(keepends=True)
    extraction=[]
    for name,ranges in [('original-types.inc',[(33,161)]),('original-functions.inc',[(1663,1693),(1702,1886)])]:
        data=b''.join(b''.join(lines[a-1:b]) for a,b in ranges)
        (out/name).write_bytes(data);extraction.append({'file':name,'lineRanges':ranges,'sha256':sha(data),'unaltered':True})
    (out/'extraction.json').write_text(json.dumps(extraction,indent=2)+'\n',encoding='utf-8')
    cases=inputs();(out/'probe-inputs.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8')
    words=[str(len(cases))]
    for c in cases:
        words += [str(len(c['images'])),str(c['blockSize']),str(c['threshold'])]
        for im in c['images']: words += [str(im['width']),str(im['height'])]+[str(b) for b in base64.b64decode(im['bgraBase64'])]
    raw=(' '.join(words)+'\n').encode('ascii');(out/'probe-input.txt').write_bytes(raw)
    msvc=Path('C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231')
    sdk=Path('C:/Program Files (x86)/Windows Kits/10');ver='10.0.28000.0';cl=msvc/'bin/Hostx64/x64/cl.exe'
    env=dict(os.environ);env['INCLUDE']=';'.join(map(str,[msvc/'include',sdk/f'Include/{ver}/ucrt']))
    env['LIB']=';'.join(map(str,[msvc/'lib/x64',sdk/f'Lib/{ver}/ucrt/x64',sdk/f'Lib/{ver}/um/x64']))
    env['PATH']=str(cl.parent)+';'+env['PATH']
    exe=out/'image-regions-probe.exe'
    command=[str(cl),'/nologo','/std:c++17','/EHsc','/Y-','/utf-8','/W3','/Od','/Fe'+str(exe),'/Fo'+str(out)+os.sep,'/I'+str(out),str(ROOT/'probe-adapter.cpp')]
    build=subprocess.run(command,env=env,capture_output=True)
    (out/'compiler.log').write_bytes(build.stdout+build.stderr)
    if build.returncode: raise RuntimeError('原本compile失敗: '+str(out/'compiler.log'))
    run=subprocess.run([str(exe)],input=raw,capture_output=True)
    (out/'probe-output.jsonl').write_bytes(run.stdout);(out/'probe-stderr.log').write_bytes(run.stderr)
    if run.returncode: raise RuntimeError('原本probe失敗')
    results=[json.loads(row) for row in run.stdout.splitlines()]
    if len(results)!=len(cases):raise ValueError('ケース数不一致')
    for c,r in zip(cases,results):c['expected']=r
    golden={'schemaVersion':1,'sourceRevision':REVISION,'sourceSha256':SOURCE_SHA,'seed':1054,'cases':cases}
    data=(json.dumps(golden,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    (out/'winimerge-image-regions-golden.json').write_bytes(data)
    if args.golden:Path(args.golden).write_bytes(data)
    metadata={'sourceRevision':REVISION,'sourceSha256':SOURCE_SHA,'goldenSha256':sha(data),'inputSha256':sha(raw),'outputSha256':sha(run.stdout),'adapterSha256':sha((ROOT/'probe-adapter.cpp').read_bytes()),'cases':len(cases),'compiler':str(cl),'arguments':command,'buildExit':build.returncode,'probeExit':run.returncode,'sourceUnchanged':sha((ROOT/'reference-source/ImgDiffBuffer.hpp').read_bytes())==SOURCE_SHA}
    (out/'metadata.json').write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf-8');print(json.dumps(metadata))
if __name__=='__main__':main()
