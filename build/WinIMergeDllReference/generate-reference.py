"""固定配布DLLの公開APIを採取。期待値は既存無改変C++ goldenのみ。"""
import argparse, base64, hashlib, json, os, stat, struct, subprocess, zlib, zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
ZIP_SHA = '0B09DE06BB56F85452CDAE919150FF7F91F22175621F65623515B84107683613'
GOLDEN_SHA = 'A1645415624D84B3CB4A328148151A0FAE48B05412CF2E504589CD98C289D65D'
HEADER_BLOB = 'd5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa'

def sha(raw): return hashlib.sha256(raw).hexdigest().upper()
def write_json(path, data): path.write_text(json.dumps(data, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
def reject_links(path):
    # resolve()でリンクを消す前に、出力までの実際の経路を確認する。
    for candidate in (path, *path.parents):
        try: info=candidate.lstat()
        except FileNotFoundError: continue
        if stat.S_ISLNK(info.st_mode) or getattr(info,'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',0x400):
            raise RuntimeError('linked evidence path: '+str(candidate))
def chunk(kind, raw): return struct.pack('>I', len(raw))+kind+raw+struct.pack('>I', zlib.crc32(kind+raw))
def png(w, h, bgra):
    rgba = bytearray(bgra)
    for i in range(0,len(rgba),4): rgba[i],rgba[i+2] = rgba[i+2],rgba[i]
    rows = b''.join(b'\0'+rgba[y*w*4:(y+1)*w*4] for y in range(h))
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(rows))+chunk(b'IEND',b'')

def decode_png(raw):
    if raw[:8] != b'\x89PNG\r\n\x1a\n': raise ValueError('PNG signature')
    pos=8; data=bytearray(); header=None
    while pos<len(raw):
        length=struct.unpack_from('>I',raw,pos)[0]; kind=raw[pos+4:pos+8]; body=raw[pos+8:pos+8+length]
        if zlib.crc32(kind+body)!=struct.unpack_from('>I',raw,pos+8+length)[0]: raise ValueError('PNG CRC')
        if kind==b'IHDR': header=struct.unpack('>IIBBBBB',body)
        elif kind==b'IDAT': data.extend(body)
        pos+=length+12
        if kind==b'IEND': break
    w,h,depth,color,compression,filter_method,interlace=header
    if depth!=8 or color not in (2,6) or (compression,filter_method,interlace)!=(0,0,0): raise ValueError('independent decoder requires PNG8 RGB/RGBA/noninterlace: '+str(header))
    channels=4 if color==6 else 3
    scan=zlib.decompress(data); stride=w*channels; prior=bytearray(stride); pixels=bytearray()
    for y in range(h):
        start=y*(stride+1); f=scan[start]; row=bytearray(scan[start+1:start+stride+1])
        for x in range(stride):
            a=row[x-channels] if x>=channels else 0; b=prior[x]; c=prior[x-channels] if x>=channels else 0
            if f==1: v=a
            elif f==2: v=b
            elif f==3: v=(a+b)//2
            elif f==4:
                p=a+b-c; distances=(abs(p-a),abs(p-b),abs(p-c)); v=(a,b,c)[distances.index(min(distances))]
            elif f==0: v=0
            else: raise ValueError('PNG filter')
            row[x]=(row[x]+v)&255
        pixels.extend(row); prior=row
    if len(scan)!=h*(stride+1): raise ValueError('PNG scan length')
    bgra=bytearray()
    for i in range(0,len(pixels),channels): bgra.extend((pixels[i+2],pixels[i+1],pixels[i],pixels[i+3] if channels==4 else 255))
    return w,h,bytes(bgra)

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--output',type=Path,required=True); args=parser.parse_args()
    out=Path(os.path.abspath(args.output)); reject_links(out)
    if out.exists(): raise RuntimeError('evidence output already exists; choose a new run directory: '+str(out))
    out.mkdir(parents=True)
    bundle=out.parent/'winimerge-1.0.54-x64.zip'; reject_links(bundle); raw=bundle.read_bytes()
    if len(raw)!=2497470 or sha(raw)!=ZIP_SHA: raise RuntimeError('official bundle mismatch; no load')
    dist=out.parent/'distribution'; reject_links(dist); dist.mkdir(exist_ok=True); inventory=[]
    with zipfile.ZipFile(bundle) as archive:
        for entry in archive.infolist():
            p=PurePosixPath(entry.filename.replace('\\','/')); mode=entry.external_attr>>16
            if p.is_absolute() or any(part in ('..','') or ':' in part for part in p.parts) or stat.S_ISLNK(mode): raise RuntimeError('unsafe ZIP entry: '+entry.filename)
            inventory.append({'name':entry.filename,'size':entry.file_size,'crc':entry.CRC})
            if p.name.lower() not in ('winimergelib.dll','vcomp140.dll','gpl.txt','freeimage-license-gplv2.txt'): continue
            target=dist/p.name
            reject_links(target)
            if target.exists() and target.read_bytes()!=archive.read(entry): raise RuntimeError('existing distribution differs')
            if not target.exists(): target.write_bytes(archive.read(entry))
    write_json(out/'bundle-inventory.json', inventory)
    dll=dist/'WinIMergeLib.dll'
    if not dll.exists(): raise RuntimeError('DLL absent')
    header=(ROOT/'WinIMergeLib.h').read_bytes()
    blob=hashlib.sha1(b'blob '+str(len(header)).encode()+b'\0'+header).hexdigest()
    if blob!=HEADER_BLOB: raise RuntimeError('public header original blob mismatch: '+blob)
    golden_path=REPO/'tests/Fixtures/ImageCopy/winimerge-image-copy-golden.json'; golden_raw=golden_path.read_bytes()
    if sha(golden_raw)!=GOLDEN_SHA: raise RuntimeError('immutable golden SHA mismatch')
    golden=json.loads(golden_raw)
    write_json(out/'source.json',{'revision':'da639cdfaeca87aaad0eaceec509afa11ad61421','headerBlob':blob,'headerSha256':sha(header),'headerAcquisition':'exact pinned GitHub gateway; blob matches prior local cache ls-tree','licenseSha256':sha((ROOT/'LICENSE.txt').read_bytes()),'goldenSha256':GOLDEN_SHA,'bundleSha256':ZIP_SHA,'dllSha256':sha(dll.read_bytes()),'release':383180398,'asset':545555188,'files':[{ 'name':p.name,'sha256':sha(p.read_bytes())} for p in dist.iterdir() if p.is_file()]})
    msvc=Path('C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231'); sdk=Path('C:/Program Files (x86)/Windows Kits/10'); ver='10.0.28000.0'
    cl=msvc/'bin/Hostx64/x64/cl.exe'; exe=out/'dll-api-probe.exe'; obj=out/'obj';obj.mkdir(exist_ok=True)
    env=dict(os.environ); env['INCLUDE']=';'.join(map(str,[msvc/'include',sdk/f'Include/{ver}/ucrt',sdk/f'Include/{ver}/um',sdk/f'Include/{ver}/shared',sdk/f'Include/{ver}/winrt']))
    env['LIB']=';'.join(map(str,[msvc/'lib/x64',sdk/f'Lib/{ver}/ucrt/x64',sdk/f'Lib/{ver}/um/x64']));env['PATH']=str(cl.parent)+';'+env['PATH']
    command=[str(cl),'/nologo','/std:c++17','/EHsc','/MT','/Y-','/utf-8','/W3','/Od','/Fe'+str(exe),'/Fo'+str(obj)+os.sep,str(ROOT/'probe.cpp')]
    build=subprocess.run(command,cwd=out,env=env,capture_output=True);(out/'compiler.log').write_bytes(build.stdout+build.stderr)
    write_json(out/'compiler.json',{'command':command,'exit':build.returncode,'sourceSha256':sha((ROOT/'probe.cpp').read_bytes()),'generatorSha256':sha(Path(__file__).read_bytes())})
    if build.returncode: raise RuntimeError('compile failed: '+str(out/'compiler.log'))
    for option,name in [('/dependents','dll-dependents.txt'),('/exports','dll-exports.txt'),('/headers','dll-headers.txt')]:
        r=subprocess.run([str(cl.parent/'dumpbin.exe'),option,str(dll)],capture_output=True);(out/name).write_bytes(r.stdout+r.stderr)
    assertions=[]; observations=[]; processes=[]; state_count=0; cases_complete=0; png_count=0
    def check(case,label,ok,detail=''): assertions.append({'case':case,'label':label,'pass':bool(ok),'detail':detail})
    # 透明RGB初期反証caseを最初に実行。全ケースはそれぞれ別process。
    cases=sorted(golden['cases'],key=lambda c: (not c['name'].startswith('2-transparent'),c['name']))
    for case in cases:
        name=case['name']; folder=out/'cases'/name; folder.mkdir(parents=True,exist_ok=True)
        write_json(folder/'original-case.json',case)
        inputs=[]
        for pane,image in enumerate(case['images']):
            raw=base64.b64decode(image['bgraBase64']); file=folder/f'pane{pane}.png'; file.write_bytes(png(image['width'],image['height'],raw)); inputs.append(file)
            (folder/f'input-pane{pane}.bgra').write_bytes(raw)
        before=[sha(p.read_bytes()) for p in inputs]
        words=[str(len(inputs)),str(case['blockSize']),repr(case['threshold']),*map(lambda b:str(int(b)),case['readOnly']),str(len(case['actions']))]
        for action in case['actions']: words += [action['kind'],str(action.get('src',0)),str(action.get('dst',1)),str(action.get('index',0))]
        protocol=(' '.join(words)+'\n').encode();(folder/'probe-input.txt').write_bytes(protocol)
        try:
            process=subprocess.Popen([str(exe),str(dll)],cwd=folder,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            pid=process.pid; stdout,stderr=process.communicate(protocol,timeout=45); rc=process.returncode
        except subprocess.TimeoutExpired:
            process.kill();stdout,stderr=process.communicate();rc=process.returncode;check(name,'timeout',False)
        (folder/'stdout.jsonl').write_bytes(stdout);(folder/'stderr.log').write_bytes(stderr)
        processes.append({'case':name,'pid':pid,'exit':rc,'completed':process.poll() is not None})
        check(name,'process-exit',rc==0,str(rc));check(name,'input-PNG-preserved',[sha(p.read_bytes()) for p in inputs]==before)
        try: lines=[json.loads(line) for line in stdout.splitlines() if line.strip()]
        except Exception as error: check(name,'parse-output',False,str(error));continue
        states=[line for line in lines if 'stateIndex' in line]; exports=[line for line in lines if 'exportPane' in line]; expected=case['expected']['states']
        check(name,'state-count',len(states)==len(expected),str(len(states)))
        if len(states)==len(expected): cases_complete+=1
        for index,actual in enumerate(states):
            if index>=len(expected): check(name,'unexpected-state',False,str(index));continue
            target=expected[index] if index==0 else expected[index]['state'];state_count+=1
            if index: check(name,f'state{index}-actionResult',actual['actionResult']==expected[index]['actionResult'],str(actual['actionResult']))
            for field in ('differenceCount','conflictCount'): check(name,f'state{index}-{field}',actual[field]==target[field],str(actual[field]))
            for field in ('undoable','redoable'):check(name,f'state{index}-{field}',actual[field]==target['history'][field])
            check(name,f'state{index}-pane-count',len(actual['panes'])==len(target['frames']))
            for pane,frame in enumerate(actual['panes']):
                frame_raw=(folder/frame['rawFile']).read_bytes();native=target['frames'][pane]; expected_raw=base64.b64decode(native['bgraBase64'])
                frame['sha256']=sha(frame_raw);frame['bgraBase64']=base64.b64encode(frame_raw).decode()
                prefix=f'state{index}-pane{pane}-'
                check(name,prefix+'dimensions',(frame['width'],frame['height'])==(native['width'],native['height']))
                check(name,prefix+'rawBGRA',frame_raw==expected_raw,'actual '+frame['sha256']+' expected '+native['sha256'])
                check(name,prefix+'bpp',frame['bpp']==32,str(frame['bpp']))
                for field in ('modified','savepoint'):check(name,prefix+field,frame[field]==target['history']['panes'][pane][field],str(frame[field]))
                if index==0:check(name,prefix+'input-decoder-bgra',frame_raw==base64.b64decode(case['images'][pane]['bgraBase64']))
        for item in exports:
            pane=item['exportPane']; file=folder/f'final-pane{pane}.png';check(name,f'final-PNG{pane}-save',item['success'] and file.exists())
            if not file.exists() or not states:continue
            try:
                w,h,pixels=decode_png(file.read_bytes());last=states[-1]['panes'][pane]
                check(name,f'final-PNG{pane}-rawBGRA',(w,h)==(last['width'],last['height']) and pixels==(folder/last['rawFile']).read_bytes());png_count+=1
            except Exception as error:check(name,f'final-PNG{pane}-decode',False,str(error))
        observations.append({'name':name,'states':states,'exports':exports,'exit':rc,'inputPngSha256':before,'saveActionScope':'real SaveImageAs to scratch PNG creates savepoint and changes filename/orig container; golden save encoder excluded'})
        write_json(folder/'observed.json',observations[-1])
    write_json(out/'assertions.json',assertions);write_json(out/'observations.json',observations);write_json(out/'processes.json',processes)
    summary={'casesRequested':len(cases),'casesComplete':cases_complete,'statesRequested':935,'statesObserved':state_count,'assertions':len(assertions),'passed':sum(a['pass'] for a in assertions),'failed':sum(not a['pass'] for a in assertions),'pngExportsDecoded':png_count,'allProcessesCompleted':all(p['completed'] for p in processes),'crashes':[p for p in processes if p['exit'] not in (0,5)],'goldenSha256':GOLDEN_SHA,'bundleSha256':ZIP_SHA,'dllSha256':sha(dll.read_bytes()),'observationsSha256':sha((out/'observations.json').read_bytes()),'unobserved':['regionIds','regions.id/op/rect','history.index/count','history.panes.modcount'],'saveActionScope':'actual SaveImageAs, not pure diagnostic mark','output':str(out)}
    write_json(out/'summary.json',summary);print(json.dumps(summary,ensure_ascii=False))
    if summary['failed']: raise SystemExit(1)

if __name__=='__main__':main()
