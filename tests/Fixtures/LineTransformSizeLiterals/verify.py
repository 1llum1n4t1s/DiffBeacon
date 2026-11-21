"""Independent original-byte, lexical-span, process and stdout reader; no producer/math golden import."""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,argparse,re,collections
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def block(raw,marker,semi):
    begin=raw.index(marker.encode());i=raw.index(b'{',begin);depth=0;quote=None
    while i<len(raw):
        if quote:
            if raw[i]==92:i+=2;continue
            if raw[i]==quote:quote=None
        elif raw[i:i+2]==b'//':
            end=raw.find(b'\n',i);i=len(raw) if end<0 else end;continue
        elif raw[i:i+2]==b'/*':i=raw.index(b'*/',i+2)+2;continue
        elif raw[i] in (34,39):quote=raw[i]
        elif raw[i]==123:depth+=1
        elif raw[i]==125:
            depth-=1
            if not depth:
                end=i+1
                if semi:assert raw[end:end+1]==b';';end+=1
                return begin,end
        i+=1
    raise AssertionError(marker)
def verify(reference,actual1=None,actual2=None):
    fixed=json.loads((BASE/'fixed-manifest.json').read_bytes())
    assert {p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file()}=={r['path'] for r in fixed['files']}|{'fixed-manifest.json'}
    for r in fixed['files']:
        b=(BASE/r['path']).read_bytes();assert len(b)==r['size'] and sha(b)==r['sha256'],r['path']
    source=json.loads((BASE/'source-manifest.json').read_bytes());assert sha(reference.read_bytes())==source['referenceArchiveSha256'];raws={}
    with zipfile.ZipFile(reference) as z:
        assert z.testzip() is None
        assert sha(z.read('original/LICENSE.md'))=='aef8b4222b79d0dbf6bf17cfff71c90a6a6bb8917a4162abe417b469ed22da2e'
        assert 'original/Externals/poco/LICENSE' in z.namelist()
        for s in source['sources']:
            b=z.read('original/'+s['path']) if s['origin']=='sibling-fixed-reference-zip' else (BASE/'additional-original'/s['path']).read_bytes()
            assert len(b)==s['size'] and sha(b)==s['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s.get('blob',s.get('gitBlob'));raws[s['path']]=b
    spans=json.loads((BASE/'extractions.json').read_bytes())
    for e in spans:
        raw=raws[e['path']]
        if e.get('wholeFile'):a,b=0,len(raw)
        elif e.get('kind')=='registration':
            candidates=[('{"'+name.lower()+'", '+name+'Func, 1, 2}').encode() for name in ('Average','Sum','Maximum','Minimum')]
            fragment=next(v for v in candidates if sha(v)==e['sha256']);assert raw.count(fragment)==1;a=raw.index(fragment);b=a+len(fragment)
        else:a,b=block(raw,e['marker'],e.get('semicolon',False))
        assert (a,b)==(e['startByte'],e['endByte']) and sha(raw[a:b])==e['sha256']
    lexer=raws['Src/FilterEngine/FilterLexer.re'];parser=raws['Src/FilterEngine/FilterParser.y']
    assert b're2c:flags:case-insensitive = 1;' in lexer and b'([0-9]+([.][0-9]+)?)("B"|"KB"|"MB"|"GB"|"TB")' in lexer
    assert b'new SizeLiteral(B.string)' in parser and b'new NegateNode(B.node)' in parser
    manifest=json.loads((BASE/'native-proof-manifest.json').read_bytes());golden=(BASE/'golden.ndjson').read_bytes();cases=json.loads((BASE/'cases.json').read_bytes());rows=[json.loads(l) for l in golden.splitlines()]
    assert len(rows)==len(cases)==95 and len({r['id'] for r in rows})==95 and [r['id'] for r in rows]==[c['id'] for c in cases]
    births=[];pids=[]
    with zipfile.ZipFile(BASE/'native-proof.zip') as z:
        assert z.testzip() is None and set(z.namelist())=={r['path'] for r in manifest}
        for r in manifest:
            b=z.read(r['path']);i=z.getinfo(r['path']);assert i.file_size==r['size'] and i.CRC==int(r['crc32'],16) and sha(b)==r['sha256']
        for run in ('final-run1','final-run2'):
            assert z.read(run+'/golden.ndjson')==z.read(run+'/native.stdout.raw')==golden and not z.read(run+'/native.stderr.raw')
            proof=json.loads(z.read(run+'/proof.json'));native=proof['native'];compile=proof['compile']
            for rec in (native,compile):assert rec['exitCode']==0 and rec['heldSignaled'] and rec['stdoutEof'] and rec['stderrEof'] and not rec['timeout'] and rec['safeHandleType']=='Handle'
            assert native['command'][0].endswith('reference.exe') and sha(z.read(run+'/reference.exe'))==proof['executableSha256']
            assert proof['casePlanSha256']==sha((BASE/'cases.json').read_bytes()) and proof['adapterSha256']==sha((BASE/'adapter.cpp').read_bytes())
            for name,digest in proof['inputSha256'].items():assert sha(z.read(run+'/'+name))==digest
            for e in spans:
                if e['file']:assert z.read(run+'/'+e['file'])==raws[e['path']][e['startByte']:e['endByte']]+(b'\n' if e.get('outerLf') else b'')
            births.append(native['birth100ns']);pids.append(native['pid'])
    assert len(set(births))==len(set(pids))==2
    for c,r in zip(cases,rows):
        assert r['mode']==c['mode'] and r['classification']==c['classification'] and r['inputUtf8Hex']==c['input'].encode().hex() and r['optimizerInvoked']==c['optimize'] and r['returned'] is True and r['parserExecuted'] is False and r['directiveExecuted'] is False
        assert re.fullmatch(r'(?:[0-9a-f]{2})*',r['serializedUtf8Hex'])
        if r['value']['kind']=='integer':assert r['serializedUtf8Hex']==r['value']['decimal'].encode().hex()
        if c['mode']=='array':assert r['rawStodProbe'] is None
        else:
            probe=r['rawStodProbe'];prefix=re.match(r'[0-9.]*',c['input']).group();assert probe['numberPartUtf8Hex']==prefix.encode().hex()
            assert probe['status'] in ('returned','invalid_argument','out_of_range')
            if probe['status']=='returned':assert re.fullmatch('[0-9a-f]{16}',probe['doubleBitsHex']) and 0<probe['consumedCharacters']<=len(prefix)
            else:assert probe['doubleBitsHex'] is None and probe['consumedCharacters'] is None
    under=next(r for r in rows if r['id']=='constructor-stod-underflow');assert under['rawStodProbe']['status']=='out_of_range' and under['value']=={'kind':'integer','decimal':'0'}
    public=json.loads((BASE/'public-case-plan.json').read_bytes());assert public['goldenSha256']==sha(golden) and len(public['cases'])==95
    for n,(item,c,line,r) in enumerate(zip(public['cases'],cases,golden.splitlines(),rows),1):
        ref=item['nativeReference'];assert item['id']==c['id'] and item['nativeCase']==c and ref['record']==r and ref['file']=='golden.ndjson' and ref['fileSha256']==sha(golden) and ref['lineNumber']==n and ref['lineSha256WithoutTerminator']==sha(line)
        assert item['producerCompiled'] is False and item['executed'] is False and item['parserEquivalent'] is False and item['directiveEquivalent'] is False and item['typedPublicValueObserved'] is False
        assert item['expression']==(c['expression'] if c['classification']=='safe-node-observation' else None)
    assert dict(collections.Counter(p['classification'] for p in public['cases']))==public['counts']=={'planned-public-serialized-bridge':77,'constructor-only-unsupported-public':14,'defined-cast-platform-rounding-observation':4}
    for actual in (actual1,actual2):
        if actual:assert actual.read_bytes()==golden
    return {'passed':True,'nativeCases':95,'publicPlanned':77,'constructorOnly':14,'platformRounding':4,'sourceBlobs':len(raws),'exactSourceSpans':len(spans),'nativeProcesses':2,'compileRuns':2,'nativeStdoutSha256':sha(golden),'proofZipSha256':sha((BASE/'native-proof.zip').read_bytes()),'parserExecuted':False,'directiveExecuted':False,'publicProducerCompiled':False}
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--reference-zip',type=pathlib.Path,required=True);p.add_argument('--actual1',type=pathlib.Path);p.add_argument('--actual2',type=pathlib.Path);a=p.parse_args();print(json.dumps(verify(a.reference_zip,a.actual1,a.actual2)))
