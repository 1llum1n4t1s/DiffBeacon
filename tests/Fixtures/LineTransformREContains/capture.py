"""Capture unchanged native recontains/evaluator/reader spans; never calculate expected regex results."""
import argparse
import ctypes
import datetime
import hashlib
import json
import os
import pathlib
import subprocess
import zipfile

BASE = pathlib.Path(__file__).resolve().parent
REPO = BASE.parents[2]
SOURCE = BASE.parent / 'LineTransformRegex/reference-source.zip'
CONTENT_SOURCE_SHA = '492581a3e1803f4ec6f3a06b0cfeedf2574f25cd24d8182fff4698fb64b0c0db'
SOURCE_SHA = '882952f28801756a05dca55fd146ddd706c4b2a3fa653b8e2f5ea76a60bc35bf'

def sha(data): return hashlib.sha256(data).hexdigest()

def block(data, marker):
    start = data.index(marker.encode()); brace = data.index(b'{', start)
    level = 0; index = brace; state = 'code'
    while index < len(data):
        c = data[index:index+1]; pair = data[index:index+2]
        if state == 'code':
            if pair == b'//': state = 'line'; index += 1
            elif pair == b'/*': state = 'comment'; index += 1
            elif c == b'"': state = 'string'
            elif c == b"'": state = 'char'
            elif c == b'{': level += 1
            elif c == b'}':
                level -= 1
                if level == 0:
                    end = index + 1
                    if marker.startswith('struct ') or marker == 'enum Options':
                        assert data[end:end+1] == b';'; end += 1
                    return start, end, data[start:end]
        elif state == 'line':
            if c == b'\n': state = 'code'
        elif state == 'comment':
            if pair == b'*/': state = 'code'; index += 1
        elif c == b'\\': index += 1
        elif (state == 'string' and c == b'"') or (state == 'char' and c == b"'"): state = 'code'
        index += 1
    raise ValueError(marker)

def node(value):
    kind = value['type']; v = value.get('value')
    if kind == 'content': return 'content(' + node(dict(type='string', value=v)) + ')'
    if kind == 'string': return 'new StringLiteral(bytes({' + ','.join(str(x) for x in v.encode()) + '}))'
    if kind == 'integer': return 'new IntLiteral(int64_t(' + str(v) + '))'
    if kind == 'bool': return 'new BoolLiteral(' + str(v).lower() + ')'
    if kind == 'none': return 'new NoneLiteral()'
    if kind == 'array': return 'array({' + ','.join(node(x) for x in v) + '})'
    if kind == 'dynamic': return 'new DynamicNode(' + node(v) + ')'
    if kind == 'throw': return 'new ThrowNode("' + v + '")'
    raise ValueError(kind)

