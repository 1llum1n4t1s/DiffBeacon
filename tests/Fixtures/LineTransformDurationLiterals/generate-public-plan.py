"""Join original raw native records to input plans; never compute numeric expected values."""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,collections,argparse,os
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def generate(output):
    output=output.absolute()
    assert not output.exists()
    for p in output.parents:
        assert p.name.lower()!='artifacts' and not p.name.startswith('.') and not p.is_symlink() and not p.is_junction()
        if p.exists():assert not p.stat().st_file_attributes&1024
    for p in (pathlib.Path(r'C:\Users\IMT\dev\DiffBeacon'),pathlib.Path.home()/'.codex'):
        assert not output.is_relative_to(p)
    if os.environ.get('CODEX_HOME'):assert not output.is_relative_to(pathlib.Path(os.environ['CODEX_HOME']))
    golden=(BASE/'golden.ndjson').read_bytes();cases=json.loads((BASE/'cases.json').read_bytes());lines=golden.splitlines();rows=[json.loads(x) for x in lines]
    assert len(rows)==len(cases) and [r['id'] for r in rows]==[c['id'] for c in cases]
    planned=[]
    for n,(case,row,line) in enumerate(zip(cases,rows,lines),1):
        mapped='planned-public-serialized-bridge' if case['classification'].startswith('safe-') else case['classification']
        planned.append(dict(id=case['id'],classification=mapped,expression=case['expression'] if mapped=='planned-public-serialized-bridge' else None,nativeCase=case,nativeReference=dict(file='golden.ndjson',fileSha256=sha(golden),lineNumber=n,lineSha256WithoutTerminator=sha(line),lineUtf8HexWithoutTerminator=line.hex(),record=row),producerCompiled=False,executed=False,parserEquivalent=False,directiveEquivalent=False,typedPublicValueObserved=False,contextEquivalent=False,qualification='Future CSharp Compile/Transform serialized output bytes only; original whole parser/directive not executed. Native kind/bits remain original node observations.'))
    result=dict(goldenSha256=sha(golden),casePlanSha256=sha((BASE/'cases.json').read_bytes()),cases=planned,counts=dict(collections.Counter(x['classification'] for x in planned)),producerCompiled=False,executed=False,parserEquivalent=False,directiveEquivalent=False,typedPublicValueObserved=False,contextEquivalent=False)
    output.parent.mkdir(parents=True,exist_ok=True);output.write_bytes((json.dumps(result,indent=2,ensure_ascii=False)+'\n').encode('utf-8'))
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);a=p.parse_args();generate(a.output)
