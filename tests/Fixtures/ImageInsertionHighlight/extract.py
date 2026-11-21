"""固定配布DLLの二回の実測だけをgoldenへ包装する。製品出力を使用しない。"""
import base64, gzip, hashlib, io, json, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
OBSERVATIONS_SHA = 'AC8E457D3D718B63663CCA99E70E619F7F4446C783C9DD880B3884F978833674'
def sha(data): return hashlib.sha256(data).hexdigest().upper()

def main():
    if len(sys.argv) != 3: raise SystemExit('extract.py <original run4> <original run5>')
    previous, latest = map(Path, sys.argv[1:])
    observations = (latest/'observations.json').read_bytes()
    assert observations == (previous/'observations.json').read_bytes()
    assert sha(observations) == OBSERVATIONS_SHA
    summary = json.loads((latest/'summary.json').read_text('utf-8'))
    assert summary['cases'] == 12 and summary['states'] == 146 and summary['failed'] == 0
    assert summary['passed'] == 670 and summary['successfulCases'] == 12
    cases = json.loads(observations)
    checked = 0
    for index, case in enumerate(cases):
        folder = latest/f'case-{index:03}'
        before = previous/f'case-{index:03}'
        for path in sorted(folder.iterdir()):
            assert path.is_file() and not path.is_symlink()
            assert path.read_bytes() == (before/path.name).read_bytes()
            checked += 1
        for pane, image in enumerate(case['inputs']):
            png = (folder/f'pane{pane}.png').read_bytes()
            assert sha(png) == image['pngSha256']
            image['pngBase64'] = base64.b64encode(png).decode('ascii')
        for state in case['states']:
            for pane, image in enumerate(state['panes']):
                png = (folder/f'state{state["stateIndex"]}-pane{pane}-aligned.png').read_bytes()
                assert sha(bytes.fromhex(image['bgraHex'])) == image['bgraSha256']
                assert sha(bytes.fromhex(image['alignedRawBgraHex'])) == image['alignedRawBgraSha256']
                image['pngSha256'] = sha(png)
                image['pngBase64'] = base64.b64encode(png).decode('ascii')
        for pane, image in enumerate(case['exports']):
            png = (folder/f'final-pane{pane}.png').read_bytes()
            assert sha(png) == image['pngSha256']
            image['pngBase64'] = base64.b64encode(png).decode('ascii')
    source = HERE.parent/'ImageRegions/reference-source/ImgDiffBuffer.hpp'
    record = {
        'schemaVersion': 1,
        'sourceRevision': 'da639cdfaeca87aaad0eaceec509afa11ad61421',
        'sourceSha256': sha(source.read_bytes()),
        'sourcePath': 'Src/ImgDiffBuffer.hpp',
        'sourceLines': '507-510,563-566,1888-1964',
        'dllSha256': summary['dllSha256'],
        'publicHeaderSha256': '597902D5FFBE8585524C86C41E73032C607E42E04E65F90F0441E521F84DB4F4',
        'publicHeaderBlob': 'd5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa',
        'observationsSha256': sha(observations),
        'captureSourceSha256': summary['generatorSha256'],
        'probeSourceSha256': summary['probeSourceSha256'],
        'extractorSha256': sha(Path(__file__).read_bytes()),
        'license': 'GPL-2.0-or-later; synthetic input PNGs CC0-1.0',
        'algorithm': 'Myers',
        'repeatEvidence': {'runs': 2, 'identicalCaseFiles': checked},
        'cases': cases,
    }
    raw = (json.dumps(record, ensure_ascii=False, separators=(',', ':'))+'\n').encode('utf-8')
    stream = io.BytesIO()
    with gzip.GzipFile(filename='', mode='wb', fileobj=stream, compresslevel=9, mtime=0) as writer:
        writer.write(raw)
    encoded = stream.getvalue()
    assert gzip.decompress(encoded) == raw
    (HERE/'winimerge-insertion-highlight-golden.json.gz').write_bytes(encoded)
    print(json.dumps({'gzipSha256': sha(encoded), 'gzipBytes': len(encoded),
        'unpackedSha256': sha(raw), 'unpackedBytes': len(raw), 'repeatFiles': checked}))

if __name__ == '__main__': main()