def extract_sources(output):
    assert sha(SOURCE.read_bytes()) == SOURCE_SHA
    with zipfile.ZipFile(SOURCE) as z:
        data = {n[9:]: z.read(n) for n in z.namelist() if n.startswith('original/')}
        assert z.testzip() is None
    root = 'Src/FilterEngine/FilterExpressionNodes.'
    poco = 'Externals/poco/Foundation/'
    spans = []
    def take(path, name, marker):
        start, end, body = block(data[path], marker)
        (output / name).write_bytes(body + b'\n')
        spans.append(dict(file=name, path=path, startByte=start, endByte=end,
                          sourceSha256=sha(data[path]), sha256=sha(body)))
    for name, marker in [
        ('string.inc', 'std::string ToStringValue('), ('flags.inc', 'static inline int getRegexFlags('),
        ('create.inc', 'static std::shared_ptr<Poco::RegularExpression> createRegex('),
        ('regex-string.inc', 'static bool regexMatch(const std::string& str, const std::string& pattern'),
        ('regex-prepared.inc', 'static bool regexMatch(const std::string& str, const Poco::RegularExpression& regex'),
        ('compute.inc', 'static auto compute(int op,'),
        ('binary-optimize.inc', 'ExprNode* BinaryOpNode::Optimize('),
        ('binary-evaluate.inc', 'ValueType BinaryOpNode::Evaluate('),
        ('not-evaluate.inc', 'ValueType NotNode::Evaluate('),
        ('as-bool.inc', 'static std::optional<bool> evalAsBool('),
        ('regex-literal-ctor.inc', 'RegularExpressionLiteral::RegularExpressionLiteral(')]:
        take(root+'cpp', name, marker)
    for kind in ['ExprNode', 'BoolLiteral', 'NoneLiteral', 'DoubleLiteral', 'IntLiteral', 'StringLiteral',
                 'DateTimeLiteral', 'RegularExpressionLiteral', 'ArrayLiteral', 'BinaryOpNode']:
        take(root+'h', kind+'.inc', 'struct '+kind+('\n' if kind == 'ExprNode' else ' : public ExprNode'))
    take(poco+'include/Poco/RegularExpression.h', 'options.inc', 'enum Options')
    take(poco+'include/Poco/RegularExpression.h', 'match-type.inc', 'struct Match')
    for name, marker in [
        ('match-data.inc', '\tclass MatchData'), ('poco-ctor.inc', 'RegularExpression::RegularExpression('),
        ('poco-dtor.inc', 'RegularExpression::~RegularExpression('),
        ('poco-match.inc', 'int RegularExpression::match(const std::string& subject, std::string::size_type offset, Match& mtch'),
        ('poco-full.inc', 'bool RegularExpression::match(const std::string& subject, std::string::size_type offset) const'),
        ('poco-compile-options.inc', 'int RegularExpression::compileOptions('),
        ('poco-match-options.inc', 'int RegularExpression::matchOptions(')]:
        take(poco+'src/RegularExpression.cpp', name, marker)
    for path, content in data.items():
        if path.startswith('Externals/poco/dependencies/pcre2/src/'):
            dest = output / path; dest.parent.mkdir(parents=True, exist_ok=True); dest.write_bytes(content)
    content_source = BASE/'content-source.zip'
    assert sha(content_source.read_bytes()) == CONTENT_SOURCE_SHA
    with zipfile.ZipFile(content_source) as z:
        assert z.testzip() is None
        data.update({n[9:]: z.read(n) for n in z.namelist() if n.startswith('original/')})
    take('Src/FilterEngine/FileContentRef.cpp', 'content-search.inc', 'bool FileContentRef::REContains(')
    take('Src/Common/UniFile.cpp', 'read-line.inc', 'bool UniMemFile::ReadString(String & line, String & eol, bool * lossy)')
    for name, marker in [('utf8-len.inc', 'int Utf8len_fromLeadByte('), ('utf8-char.inc', 'unsigned GetUtf8Char('), ('make-char.inc', 'void maketchar(String & ch, unsigned unich, bool & lossy, unsigned codepage)')]:
        take('Src/Common/unicoder.cpp', name, marker)
    header = BASE.parent / 'LineTransformArrays/original/FilterParser.h'
    (output/'FilterParser.h').write_bytes(header.read_bytes())
    return dict(sourceZipSha256=SOURCE_SHA, contentSourceZipSha256=CONTENT_SOURCE_SHA, parserHeaderSha256=sha(header.read_bytes()), spans=spans)

