"""独立reader。製品・captureをimportせず、固定原本/採取証拠/実file全hashを照合する。"""
import sys
sys.dont_write_bytecode = True
import argparse, hashlib, json, pathlib, zipfile
BASE=pathlib.Path(__file__).resolve().parent
def sha(b): return hashlib.sha256(b).hexdigest()
def readj(p): return json.loads(p.read_bytes())
def none(): return {'type':'none'}
def integer(x): return {'type':'integer','value':str(x)}
def string(x): return {'type':'string','value':x}
def array(x): return {'type':'array','value':x}
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--actual',type=pathlib.Path);a=ap.parse_args()
    fixed=readj(BASE/'fixed-manifest.json')
    for n,s in fixed['files'].items():
        b=(BASE/n).read_bytes();assert len(b)==s['size'] and sha(b)==s['sha256'], n
    source=readj(BASE/'source-manifest.json');assert source['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
    data={}
    with zipfile.ZipFile(BASE/'reference-source.zip') as z:
        assert z.testzip() is None
        assert set(z.namelist())=={'original/'+s['path'] for s in source['sources']}
        for s in source['sources']:
            b=z.read('original/'+s['path']);assert len(b)==s['size'] and sha(b)==s['sha256']
            assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['blob'];data[s['path']]=b
    extracted={}
    for s in readj(BASE/'provenance.json'):
        b=data[s['path']];v=b[s['startByte']:s['endByte']]
        assert sha(v)==s['sha256']
        assert b[:s['startByte']].count(b'\n')+1==s['startLine'] and b[:s['endByte']].count(b'\n')+1==s['endLine']
        extracted.setdefault(s['output'],[]).append(v+b'\n')
    golden=(BASE/'golden.ndjson').read_bytes();rows=[json.loads(x) for x in golden.splitlines()];by={r['id']:r for r in rows}
    assert len(by)==len(rows)==148
    assert [r['id'] for r in readj(BASE/'case-plan.json')]==[r['id'] for r in rows]
    with zipfile.ZipFile(BASE/'native-proof.zip') as z:
        assert z.testzip() is None
        for run in ['run1','run2']:
            def zb(n):return z.read(run+'/'+n)
            assert zb('golden.ndjson')==golden and not zb('reference.stderr') and not zb('compile.stderr')
            receipt=json.loads(zb('receipt.json'));compile_receipt=json.loads(zb('compile-receipt.json'))
            assert receipt['exitCode']==compile_receipt['exitCode']==0 and receipt['heldProcessHandleSignaled']
            assert receipt['stdoutEof'] and receipt['stderrEof'] and receipt['stdoutSha256']==sha(golden)
            assert receipt['adapterSha256']==sha((BASE/'adapter.cpp').read_bytes())
            assert zb('adapter.cpp')==(BASE/'adapter.cpp').read_bytes()
            for n,pieces in extracted.items(): assert zb(n)==b''.join(pieces),n
            for n,digest in compile_receipt['inputSha256'].items(): assert sha(zb(n))==digest,n
    if a.actual: assert a.actual.read_bytes()==golden, 'actual differs from fixed native stdout'
    def value(id,expected):assert by[id]['value']==expected,(id,by[id]['value'],expected)
    # 原関数とは別実装のreaderで入力全bytesと各アルゴリズムを照合する。
    input_manifest=readj(BASE/'input-manifest.json')
    with zipfile.ZipFile(BASE/'input.zip') as z:
        assert z.testzip() is None and set(z.namelist())=={x['name'] for x in input_manifest['files']}
        for item in input_manifest['files']:
            b=z.read(item['name']);assert len(b)==item['size'] and sha(b)==item['sha256']
            if item['name']=='cache.bin':continue
            name=item['name'];prefix='file-'+name+'-mixed-'
            value(prefix+'System.Size',integer(len(b)));value(prefix+'System.FileName',string(name));value(prefix+'System.FileExtension',string('.bin'))
            value(prefix+'System.DateModified',{'type':'timestamp-us','value':'1700000000123456'})
            for algorithm in ['MD5','SHA1','SHA256']:
                h=hashlib.new(algorithm.lower(),b).hexdigest()
                for mode in ['mixed','hashonly']:
                    id='file-'+name+'-'+mode+'-Hash.'+algorithm;value(id,string(h));assert by[id]['vt']==4113
                    assert by[id]['serviceSuccess']==(mode=='mixed')
            assert all(by[prefix+n]['serviceSuccess'] for n in ['System.Size','System.FileName','System.FileExtension','System.DateModified'])
    for name in ['System.Size','System.FileName','System.FileExtension','System.DateModified']:
        id='file-absent.bin-mixed-'+name;value(id,none());assert not by[id]['serviceSuccess']
    for mode in ['mixed','hashonly']:
        for name in ['MD5','SHA1','SHA256']:
            id='file-absent.bin-'+mode+'-Hash.'+name;value(id,string(''));assert not by[id]['serviceSuccess']
    for dirs in [1,2,3]:
        for mask in range(1<<dirs):
            expected=[integer((i+1)*10) if mask&(1<<i) else none() for i in range(dirs)]
            for name,i in [('prop',0),('leftprop',0),('middleprop',1),('rightprop',dirs-1)]:
                v=array(expected) if name=='prop' and dirs>1 else (expected[i] if i<dirs else none())
                value(f'side-{dirs}-{mask}-{name}',v)
    value('side-nested-keywords',array([array([string(n),string('日本語')]) for n in ['left','middle','right']]))
    value('side-unregistered',none())
    for id,x in [('convert-i4',-(1<<31)),('convert-ui4',(1<<32)-1),('convert-i8',-(1<<63)),('convert-ui8-overflow',-1)]:value(id,integer(x))
    value('convert-byte-vector',string(bytes([0,1,15,16,127,128,254,255]).hex()));value('convert-byte-vector-empty',string(''))
    for id in ['convert-empty','convert-r4-unsupported','convert-unsupported-1','convert-unsupported-2','convert-unsupported-8','convert-unsupported-4099']:value(id,none())
    value('convert-wide',string('日本語😀'));value('convert-string-vector',array([string('a'),string('日本語'),string('')]))
    for id in ['truth-scalar-integer','truth-scalar-string','truth-scalar-double','truth-array-integer','truth-nested-string']:assert not by[id]['containsTrue']
    assert by['truth-nested-true']['containsTrue'] and by['truth-nested-true']['evalAsBool'] is False
    for n in ['System.Size','System.FileName','Hash.MD5','Hash.SHA1','Hash.SHA256']:value('canonical-'+n,integer(0))
    for n in ['system.size','System.NotARealProperty.20261017','hash.sha256','Hash.Sha256','']:value('canonical-'+n,integer(-1))
    for n in ['null','zero','two']:assert by['validation-'+n]['error']=='prop function requires 1 argument'
    assert by['validation-nonliteral']['error']=='prop function requires a string literal as argument'
    assert by['validation-invalid']['error']=='Bogus.Property' and by['validation-valid']['error']==''
    for n,x in [('before',3),('after-file-change',3),('after-reset',8)]:value('cache-'+n,integer(x))
    print(json.dumps(dict(status='passed',cases=len(rows),goldenSha256=sha(golden),sourceBlobs=len(data),spanCount=len(readj(BASE/'provenance.json')),independentFileHashes=18)))
if __name__=='__main__':main()
