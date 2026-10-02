"""固定原本観測だけを容量の小さなgoldenへ包装する。製品出力は使用しない。"""
import base64, gzip, hashlib, io, json, sys
from pathlib import Path

HERE=Path(__file__).resolve().parent
LINE=HERE.parent/'ImageLines'
def sha(data):return hashlib.sha256(data).hexdigest().upper()
def pack(path,record):
    raw=(json.dumps(record,ensure_ascii=False,separators=(',',':'))+'\n').encode('utf-8')
    memory=io.BytesIO()
    with gzip.GzipFile(filename='',mode='wb',fileobj=memory,compresslevel=9,mtime=0) as writer:writer.write(raw)
    encoded=memory.getvalue();path.write_bytes(encoded)
    assert gzip.decompress(encoded)==raw
    return {'path':str(path),'unpackedBytes':len(raw),'unpackedSha256':sha(raw),'gzipBytes':len(encoded),'gzipSha256':sha(encoded)}

def main():
    if len(sys.argv)!=3:raise SystemExit('extract.py <DLL run3> <line-script run2>')
    dll_run,line_run=map(Path,sys.argv[1:]);dll_bytes=(dll_run/'observations.json').read_bytes();line_bytes=(line_run/'observations.json').read_bytes()
    assert sha(dll_bytes)=='BA3295C6880776C6663FE2AC3B75135A3902589D6754DAE24C12FBE10E441360'
    assert sha(line_bytes)=='8CD74EC009262E239E78B2C9E28C973E02331A5C6FFEE79395303F051350C13E'
    source=LINE/'reference-source/Diff.hpp'
    assert sha(source.read_bytes())=='DB4938A98F1953DDBA9AA6F1F8A8AFB064C5C133036467379633CC6108A98142'
    revision='da639cdfaeca87aaad0eaceec509afa11ad61421';extractor=sha(Path(__file__).read_bytes())
    dll_summary=json.loads((dll_run/'summary.json').read_bytes());line_summary=json.loads((line_run/'summary.json').read_bytes())
    assert dll_summary['cases']==58 and dll_summary['successfulCases']==58 and dll_summary['states']==304 and dll_summary['failed']==0
    assert line_summary['cases']==14797 and line_summary['failed']==0
    cases=json.loads(dll_bytes)
    for index,case in enumerate(cases):
        folder=dll_run/f'case-{index:03}'
        assert case['exitCode']==0
        for pane,image in enumerate(case['inputs']):
            png=(folder/f'pane{pane}.png').read_bytes();assert sha(png)==image['pngSha256'];image['pngBase64']=base64.b64encode(png).decode('ascii')
        for state in case['states']:
            for pane,image in enumerate(state['panes']):
                png=(folder/f'state{state["stateIndex"]}-pane{pane}-aligned.png').read_bytes()
                assert sha(bytes.fromhex(image['bgraHex']))==image['bgraSha256']
                image['pngBase64']=base64.b64encode(png).decode('ascii');image['pngSha256']=sha(png)
        for pane,image in enumerate(case['exports']):
            png=(folder/f'final-pane{pane}.png').read_bytes();assert sha(png)==image['pngSha256'];image['pngBase64']=base64.b64encode(png).decode('ascii')
    dll_record={'sourceRevision':revision,'dllSha256':dll_summary['dllSha256'],'publicHeaderBlob':'d5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa',
        'observationsSha256':sha(dll_bytes),'captureSourceSha256':dll_summary['generatorSha256'],'probeSourceSha256':dll_summary['probeSourceSha256'],
        'extractorSha256':extractor,'algorithm':'Myers','cases':cases}
    line_record={'sourceRevision':revision,'sourceBlob':'8863642be2c604a0c8df72886f207c83a8e0bf7f','sourceSha256':sha(source.read_bytes()),
        'observationsSha256':sha(line_bytes),'captureSourceSha256':line_summary['generatorSha256'],'probeSourceSha256':line_summary['probeSourceSha256'],
        'extractorSha256':extractor,'algorithm':'Myers','cases':json.loads(line_bytes)}
    results=[pack(HERE/'winimerge-insertions-golden.json.gz',dll_record),pack(LINE/'winimerge-image-lines-golden.json.gz',line_record)]
    print(json.dumps(results))

if __name__=='__main__':main()
