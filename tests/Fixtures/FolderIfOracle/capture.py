"""固定raw blobの無変更spanを外部fresh TempでMSVC compile・別process実行する。"""
import sys
sys.dont_write_bytecode = True
import argparse, ctypes, datetime, hashlib, json, os, pathlib, subprocess, zipfile
BASE = pathlib.Path(__file__).resolve().parent
COMMIT = '6780924ba35f7f55af0f6f14dd302561175830a6'
SOURCES = ['Src/FilterEngine/FilterExpressionNodes.cpp', 'Src/FilterEngine/FilterExpressionNodes.h',
           'Externals/poco/Foundation/src/Timestamp.cpp', 'Externals/poco/Foundation/include/Poco/Timestamp.h',
           'LICENSE.md', 'Externals/poco/LICENSE']

def sha(data): return hashlib.sha256(data).hexdigest()
def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('xb') as file: file.write(data if isinstance(data, bytes) else data.encode('utf8'))
def js(path, value): write(path, json.dumps(value, ensure_ascii=False, indent=2)+'\n')

def validate(path):
    path = path.absolute()
    for item in [path, *path.parents]:
        assert not item.is_symlink() and not item.is_junction(), str(item)
        if item.exists(): assert not (getattr(item.stat(), 'st_file_attributes', 0) & 0x400), str(item)
    path = path.resolve()
    roots = [BASE.parents[2], pathlib.Path.home()/'.codex']
    if os.environ.get('CODEX_HOME'): roots.append(pathlib.Path(os.environ['CODEX_HOME']))
    assert all(not path.is_relative_to(root.resolve()) for root in roots), str(path)
    assert path.is_relative_to(pathlib.Path(r'C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\if-oracle-20261017').resolve()), str(path)
    assert all(part.lower() != 'artifacts' for part in path.parts), str(path)
    return path

def block(data, marker):
    start = data.index(marker.encode()); index = data.index(b'{', start); level = 0; state = 'code'
    while index < len(data):
        char = data[index:index+1]; pair = data[index:index+2]
        if state == 'code':
            if pair == b'//': state = 'line'; index += 1
            elif pair == b'/*': state = 'comment'; index += 1
            elif char == b'"': state = 'string'
            elif char == b"'": state = 'char'
            elif char == b'{': level += 1
            elif char == b'}':
                level -= 1
                if level == 0: return start, index+1
        elif state == 'line':
            if char == b'\n': state = 'code'
        elif state == 'comment':
            if pair == b'*/': state = 'code'; index += 1
        elif char == b'\\': index += 1
        elif state == 'string' and char == b'"' or state == 'char' and char == b"'": state = 'code'
        index += 1
    raise ValueError(marker)

def spans(data):
    result = []
    def add(output, path, marker=None, bounds=None):
        raw = data[path]; start, end = bounds if bounds else block(raw, marker)
        result.append(dict(output=output, path=path, startByte=start, endByte=end,
                           startLine=raw[:start].count(b'\n')+1, endLine=raw[:end].count(b'\n')+1,
                           sha256=sha(raw[start:end])))
    path = SOURCES[0]
    add('eval-bool.inc', path, 'static std::optional<bool> evalAsBool(')
    add('if.inc', path, 'static auto IfFunc(')
    add('function-info.inc', path, 'struct FunctionInfo\n' if b'struct FunctionInfo\n' in data[path] else 'struct FunctionInfo\r\n')
    result[-1]['endByte'] += 1
    result[-1]['sha256'] = sha(data[path][result[-1]['startByte']:result[-1]['endByte']])
    raw = data[path]; start = raw.index(b'\tsize_t argCount = args ? args->size() : 0;'); end = raw.index(b'\n\tfunc = info->func;', start)
    add('arity-check.inc', path, bounds=(start, end))
    raw = data[path]; start = raw.index(b'{ "if"') if b'{ "if"' in raw else raw.index(b'"if"')
    start = raw.rfind(b'\n', 0, start)+1
    add('registration.inc', path, bounds=(start, raw.index(b'\n', start)+1))
    path = SOURCES[1]; raw = data[path]
    start = raw.index(b'namespace Poco { class RegularExpression; }'); end = raw.index(b'struct FilterEvalContext;')
    add('value-type.inc', path, bounds=(start, end))
    start, end = block(raw, 'struct ExprNode\n') if b'struct ExprNode\n' in raw else block(raw, 'struct ExprNode\r\n')
    add('expr-node.inc', path, bounds=(start, end+1))
    path = SOURCES[2]
    for marker in ['Timestamp::Timestamp(TimeVal tv)', 'Timestamp::Timestamp(const Timestamp& other)', 'Timestamp::~Timestamp()']:
        add('timestamp.inc', path, marker)
    add('timestamp.inc', SOURCES[3], 'inline bool Timestamp::operator ==')
    add('timestamp.inc', SOURCES[3], 'inline Timestamp::TimeVal Timestamp::epochMicroseconds() const')
    return result

