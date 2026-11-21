"""Compile and execute original Poco Glob twice; never synthesize golden answers."""
import argparse
import ctypes
import datetime
import hashlib
import json
import os
import pathlib
import subprocess
import zipfile
from source_tools import block

BASE = pathlib.Path(__file__).resolve().parent
REPO = BASE.parents[2]
def sha(data): return hashlib.sha256(data).hexdigest()
def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(value if isinstance(value, bytes) else value.encode('utf-8'))

def validate(path):
    path = pathlib.Path(path).absolute()
    forbidden = [REPO, pathlib.Path.home()/'.codex']
    if os.environ.get('CODEX_HOME'): forbidden.append(pathlib.Path(os.environ['CODEX_HOME']))
    for ancestor in [path, *path.parents]:
        assert not ancestor.is_symlink() and not ancestor.is_junction(), ancestor
    assert path.resolve().is_relative_to(pathlib.Path(os.environ['TEMP']).resolve())
    assert all(not path.resolve().is_relative_to(p.resolve()) for p in forbidden)
    assert not path.exists(), 'use a new output directory'
    return path

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--output', required=True)
    ap.add_argument('--compiler', required=True)
    ap.add_argument('--vcvars', required=True)
    ap.add_argument('--initial-observation', action='store_true', help=argparse.SUPPRESS)
    args = ap.parse_args()
    output = validate(args.output)
    with zipfile.ZipFile(BASE/'reference-source.zip') as z:
        assert z.testzip() is None
        source = {n[9:]: z.read(n) for n in z.namelist()}
    manifest = json.loads((BASE/'source-manifest.json').read_text())
    assert sorted(source) == sorted(x['path'] for x in manifest['sources'])
    for item in manifest['sources']:
        assert sha(source[item['path']]) == item['sha256']
    cases = json.loads((BASE/'cases.json').read_text(encoding='utf-8'))
    receipts = []
    for run in ['run-1', 'run-2']:
        work = output/run
        for name, data in source.items(): write(work/'original'/name, data)
        poco = 'Externals/poco/Foundation/'
        inc = work/'include/Poco'
        for name in ['Glob', 'TextIterator', 'UTF8Encoding', 'Unicode']:
            write(inc/(name+'.h'), source[poco+'include/Poco/'+name+'.h'])
        for path in sorted((BASE/'support').rglob('*.h')):
            write(work/'include'/path.relative_to(BASE/'support'), path.read_bytes())
        data = source[poco+'src/Glob.cpp']
        spans = []
        for marker in ['Glob::Glob(', 'Glob::~Glob(', 'bool Glob::match(const std::string& subject)',
                       'bool Glob::match(TextIterator&', 'bool Glob::matchAfterAsterisk(', 'bool Glob::matchSet(']:
            start, end, body = block(data, marker)
            spans.append(dict(marker=marker, path=poco+'src/Glob.cpp', startByte=start, endByte=end,
                              sha256=sha(body)))
        write(work/'glob.inc', b'\n\n'.join(data[x['startByte']:x['endByte']] for x in spans)+b'\n')
        write(work/'extractions.json', json.dumps(spans, indent=2)+'\n')
        write(work/'adapter.cpp', (BASE/'adapter.cpp').read_bytes())
        def literal(value): return 'bytes({'+','.join(str(b) for b in value.encode('utf-8'))+'})'
        write(work/'cases.inc', '\n'.join('run('+json.dumps(c['id'])+','+literal(c['subject'])+','+
              literal(c['pattern'])+','+str(c['caseSensitive']).lower()+');' for c in cases)+'\n')
        cdir = work/'original/Externals/poco/dependencies/pcre2/src'
        def rsp(name, values): write(work/name, '\n'.join('"'+str(v)+'"' for v in values)+'\n')
        rsp('c.rsp', ['/nologo', '/c', '/O2', '/DHAVE_CONFIG_H', '/DPCRE2_STATIC', '/I'+str(cdir), cdir/'pcre2.c'])
        rsp('cpp.rsp', ['/nologo', '/utf-8', '/std:c++20', '/EHsc', '/O2', '/DPCRE2_STATIC',
            '/I'+str(work/'include'), '/I'+str(work), '/I'+str(cdir), '/Fe:reference.exe', 'adapter.cpp',
            *[work/'original'/poco/'src'/(name+'.cpp') for name in ['TextIterator', 'UTF8Encoding', 'Unicode']],
            'pcre2.obj'])
        for path in [args.compiler, args.vcvars]:
            assert pathlib.Path(path).is_absolute() and pathlib.Path(path).is_file()
            assert not any(c in path for c in '\r\n"%')
        write(work/'compile.cmd', '@echo off\ncall "'+args.vcvars+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n'+
            '"'+args.compiler+'" @c.rsp\nif errorlevel 1 exit /b %errorlevel%\n'+
            '"'+args.compiler+'" @cpp.rsp\nexit /b %errorlevel%\n')
        compiled = subprocess.run(['cmd', '/c', str(work/'compile.cmd')], cwd=work,
                                  capture_output=True, timeout=240)
        write(work/'compile.stdout', compiled.stdout); write(work/'compile.stderr', compiled.stderr)
        assert compiled.returncode == 0, compiled.stdout.decode(errors='replace')[-7000:]
        started = datetime.datetime.now(datetime.timezone.utc).isoformat()
        proc = subprocess.Popen([str(work/'reference.exe')], cwd=work, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        class FT(ctypes.Structure): _fields_ = [('low', ctypes.c_uint32), ('high', ctypes.c_uint32)]
        times = [FT() for _ in range(4)]; handle = ctypes.c_void_p(int(proc._handle))
        assert ctypes.windll.kernel32.GetProcessTimes(handle, *[ctypes.byref(t) for t in times])
        timed = False
        try: stdout, stderr = proc.communicate(timeout=45)
        except subprocess.TimeoutExpired:
            timed = True; proc.kill(); stdout, stderr = proc.communicate(timeout=10)
        signaled = ctypes.windll.kernel32.WaitForSingleObject(handle, 0) == 0
        receipt = dict(pid=proc.pid, birthFileTime=(times[0].high<<32)|times[0].low,
            heldProcessHandleSignaled=signaled, startedUtc=started, exitCode=proc.returncode,
            timeout=timed, stdoutEof=True, stderrEof=True, stdoutSha256=sha(stdout), stderrSha256=sha(stderr),
            compilerPath=args.compiler, compilerSha256=sha(pathlib.Path(args.compiler).read_bytes()),
            executableSha256=sha((work/'reference.exe').read_bytes()), caseCount=len(cases))
        write(work/'receipt.json', json.dumps(receipt, indent=2)+'\n')
        write(work/'golden.ndjson', stdout); write(work/'reference.stderr', stderr)
        assert proc.returncode == 0 and signaled and not timed and not stderr
        assert [json.loads(line)['id'] for line in stdout.splitlines()] == [c['id'] for c in cases]
        receipts.append(receipt)
    assert (output/'run-1/golden.ndjson').read_bytes() == (output/'run-2/golden.ndjson').read_bytes()
    assert receipts[0]['pid'] != receipts[1]['pid']
    if not args.initial_observation:
        assert (output/'run-1/golden.ndjson').read_bytes() == (BASE/'golden.ndjson').read_bytes()
    print(json.dumps(dict(caseCount=len(cases), runs=receipts)))

if __name__ == '__main__': main()