def main():
    ap = argparse.ArgumentParser(); ap.add_argument('--output', type=pathlib.Path, required=True)
    ap.add_argument('--compiler', required=True); ap.add_argument('--vcvars', required=True)
    ap.add_argument('--initial-observation', action='store_true'); args = ap.parse_args()
    output = args.output.absolute()
    for path in [output, *output.parents]: assert not path.is_symlink() and not path.is_junction()
    assert output.is_relative_to(pathlib.Path(os.environ['TEMP']).resolve())
    assert not output.is_relative_to(REPO) and not output.is_relative_to(pathlib.Path.home()/'.codex')
    if os.environ.get('CODEX_HOME'): assert not output.is_relative_to(pathlib.Path(os.environ['CODEX_HOME']).resolve())
    assert not output.exists(); output.mkdir(parents=True)
    provenance = extract_sources(output)
    (output/'provenance.json').write_text(json.dumps(provenance, indent=2)+'\n', encoding='utf-8')
    (output/'adapter.cpp').write_bytes((BASE/'adapter.cpp').read_bytes())
    cases = json.loads((BASE/'cases.json').read_text(encoding='utf-8'))
    calls = []
    for case in cases:
        calls.append('run('+json.dumps(case['id'])+','+str(case['optimize']).lower()+','+
                     str(case['caseSensitive']).lower()+','+str(case['negate']).lower()+','+
                     node(case['left'])+','+node(case['right'])+');')
    (output/'cases.inc').write_text('\n'.join(calls)+'\n', encoding='utf-8')
    cdir = output/'Externals/poco/dependencies/pcre2/src'
    def rsp(name, items):
        (output/name).write_text('\n'.join('"'+str(x)+'"' for x in items), encoding='utf-8')
    rsp('c.rsp', ['/nologo', '/c', '/O2', '/DHAVE_CONFIG_H', '/DPCRE2_STATIC', '/I'+str(cdir), cdir/'pcre2.c'])
    rsp('cpp.rsp', ['/nologo', '/utf-8', '/std:c++20', '/EHsc', '/O2', '/DPCRE2_STATIC',
                    '/I'+str(cdir), '/Fe:reference.exe', 'adapter.cpp', 'pcre2.obj'])
    for value in [args.compiler, args.vcvars]: assert not any(x in value for x in '\r\n"%')
    (output/'compile.cmd').write_text('@echo off\ncall "'+args.vcvars+'" >nul\nif errorlevel 1 exit /b %errorlevel%\n"'+args.compiler+'" @c.rsp\nif errorlevel 1 exit /b %errorlevel%\n"'+args.compiler+'" @cpp.rsp\nexit /b %errorlevel%\n')
    compiled = subprocess.run(['cmd', '/c', str(output/'compile.cmd')], cwd=output, capture_output=True, timeout=240)
    (output/'compile.stdout').write_bytes(compiled.stdout); (output/'compile.stderr').write_bytes(compiled.stderr)
    assert compiled.returncode == 0, compiled.stdout.decode(errors='replace')[-8000:]
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    proc = subprocess.Popen([str(output/'reference.exe')], cwd=output, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    class FT(ctypes.Structure): _fields_ = [('low', ctypes.c_uint32), ('high', ctypes.c_uint32)]
    times = [FT() for _ in range(4)]; handle = ctypes.c_void_p(int(proc._handle))
    assert ctypes.windll.kernel32.GetProcessTimes(handle, *[ctypes.byref(t) for t in times])
    timed = False
    try: stdout, stderr = proc.communicate(timeout=45)
    except subprocess.TimeoutExpired:
        timed = True; proc.kill(); stdout, stderr = proc.communicate(timeout=10)
    signaled = ctypes.windll.kernel32.WaitForSingleObject(handle, 0) == 0
    receipt = dict(pid=proc.pid, birthFileTime=(times[0].high<<32)|times[0].low, heldProcessHandleSignaled=signaled,
        startedUtc=started, exitCode=proc.returncode, timeout=timed, stdoutEof=True, stderrEof=True,
        stdoutSha256=sha(stdout), stderrSha256=sha(stderr), compilerSha256=sha(pathlib.Path(args.compiler).read_bytes()),
        executableSha256=sha((output/'reference.exe').read_bytes()), caseCount=len(cases))
    (output/'receipt.json').write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8')
    (output/'golden.ndjson').write_bytes(stdout); (output/'reference.stderr').write_bytes(stderr)
    assert proc.returncode == 0 and signaled and not timed and not stderr
    assert [json.loads(line)['id'] for line in stdout.splitlines()] == [c['id'] for c in cases]
    if not args.initial_observation: assert stdout == (BASE/'golden.ndjson').read_bytes()
    print(json.dumps(receipt))

if __name__ == '__main__': main()