def initialize():
    # 作業treeの旧sourceではなく、固定Git commitのraw blobだけを使う。
    repo = BASE.parents[2]
    data = {}; selected = []
    for path in SOURCES:
        raw = subprocess.run(['git','show',COMMIT+':'+path], cwd=repo, capture_output=True, check=True).stdout
        blob = subprocess.run(['git','rev-parse',COMMIT+':'+path], cwd=repo, capture_output=True, check=True).stdout.decode().strip()
        assert hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest() == blob
        data[path] = raw
        selected.append(dict(path=path, blob=blob, size=len(raw), sha256=sha(raw)))
    with zipfile.ZipFile(BASE/'reference-source.zip', 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path, raw in data.items():
            info = zipfile.ZipInfo('original/'+path, (2026,10,17,0,0,0)); info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, raw)
    js(BASE/'source-manifest.json', dict(commit=COMMIT, sources=selected))
    js(BASE/'provenance.json', spans(data))

def load():
    manifest = json.loads((BASE/'source-manifest.json').read_bytes()); assert manifest['commit'] == COMMIT
    data = {}
    with zipfile.ZipFile(BASE/'reference-source.zip') as archive:
        assert set(archive.namelist()) == {'original/'+path for path in SOURCES}
        for item in manifest['sources']:
            raw = archive.read('original/'+item['path'])
            assert len(raw) == item['size'] and sha(raw) == item['sha256']
            assert hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest() == item['blob']
            data[item['path']] = raw
    assert spans(data) == json.loads((BASE/'provenance.json').read_bytes())
    return data

def expression(value):
    kind = value['type']; scalar = value.get('value')
    if kind == 'none': return 'std::monostate{}'
    if kind == 'boolean': return 'true' if scalar else 'false'
    if kind == 'integer': return 'int64_t('+str(scalar)+')'
    if kind == 'double': return 'double('+repr(scalar)+')'
    if kind == 'timestamp': return 'Poco::Timestamp(int64_t('+str(scalar)+'))'
    if kind == 'string': return 'std::string('+json.dumps(scalar, ensure_ascii=False)+')'
    if kind == 'regex': return 'std::shared_ptr<Poco::RegularExpression>('+('new Poco::RegularExpression' if scalar else 'nullptr')+')'
    if kind == 'file-ref': return 'std::shared_ptr<FileContentRef>('+('new FileContentRef' if scalar else 'nullptr')+')'
    if kind == 'array': return 'array({'+','.join(expression(item) for item in scalar)+'})'
    raise ValueError(kind)

