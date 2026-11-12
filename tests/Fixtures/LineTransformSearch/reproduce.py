"""固定Git blobから無改変関数を切り出し、外部出力先でC++原典を実行する。"""
import argparse, ctypes, datetime, hashlib, json, os, pathlib, shutil, subprocess

ROOT = pathlib.Path(__file__).resolve().parent

def literal(text):
    return 'bytes({' + ','.join(str(x) for x in text.encode('utf-8')) + '})'

def ast(n):
    return 'node(' + ','.join([json.dumps(n['op']),literal(n.get('text','')),str(n.get('integer',0)),str(n.get('side',0)),'{'+','.join(ast(x) for x in n.get('children',[]))+'}']) + ')'

def build(output, compiler):
    output = pathlib.Path(os.path.abspath(output))
    for ancestor in [output, *output.parents]:
        if ancestor.is_symlink() or ancestor.is_junction():
            raise ValueError('output ancestor is a link')
    output = output.resolve()
    repo = ROOT.parents[2]
    codex = pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex'))).resolve()
    for prohibited in [repo,codex]:
        if output.is_relative_to(prohibited): raise ValueError('output must be outside repository and CODEX_HOME')
    if any(p.name.lower()=='artifacts' for p in [output,*output.parents]): raise ValueError('forbidden output directory name')
    for p in [output,*output.parents]:
        if p.exists() and (p.is_symlink() or p.is_junction()): raise ValueError('output ancestor is a link')
    output.mkdir(parents=True,exist_ok=True)
    provenance=json.loads((ROOT/'provenance.json').read_text())
    for source in provenance['sources']:
        data=(ROOT/source['file']).read_bytes()
        assert len(data)==source['size'] and hashlib.sha256(data).hexdigest()==source['sha256'],source['file']
    specs={
      'provider.inc':('FileTransform.cpp',[(790,920)]),
      'to-string.inc':('FilterExpressionNodes.cpp',[(2315,2357)]),
      'convert-string.inc':('FilterExpression.cpp',[(422,427)]),
      'bool.inc':('FilterExpressionNodes.cpp',[(34,49)]),
      'if.inc':('FilterExpressionNodes.cpp',[(2433,2442)]),
      'range-parse.inc':('FilterExpressionNodes.cpp',[(3905,3975)]),
      'range-evaluate.inc':('FilterExpressionNodes.cpp',[(3992,4050)]),
      'fields.inc':('FilterExpressionNodes.cpp',[(1247,1255),(1267,1274),(1425,1433),(1443,1448)]),
      'line-at.inc':('FilterExpressionNodes.cpp',[(3877,3903)]),
      'trim.inc':('FilterExpressionNodes.cpp',[(2862,2881)]),
       'array-functions.inc':('FilterExpressionNodes.cpp',[(1720,1835)]),
      'lifting.inc':('FilterExpressionNodes.cpp',[(1693,1704)]),
      'to-int.inc':('FilterExpressionNodes.cpp',[(2520,2530),(2576,2607)]),
      'len.inc':('FilterExpressionNodes.cpp',[(1851,1869)]),
      'get-string.inc':('FilterExpressionNodes.cpp',[(51,58)]),
      'compute.inc':('FilterExpressionNodes.cpp',[(581,845)]),
      'choices.inc':('FilterExpressionNodes.cpp',[(2443,2518)]),
      'logical.inc':('FilterExpressionNodes.cpp',[(2662,2752)]),
      'utf8.inc':('unicoder.cpp',[(112,123),(145,156)]),
      'search.inc':('FilterExpressionNodes.cpp',[(2923,3031)]),
      'poco-ascii.inc':('Poco-Ascii.h',[(24,226)]),
      'poco-table.inc':('Poco-Ascii.cpp',[(18,154)]),
      'poco-lower.inc':('Poco-String.h',[(205,226)]),
      'apply.inc':('FileTransform.cpp',[(1051,1075)])}
    for name,(source,ranges) in specs.items():
        lines=(ROOT/'original'/source).read_bytes().splitlines(keepends=True)
        (output/name).write_bytes(b''.join(b''.join(lines[a-1:b]) for a,b in ranges))
    cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'))
    generated=[]
    for c in cases:
        groups=[]
        for g in c['groups']:groups.append('{'+str(g.get('caseSensitive',True)).lower()+','+str(g['mask'])+', {'+','.join(ast(x) for x in g['ast'])+'}}')
        generated.append('runCase('+json.dumps(c['id'])+','+str(c['table']).lower()+','+str(ord(c['delimiter']))+','+str(ord(c['quote']))+','+str(c['pane'])+',{'+','.join(literal(x) for x in c['lines'])+'},{'+','.join(groups)+'});')
    (output/'cases.inc').write_text('\n'.join(generated)+'\n',encoding='utf-8')
    executable=output/'reference.exe'
    compiler_path=pathlib.Path(shutil.which(compiler) or compiler).resolve(strict=True)
    command=[str(compiler_path),'/nologo','/std:c++20','/EHsc','/utf-8','/I'+str(output),'/I'+str(ROOT/'original'),str(ROOT/'adapter.cpp'),'/Fo'+str(output/'adapter.obj'),'/Fe'+str(executable)]
    compiled=subprocess.run(command,cwd=output,capture_output=True)
    (output/'compile.stdout').write_bytes(compiled.stdout);(output/'compile.stderr').write_bytes(compiled.stderr)
    if compiled.returncode:raise RuntimeError(compiled.stdout.decode(errors='replace')+compiled.stderr.decode(errors='replace'))
    started=datetime.datetime.now(datetime.timezone.utc).isoformat()
    p=subprocess.Popen([str(executable)],cwd=output,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    birth=None
    if os.name=='nt':
        class FILETIME(ctypes.Structure):_fields_=[('low',ctypes.c_uint32),('high',ctypes.c_uint32)]
        times=[FILETIME() for _ in range(4)]
        ok=ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(p._handle)),*[ctypes.byref(t) for t in times])
        if ok:birth=(times[0].high<<32)|times[0].low
    stdout,stderr=p.communicate()
    terminalHeld=False
    if os.name=='nt':
        terminalHeld=ctypes.windll.kernel32.WaitForSingleObject(ctypes.c_void_p(int(p._handle)),0)==0
        assert terminalHeld, 'held process handle must be signaled after exit/pipe EOF'

    (output/'reference.stdout.ndjson').write_bytes(stdout);(output/'reference.stderr').write_bytes(stderr)
    receipt=dict(command=command,compilerSha256=hashlib.sha256(compiler_path.read_bytes()).hexdigest(),sourceCommit=provenance['commit'],sourceBlobs={s['path']:s['blob'] for s in provenance['sources']},pid=p.pid,birthFileTime=birth,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),exitCode=p.returncode,stdoutEof=True,stderrEof=True,stdoutSha256=hashlib.sha256(stdout).hexdigest(),stderrSha256=hashlib.sha256(stderr).hexdigest(),caseCount=len(cases),heldProcessHandleSignaled=terminalHeld,extraction=specs)
    (output/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
    assert p.returncode==0 and not stderr
    actual=[json.loads(x) for x in stdout.splitlines()]
    assert [x['id'] for x in actual]==[x['id'] for x in cases]
    print(json.dumps(dict(caseCount=len(cases),stdout=str(output/'reference.stdout.ndjson'),receipt=str(output/'receipt.json'))))

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);parser.add_argument('--compiler',default='cl');args=parser.parse_args();build(args.output,args.compiler)
