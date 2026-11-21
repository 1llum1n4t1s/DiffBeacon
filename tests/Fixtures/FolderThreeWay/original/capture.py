# 入力設計と実行記録のみ。期待算法の再実装は行わない。
import base64, hashlib, json, pathlib, subprocess, time, datetime, ctypes
ROOT = pathlib.Path(__file__).resolve().parent
def sha(data): return hashlib.sha256(data).hexdigest()
if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser();parser.add_argument('--run',action='store_true');parser.add_argument('--output',required=True,type=pathlib.Path);parser.add_argument('--executable',required=True,type=pathlib.Path)
    parser.add_argument('--no-observer',action='store_true');args=parser.parse_args()
    if not args.run:raise SystemExit('Parent native-start instruction required; use --run.')
    output=args.output.resolve();exe=args.executable.resolve()
    if ROOT not in output.parents or output.exists():raise RuntimeError('Capture output must be fresh and below harness')
    if ROOT not in exe.parents:raise RuntimeError('Executable must belong to this harness attempt')
    for p in [output.parent,*output.parents,exe,*exe.parents]:
        if p.is_symlink():raise RuntimeError('Links forbidden')
    build=json.loads((exe.parent/'build-status.json').read_text(encoding='utf-8-sig'))
    if not build['complete'] or sha(exe.read_bytes()).lower()!=build['executableSha256'].lower():raise RuntimeError('Build/exe mismatch')
    manifest=json.loads((exe.parent/'harness-manifest.json').read_text(encoding='utf-8-sig'))
    for item in manifest:
        data=(ROOT/item['path']).read_bytes()
        if len(data)!=item['bytes'] or sha(data).lower()!=item['sha256'].lower():raise RuntimeError('Harness changed after compile')
    output.mkdir()
    (output/'harness-manifest.json').write_bytes((exe.parent/'harness-manifest.json').read_bytes())
    transport=(ROOT/'transport.ndjson').read_bytes();(output/'transport.ndjson').write_bytes(transport)
    cases=[json.loads(line) for line in transport.decode('utf-8-sig').splitlines()]
    with (output/'expected.ndjson').open('x',encoding='utf-8') as expected:
        for case in cases:
            work=output/case['id'];work.mkdir(exist_ok=False);paths=[]
            for side,v in enumerate(case['sides']):
                p=work/f'{side}.txt';data=base64.b64decode(v['bytesBase64']);data.decode('utf-8',errors='strict')
                if b'\x00' in data:raise RuntimeError('NUL input outside scope')
                if sha(data)!=v['sha256']:raise RuntimeError('Transport input SHA mismatch')
                p.write_bytes(data);paths.append(p)
            command=[str(exe),str(case['presenceMask']),*map(str,paths)]
            if args.no_observer:command.append('--no-observer')
            requested=datetime.datetime.now(datetime.timezone.utc).isoformat();proc=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            creation,exit_time,kernel,user=(ctypes.c_ulonglong() for _ in range(4));get_times=ctypes.windll.kernel32.GetProcessTimes
            get_times.argtypes=[ctypes.c_void_p,*([ctypes.POINTER(ctypes.c_ulonglong)]*4)]
            birth_ok=bool(get_times(ctypes.c_void_p(int(proc._handle)),*[ctypes.byref(x) for x in (creation,exit_time,kernel,user)]))
            timed_out=False
            try:out,err=proc.communicate(timeout=30)
            except subprocess.TimeoutExpired:timed_out=True;proc.kill();out,err=proc.communicate()
            actual=proc.wait();pid=proc.pid;proc.stdout.close();proc.stderr.close();proc._handle.Close()
            (work/'stdout.bin').write_bytes(out);(work/'stderr.bin').write_bytes(err)
            record={'timedOut':timed_out,'pid':pid,'birthObserved':birth_ok,'birthWindowsFileTime':creation.value,'requestedUtc':requested,'arguments':command,'actualExit':actual,'exitObserved':True,'waitCompleted':True,'disposed':True,'stdoutComplete':True,'stderrComplete':True,'stdoutBytes':len(out),'stderrBytes':len(err),'stdoutSha256':sha(out),'stderrSha256':sha(err),'executableSha256':sha(exe.read_bytes())}
            (work/'process.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
            for p,v in zip(paths,case['sides']):
                if sha(p.read_bytes())!=v['sha256']:raise RuntimeError('input changed')
            if not birth_ok or timed_out or actual!=0:raise RuntimeError(f'native failure {actual}: {case["id"]}')
            value=json.loads(out)
            for key in ['presenceMask','threewayInputFlags','pairs','ranges','significantCount','totalCount','reportedSignificant','reportedTrivial','flagsBefore','flagsAfter']:
                if key not in value:raise RuntimeError(f'missing field {key}')
            expected.write(json.dumps({'id':case['id'],'original':value})+'\n');expected.flush()
    (output/'capture-status.json').write_text(json.dumps({'observerEnabled':not args.no_observer,'complete':True,'count':len(cases),'expectedSha256':sha((output/'expected.ndjson').read_bytes()),'transportSha256':sha(transport),'executable':str(exe),'executableSha256':sha(exe.read_bytes())}),encoding='utf-8')