def cases():
    plan = json.loads((BASE/'case-plan.json').read_bytes())
    assert len({row['id'] for row in plan}) == len(plan)
    lines = []
    for row in plan:
        assert row['mode'] in ['evaluate','arity']
        if row['mode'] == 'evaluate': assert len(row['args']) == 3
        args = ['{'+expression(arg['value'])+','+('true' if arg.get('throws') else 'false')+'}' for arg in row['args']]
        lines.append('run('+json.dumps(row['id'])+','+json.dumps(row['mode'])+',{'+','.join(args)+'});')
    return '\n'.join(lines)+'\n'

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--initialize-source', action='store_true')
    parser.add_argument('--output', type=pathlib.Path); parser.add_argument('--compiler', type=pathlib.Path); parser.add_argument('--vcvars', type=pathlib.Path)
    args = parser.parse_args()
    if args.initialize_source: initialize(); return
    assert args.output and args.compiler and args.vcvars
    output = validate(args.output); assert not output.exists(), 'fresh-only '+str(output)
    output.mkdir(parents=True); (output/'compiler-temp').mkdir()
    data = load(); extracted = {}
    for span in spans(data): extracted.setdefault(span['output'], []).append(data[span['path']][span['startByte']:span['endByte']]+b'\n')
    for name, pieces in extracted.items(): write(output/name, b''.join(pieces))
    write(output/'adapter.cpp', (BASE/'adapter.cpp').read_bytes()); write(output/'cases.inc', cases())
    compiler = args.compiler.resolve(); vcvars = args.vcvars.resolve(); assert compiler.is_file() and vcvars.is_file()
    options = ['/nologo','/utf-8','/std:c++20','/EHsc','/O2','/Fe:reference.exe','/Fo:adapter.obj','adapter.cpp']
    write(output/'compile.rsp', '\n'.join('"'+item+'"' for item in options))
    write(output/'compile.cmd', '@echo off\ncall "'+str(vcvars)+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+str(compiler)+'" @compile.rsp\nexit /b %errorlevel%\n')
    env = dict(os.environ, TEMP=str(output/'compiler-temp'), TMP=str(output/'compiler-temp'))
    cp = subprocess.run(['cmd.exe','/d','/c',str(output/'compile.cmd')], cwd=output, env=env, capture_output=True, timeout=180)
    write(output/'compile.stdout', cp.stdout); write(output/'compile.stderr', cp.stderr)
    js(output/'compile-receipt.json', dict(exitCode=cp.returncode, compiler=str(compiler), compilerSha256=sha(compiler.read_bytes()),
        vcvars=str(vcvars), vcvarsSha256=sha(vcvars.read_bytes()), temp=env['TEMP'],
        inputSha256={item.name:sha(item.read_bytes()) for item in output.iterdir() if item.suffix in ['.cpp','.inc','.rsp','.cmd']}))
    assert cp.returncode == 0, cp.stdout.decode(errors='replace')[-7000:]
    proc = subprocess.Popen([str(output/'reference.exe')], cwd=output, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    handle = ctypes.c_void_p(int(proc._handle))
    class FT(ctypes.Structure): _fields_ = [('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
    times = [FT() for _ in range(4)]
    assert ctypes.windll.kernel32.GetProcessTimes(handle, *[ctypes.byref(item) for item in times])
    try: stdout, stderr = proc.communicate(timeout=60)
    except subprocess.TimeoutExpired:
        proc.kill(); stdout, stderr = proc.communicate(timeout=10)
        write(output/'timeout.stdout', stdout); write(output/'timeout.stderr', stderr); raise
    write(output/'golden.ndjson', stdout); write(output/'reference.stderr', stderr)
    signaled = ctypes.windll.kernel32.WaitForSingleObject(handle,0) == 0
    receipt = dict(exitCode=proc.returncode, pid=proc.pid, birthFileTime=(times[0].high<<32)|times[0].low,
        heldProcessHandleSignaled=signaled, stdoutEof=True, stderrEof=True, stdoutSha256=sha(stdout),
        executableSha256=sha((output/'reference.exe').read_bytes()), adapterSha256=sha((BASE/'adapter.cpp').read_bytes()),
        casePlanSha256=sha((BASE/'case-plan.json').read_bytes()), caseCount=len(stdout.splitlines()),
        endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    js(output/'receipt.json', receipt)
    assert proc.returncode == 0 and not stderr and signaled
    if (BASE/'golden.ndjson').exists(): assert stdout == (BASE/'golden.ndjson').read_bytes()
    print(json.dumps(receipt))
if __name__ == '__main__': main()
