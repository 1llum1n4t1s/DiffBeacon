"""失敗先行: ghost/raw混同、座標写像、構造copy/Undo、readonly、変換/offset、閾値hashを原本で確認する。"""
import json, os, subprocess, sys
from pathlib import Path

ROOT=Path(__file__).resolve().parent
HELPER=Path('C:/Users/IMT/dev/DiffBeacon/build/WinIMergeDllReference')
DLL=Path('E:/DiffBeacon-artifacts/reference/winimerge-dll-v1.0.54/distribution/WinIMergeLib.dll')
ns={'__name__':'reference_functions','__file__':str(HELPER/'generate-reference.py')}
exec(compile((HELPER/'generate-reference.py').read_text('utf-8-sig'),str(HELPER/'generate-reference.py'),'exec'),ns)
sha,png,decode_png,write_json=[ns[n] for n in ('sha','png','decode_png','write_json')]
assert sha(DLL.read_bytes())=='36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6'
assert sha((HELPER/'WinIMergeLib.h').read_bytes())=='597902D5FFBE8585524C86C41E73032C607E42E04E65F90F0441E521F84DB4F4'
ns['reject_links'](ROOT)
name=sys.argv[1] if len(sys.argv)>1 else 'run1'
if name not in ('run1','run2','run3','run4','run5'): raise ValueError('fixed run names only')
OUT=ROOT/name
if OUT.exists(): raise RuntimeError('existing evidence')
OUT.mkdir();(OUT/'capture-source.py').write_bytes(Path(__file__).read_bytes())
base=Path('E:/DiffBeacon-artifacts/reference/image-transforms-v1.0.54/transform-probe.cpp').read_text('utf-8-sig')
source=base[:base.index('static void State(')]+(ROOT/'state-function.cpp.txt').read_text('utf-8')+'\n'+base[base.index('int wmain('):]
source=source.replace('api->SetInsertionDeletionDetectionMode(IImgMergeWindow::INSERTION_DELETION_DETECTION_NONE);','api->SetDiffAlgorithm(IImgMergeWindow::MYERS_DIFF);\n        api->SetInsertionDeletionDetectionMode(IImgMergeWindow::INSERTION_DELETION_DETECTION_NONE);')
source=source.replace('else if (kind == "rotate")','else if (kind == "mode") api->SetInsertionDeletionDetectionMode(static_cast<IImgMergeWindow::INSERTION_DELETION_DETECTION_MODE>(index));\n            else if (kind == "offset") api->AddImageOffset(dst, src, index);\n            else if (kind == "rotate")')
source=source.replace('api->SetShowDifferences(false)', 'api->SetShowDifferences(true); api->SetDiffColorAlpha(0.7)')
source=source.replace('else if (kind == "rotate")', 'else if (kind == "alpha") api->SetDiffColorAlpha(index / 100.0);\n            else if (kind == "select") result=api->SelectDiff(index == -2 ? api->GetDiffCount()-1 : index);\n            else if (kind == "rotate")')
probe=OUT/'insertion-probe.cpp';probe.write_text(source,'utf-8')
msvc=Path('C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231')
sdk=Path('C:/Program Files (x86)/Windows Kits/10');version='10.0.28000.0'
cl=msvc/'bin/Hostx64/x64/cl.exe';exe=OUT/'insertion-probe.exe';obj=OUT/'obj';obj.mkdir()
env=dict(os.environ)
env['INCLUDE']=';'.join(map(str,[HELPER,msvc/'include',sdk/f'Include/{version}/ucrt',sdk/f'Include/{version}/um',sdk/f'Include/{version}/shared']))
env['LIB']=';'.join(map(str,[msvc/'lib/x64',sdk/f'Lib/{version}/ucrt/x64',sdk/f'Lib/{version}/um/x64']))
env['PATH']=str(cl.parent)+';'+env['PATH']
command=[str(cl),'/nologo','/std:c++17','/EHsc','/MT','/Y-','/utf-8','/W3','/Od','/Fe'+str(exe),'/Fo'+str(obj)+os.sep,str(probe)]
compiled=subprocess.run(command,cwd=OUT,env=env,capture_output=True,timeout=60)
(OUT/'compiler.log').write_bytes(compiled.stdout+compiled.stderr);write_json(OUT/'compiler.json',{'command':command,'exitCode':compiled.returncode})
if compiled.returncode: raise RuntimeError('compile failed; see compiler.log')
def a(kind,s=-1,d=0,i=0): return [kind,s,d,i]
def image(sequence,horizontal=False,cross=3,alpha=255):
    w,h=(len(sequence),cross) if horizontal else (cross,len(sequence))
    pixels=bytes(v for y in range(h) for x in range(w) for v in [(sequence[x] if horizontal else sequence[y])*23,((y if horizontal else x)*31+7)%256,(sequence[x] if horizontal else sequence[y])*11,alpha])
    return w,h,pixels
