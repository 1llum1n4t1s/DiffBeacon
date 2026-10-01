"""原本抜粋をMSVCで実行する。期待値計算はC++原本だけで行う。"""
import argparse, base64, hashlib, json, os, random, subprocess
from pathlib import Path

ROOT=Path(__file__).resolve().parent
REVISION='da639cdfaeca87aaad0eaceec509afa11ad61421'
SOURCE_SHA='7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28'
IMAGE_SHA='173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609'
MERGE_SHA='956B69A5D76D6918CD6D3245B5DB5BFDEF902C82221E2539FFEA1B33996AE40B'
def sha(data): return hashlib.sha256(data).hexdigest().upper()
def image(w,h,color=(0,0,0,255),changes=()):
    data=bytearray(color*(w*h))
    for x,y,pixel in changes: data[(y*w+x)*4:(y*w+x+1)*4]=bytes(pixel)
    return {'width':w,'height':h,'bgraBase64':base64.b64encode(data).decode('ascii')}
def inputs():
    cases=[];a=(11,29,47,255);b=(191,73,109,255);c=(251,127,61,128)
    ring=[(x*8,y*8,b) for y in range(5) for x in range(5) if x in (0,4) or y in (0,4)]
    lshape=[(x*8,y*8,b) for x,y in ((0,0),(0,1),(0,2),(1,2),(2,2))]
    plain=image(40,40,a)
    two=[('equal',[image(17,17,a)]*2),('ring',[plain,image(40,40,a,ring)]),('lshape',[image(24,24,a),image(24,24,a,lshape)]),
         ('transparent',[image(17,9,(13,37,71,0)),image(17,9,(17,53,103,128))]),
         ('dimensions',[image(9,9,a),image(17,17,b)]),('multi-region-expand',[image(33,17,a,[(0,0,b)]),image(17,17,a)])]
    triple=[('left-only',[image(17,17,a,[(0,0,b)]),image(17,17,a),image(17,17,a)]),
            ('middle-only',[image(17,17,a),image(17,17,a,[(0,0,b)]),image(17,17,a)]),
            ('right-only',[image(17,17,a),image(17,17,a),image(17,17,a,[(16,16,b)])]),
            ('conflict',[image(17,17,b),image(17,17,a),image(17,17,c)]),
            ('ring-hole',[image(40,40,a,ring),plain,image(40,40,a,[(16,16,c)])]),
            ('lshape-hole-dimensions',[image(24,24,a,lshape),image(24,24,a),image(33,24,a,[(16,0,c)])])]
    def action(kind,src=0,dst=1,index=0):return {'kind':kind,'src':src,'dst':dst,'index':index}
    def add(name,ims,actions,readonly=None):cases.append({'name':name,'blockSize':8,'threshold':0,'images':ims,'readOnly':readonly or [False]*len(ims),'actions':actions})
    for name,ims in two+triple:
        for src in range(len(ims)):
            for dst in range(len(ims)):
                if src==dst:continue
                for kind in ('copy','all'):
                    ops=[action(kind,src,dst),action('undo'),action('redo'),action('save',dst=dst),action('undo'),action('redo')]
                    add(f'{len(ims)}-{name}-{kind}-{src}to{dst}',ims,ops)
        if len(ims)==3:
            for dst in range(3):add(f'3-{name}-auto-to{dst}',ims,[action('auto',dst=dst),action('undo'),action('redo'),action('save',dst=dst),action('undo'),action('redo')])
    for n,ims in [(2,two[1][1]),(3,triple[4][1])]:
        for kind,src,dst,index,label in [('copy',0,0,0,'same-pane'),('all',0,n,0,'invalid-dst'),('copy',-1,1,0,'invalid-src'),('copy',0,1,-1,'negative-id'),('copy',0,1,999,'large-id'),('copy',0,1,0,'readonly')]:
            ro=[False]*n;ro[dst if 0<=dst<n else 0]=label=='readonly'
            add(f'{n}-reject-{label}',ims,[action(kind,src,dst,index),action('undo'),action('redo')],ro)
    for n,ims in [(2,two[5][1]),(3,triple[4][1])]:
        add(f'{n}-history-branch',ims,[action('all',0,1),action('undo'),action('copy',1,0),action('redo'),action('undo'),action('redo')])
        add(f'{n}-history-shared-panes',ims,[action('all',0,1),action('all',1,0),action('undo'),action('undo'),action('redo'),action('redo')])
        add(f'{n}-history-savepoint',ims,[action('all',0,1),action('save',dst=1),action('undo'),action('redo'),action('set-savepoint',dst=1,index=0),action('undo')])
    for src in range(3):
        for dst in range(3):
            if src!=dst:add(f'3-ring-hole-selected-last-{src}to{dst}',triple[4][1],[action('copy',src,dst,1),action('undo'),action('redo')])
    add('3-history-two-expansions',[image(9,9,a),image(17,17,b),image(33,25,c)],
        [action('all',1,0),action('all',2,0),action('undo'),action('undo'),action('redo'),action('redo')])
    for n,ims in [(2,two[1][1]),(3,triple[4][1])]:
        ro=[False]*n;ro[1]=True
        add(f'{n}-reject-readonly-all',ims,[action('all',0,1),action('undo'),action('redo')],ro)
        if n==3:add('3-reject-readonly-auto',ims,[action('auto',dst=1),action('undo'),action('redo')],ro)
    add('3-reject-invalid-auto',triple[4][1],[action('auto',dst=3),action('undo'),action('redo')])
    return cases

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);parser.add_argument('--golden');args=parser.parse_args()
    out=Path(args.output).resolve();out.mkdir(parents=True,exist_ok=True)
    source=(ROOT.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp').read_bytes()
    if sha(source)!=SOURCE_SHA: raise ValueError('原本SHA不一致')
    if sha((ROOT.parent/'ImageRegions/reference-source/image.hpp').read_bytes())!=IMAGE_SHA: raise ValueError('image.hpp原本SHA不一致')
    merge=(ROOT/'reference-source/ImgMergeBuffer.hpp').read_bytes()
    if sha(merge)!=MERGE_SHA:raise ValueError('ImgMergeBuffer.hpp原本SHA不一致')
    lines=source.splitlines(keepends=True);merge_lines=merge.splitlines(keepends=True)
    extraction=[]
    ranges_list=[('original-types.inc',[(33,161)]),('original-comparison.inc',[(1663,1693),(1702,1886)]),('original-mode.inc',[(479,481)]),('original-convert.inc',[(1440,1562)]),('original-transformation.inc',[(2264,2279)])]
    for name,ranges in ranges_list:
        data=b''.join(b''.join(lines[a-1:b]) for a,b in ranges)
        (out/name).write_bytes(data);extraction.append({'file':name,'lineRanges':ranges,'sha256':sha(data),'unaltered':True})
    for name,ranges in [('original-history.inc',[(22,135)]),('original-copy.inc',[(216,317),(342,389),(566,689)])]:
        data=b''.join(b''.join(merge_lines[a-1:b]) for a,b in ranges);(out/name).write_bytes(data)
        extraction.append({'file':name,'source':'ImgMergeBuffer.hpp','lineRanges':ranges,'sha256':sha(data),'unaltered':True})
    (out/'extraction.json').write_text(json.dumps(extraction,indent=2)+'\n',encoding='utf-8')
    cases=inputs();(out/'probe-inputs.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8')
    words=[str(len(cases))]
    for c in cases:
        words += [str(len(c['images'])),str(c['blockSize']),str(c['threshold'])]
        for im,ro in zip(c['images'],c['readOnly']):words += [str(im['width']),str(im['height']),str(int(ro))]+[str(b) for b in base64.b64decode(im['bgraBase64'])]
        words += [str(len(c['actions']))]
        for a in c['actions']:words += [a['kind'],str(a['src']),str(a['dst']),str(a['index'])]
    raw=(' '.join(words)+'\n').encode('ascii');(out/'probe-input.txt').write_bytes(raw)
    msvc=Path('C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231')
    sdk=Path('C:/Program Files (x86)/Windows Kits/10');ver='10.0.28000.0';cl=msvc/'bin/Hostx64/x64/cl.exe'
    env=dict(os.environ);env['INCLUDE']=';'.join(map(str,[msvc/'include',sdk/f'Include/{ver}/ucrt']))
    env['LIB']=';'.join(map(str,[msvc/'lib/x64',sdk/f'Lib/{ver}/ucrt/x64',sdk/f'Lib/{ver}/um/x64']))
    env['PATH']=str(cl.parent)+';'+env['PATH']
    exe=out/'image-copy-probe.exe'
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
        for entry in r['states']:
            state=entry.get('state',entry)
            for frame in state['frames']:
                pixels=bytes(frame.pop('bytes'));frame['bgraBase64']=base64.b64encode(pixels).decode('ascii');frame['sha256']=sha(pixels)
        c['expected']=r
    golden={'schemaVersion':1,'sourceRevision':REVISION,'sourceSha256':MERGE_SHA,'comparisonSourceSha256':SOURCE_SHA,'adapterContract':{'setSize':'new zero-filled BGRA','pasteSubImage':'raw BGRA rectangle copy','save':'UndoRecords savepoint only; no encoder'},'cases':cases}
    data=(json.dumps(golden,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    (out/'winimerge-image-copy-golden.json').write_bytes(data)
    if args.golden:Path(args.golden).write_bytes(data)
    metadata={'sourceRevision':REVISION,'sourceSha256':MERGE_SHA,'comparisonSourceSha256':SOURCE_SHA,'goldenSha256':sha(data),'inputSha256':sha(raw),'outputSha256':sha(run.stdout),'adapterSha256':sha((ROOT/'probe-adapter.cpp').read_bytes()),'cases':len(cases),'compiler':str(cl),'arguments':command,'buildExit':build.returncode,'probeExit':run.returncode,'sourceUnchanged':sha((ROOT.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp').read_bytes())==SOURCE_SHA and sha((ROOT/'reference-source/ImgMergeBuffer.hpp').read_bytes())==MERGE_SHA}
    (out/'metadata.json').write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf-8');print(json.dumps(metadata))
if __name__=='__main__':main()
