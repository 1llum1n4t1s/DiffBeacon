# 原本golden採取の独立reader。比較算法は実装せず、全bodyと原本公開値の整合を照合する。
import argparse, base64, hashlib, json, pathlib
ROOT=pathlib.Path(__file__).resolve().parent
REPO=ROOT.parents[3]
def sha(data):return hashlib.sha256(data).hexdigest().lower()
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def require(ok,message):
    if not ok:raise RuntimeError(message)
def verify(output):
    status=read(output/'capture-status.json');require(status['complete'] and status['count']==63,'No complete 63-case capture')
    exe=pathlib.Path(status['executable']);require(sha(exe.read_bytes())==status['executableSha256'].lower(),'exe hash')
    build=read(exe.parent/'build-status.json');require(build['complete'],'build incomplete')
    snapshot=exe.parent/'harness-snapshot'
    manifest_body=(output/'harness-manifest.json').read_bytes()
    require(manifest_body==(exe.parent/'harness-manifest.json').read_bytes(),'build/capture manifest differs')
    require(sha(manifest_body)==build['manifestSha256'].lower(),'build manifest SHA')
    for item in json.loads(manifest_body.decode('utf-8-sig')):
        body=(snapshot/item['path']).read_bytes();require(len(body)==item['bytes'] and sha(body)==item['sha256'].lower(),f'harness hash {item["path"]}')
    for record in read(snapshot/'provenance.json'):
        body=(REPO/record['source']).read_bytes();require(sha(body)==record['sourceSha256'].lower(),'source SHA')
        if 'copy' in record:require(body==(snapshot/record['copy']).read_bytes(),'copy exact bytes')
        if 'slice' in record:
            # keepends=TrueでCRLF/LFも原本byteとして照合する。
            expected=b''.join(body.splitlines(keepends=True)[record['first']-1:record['last']]);actual=(snapshot/record['slice']).read_bytes()
            if 'byteStart' in record:expected=body[record['byteStart']:record['byteStart']+record['byteLength']]
            require(expected==actual and sha(actual)==record['sliceSha256'].lower(),'slice exact bytes')
    for tag in [*[f'compile-{i}' for i in range(6)],'link']:
        p=read(exe.parent/f'{tag}.process.json')
        require(p['actualExit']==0 and p['pid']>0 and p['birthUtc'],'build process identity/exit')
        require(all(p[x] for x in ['exitObserved','waitCompleted','disposed','stdoutComplete','stderrComplete']),'build process completion')
        for stream in ['stdout','stderr']:
            body=(exe.parent/f'{tag}.{stream}.bin').read_bytes();require(len(body)==p[stream+'Bytes'] and sha(body)==p[stream+'Sha256'].lower(),'build full stream SHA/size')
    transport=(output/'transport.ndjson').read_bytes();expected_body=(output/'expected.ndjson').read_bytes()
    require(sha(transport)==status['transportSha256'] and sha(expected_body)==status['expectedSha256'],'final body SHA')
    cases=[json.loads(x) for x in transport.decode('utf-8-sig').splitlines()];golden=[json.loads(x) for x in expected_body.decode('utf-8').splitlines()]
    require(len(cases)==len(golden)==63 and len({x['id'] for x in cases})==63,'63 unique cases')
    require({x['presenceMask'] for x in cases}==set(range(1,8)),'presence coverage')
    for case,item in zip(cases,golden):
        require(case['id']==item['id'],'case order/ID');work=output/case['id'];p=read(work/'process.json');value=item['original']
        require(p['pid']>0 and p['birthObserved'] and p['birthWindowsFileTime']>0 and p['actualExit']==0 and not p['timedOut'],'process identity/actual exit')
        require(all(p[x] for x in ['exitObserved','waitCompleted','disposed','stdoutComplete','stderrComplete']),'native process completion')
        require(p['executableSha256']==status['executableSha256'],'per-process executable SHA')
        for stream in ['stdout','stderr']:
            body=(work/f'{stream}.bin').read_bytes();require(len(body)==p[stream+'Bytes'] and sha(body)==p[stream+'Sha256'],'native full stream SHA/size')
        require(json.loads((work/'stdout.bin').read_bytes())==value,'expected equals complete stdout JSON')
        for side,s in enumerate(case['sides']):
            body=(work/f'{side}.txt').read_bytes();require(body==base64.b64decode(s['bytesBase64']) and sha(body)==s['sha256'],'all input bytes/SHA');body.decode('utf-8');require(b'\0' not in body,'NUL unsupported')
            require(s['exists']==bool(case['presenceMask']&(1<<side)),'presence transport')
        mask=case['presenceMask'];require(value['presenceMask']==mask and value['threewayInputFlags']==((mask<<28)|0x200000),'THREEWAY/presence raw flags')
        require(len(value['pairs'])==3,'all pairs')
        for pair in value['pairs']:
            require(pair['binaryStatus']==pair['binaryFiles']==0,'text-only boundary')
            require(isinstance(pair['ranges'],list) and isinstance(pair['rawChanges'],list),'pair range/raw bodies')
            for c in pair['rawChanges']:require(set(['line0','line1','deleted','inserted','trivial'])<=c.keys(),'raw/trivial body')
        ranges=value['ranges'];significant=sum(d['op']!=5 for d in ranges)
        require(value['totalCount']==len(ranges) and value['significantCount']==significant,'merged counts')
        if mask in [1,2,4]:require(value['reportedSignificant']==value['reportedTrivial']==-1,'unique unknown counts')
        else:require(value['reportedSignificant']==significant and value['reportedTrivial']==len(ranges)-significant,'reported counts')
        before=value['flagsBefore'];after=value['flagsAfter']
        require(after==(before if mask==7 else (before&~0x7000)|0x1000),'original final missing correction')
        require((before&0x7000)==(0x1000 if significant else 0x2000) and (before&0x3f)==1 and (before&0xc0)==0x40,'Full flags/count boundary')
        if significant:
            identical=[not any(not c['trivial'] for c in pair['rawChanges']) for pair in value['pairs']]
            only=0x8000 if identical[1] else 0x10000 if identical[2] else 0x18000 if identical[0] else 0
            require((before&0x18000)==only,'original ONLY precedence')
    return {'complete':True,'cases':63,'allInputBytes':True,'allProcessBodies':True,'allSourceSlices':True,'threewayPresenceFlags':True,'originalExpectedBodies':True,'scope':'structural/original-public-value checks; no independent line algorithm oracle'}
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--output',required=True,type=pathlib.Path);args=p.parse_args();output=args.output.resolve()
    require(ROOT in output.parents,'reader output outside harness')
    result=verify(output);target=output/'independent-reader.json'
    with target.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2)
    print(json.dumps(result))