cases=[]
patterns=[('insert-start',[[1,2,3],[4,1,2,3]]),('insert-middle',[[1,2,3],[1,4,2,3]]),('insert-end',[[1,2,3],[1,2,3,4]]),('delete-middle',[[1,4,2,3],[1,2,3]]),('transparent-middle',[[1,2,3],[1,4,2,3]]),('two-regions',[[1,2,3],[4,1,2,3,5]])]
for mode in (1,2):
    for label,sequences in patterns:
        actions=[a('mode',i=mode),a('alpha',i=0),a('alpha',i=30),a('alpha',i=70),a('alpha',i=100),a('select',i=0),a('alpha',i=30),a('alpha',i=70),a('select',i=-2),a('alpha',i=100),a('select',i=-1)]
        ro=[0,1] if label=='delete-middle' else [0,0]
        threshold=2 if label=='transparent-middle' else 0
        if label=='insert-end':actions.insert(1,a('offset',1,1,1))
        if label=='two-regions':sequences=[sequences[1],sequences[0],[1,2,3,6]];ro=[0,0,0]
        cases.append((f'{mode}-{label}',1,threshold,ro,[image(s,mode==2,alpha=0 if label=='transparent-middle' else 255) for s in sequences],actions))
observations=[];assertions=[]
def check(label,condition): assertions.append({'name':label,'passed':bool(condition)})
for index,(label,block,threshold,ro,images,actions) in enumerate(cases):
    folder=OUT/f'case-{index:03}';folder.mkdir();inputs=[]
    for pane,(w,h,pixels) in enumerate(images):
        data=png(w,h,pixels);(folder/f'pane{pane}.png').write_bytes(data);inputs.append({'width':w,'height':h,'bgraHex':pixels.hex(),'pngSha256':sha(data)})
    script=f'{len(images)} {block} {threshold} '+' '.join(map(str,ro))+f' {len(actions)}\n'+'\n'.join(' '.join(map(str,item)) for item in actions)+'\n'
    (folder/'actions.txt').write_text(script,encoding='ascii')
    try:
        run=subprocess.run([str(exe),str(DLL)],cwd=folder,input=script.encode(),capture_output=True,timeout=30)
        stdout,stderr,code=run.stdout,run.stderr,run.returncode
    except subprocess.TimeoutExpired as error: stdout,stderr,code=error.stdout or b'',error.stderr or b'','timeout'
    (folder/'stdout.jsonl').write_bytes(stdout);(folder/'stderr.log').write_bytes(stderr)
    record={'name':label,'blockSize':block,'threshold':threshold,'readOnly':ro,'inputs':inputs,'actions':actions,'exitCode':code,'states':[],'exports':[]}
    if code!=0: observations.append(record);continue
    states=[json.loads(line) for line in stdout.splitlines() if line and 'stateIndex' in json.loads(line)]
    check(label+' state count',len(states)==len(actions)+1)
    for state in states:
        for pane,p in enumerate(state['panes']):
            data=(folder/f'state{state["stateIndex"]}-pane{pane}-aligned.png').read_bytes();w,h,pixels=decode_png(data)
            check(f'{label} state{state["stateIndex"]} pane{pane} canvas',w==p['canvasWidth'] and h==p['canvasHeight'])
            p['bgraHex']=pixels.hex();p['bgraSha256']=sha(pixels)
            raw=(folder/f'state{state["stateIndex"]}-pane{pane}.bgra').read_bytes();p['alignedRawBgraHex']=raw.hex();p['alignedRawBgraSha256']=sha(raw)
            check(f'{label} state{state["stateIndex"]} pane{pane} raw canvas', len(raw)==w*h*4)
        record['states'].append(state)
    for pane in range(len(images)):
        data=(folder/f'final-pane{pane}.png').read_bytes();w,h,pixels=decode_png(data)
        check(f'{label} pane{pane} original PNG preserved', (w,h,pixels)==images[pane])
        record['exports'].append({'pane':pane,'width':w,'height':h,'bgraHex':pixels.hex(),'pngSha256':sha(data)})
    observations.append(record)
write_json(OUT/'observations.json',observations);write_json(OUT/'assertions.json',assertions)
summary={'cases':len(cases),'successfulCases':sum(c['exitCode']==0 for c in observations),'originalFailures':[{'name':c['name'],'exitCode':c['exitCode']} for c in observations if c['exitCode']!=0],'states':sum(len(c['states']) for c in observations),'passed':sum(a['passed'] for a in assertions),'failed':sum(not a['passed'] for a in assertions),'dllSha256':sha(DLL.read_bytes()),'probeSha256':sha(exe.read_bytes()),'probeSourceSha256':sha(probe.read_bytes()),'generatorSha256':sha(Path(__file__).read_bytes()),'observationsSha256':sha((OUT/'observations.json').read_bytes())}
write_json(OUT/'summary.json',summary);print(json.dumps(summary))
if summary['failed']: raise SystemExit(1)
