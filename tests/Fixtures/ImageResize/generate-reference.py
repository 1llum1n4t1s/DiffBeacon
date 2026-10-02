"""原本Resize/変換呼出しの無改変C++採取。入力CC0、抽出核GPL-2.0-or-later。"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parent
EXPECTED = {
    'ImgMergeBuffer.hpp': '956B69A5D76D6918CD6D3245B5DB5BFDEF902C82221E2539FFEA1B33996AE40B',
    'ImgDiffBuffer.hpp': '7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28',
}


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def extract(source, marker):
    start = source.index(marker)
    brace = source.index(b'{', start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == 123) - (source[end] == 125)
        end += 1
    if marker.startswith(b'\tclass '):
        assert source[end:end+1] == b';'
        end += 1
    assert source[end:end+2] == b'\r\n'
    return source[start:end+2], start, end+2


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--record', action='store_true', help='初回採取のみ。固定fixtureを生成する')
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    if any(out.iterdir()):
        raise ValueError('採取先は空の専用ディレクトリが必要です')
    merge = (ROOT.parent / 'ImageCopy/reference-source/ImgMergeBuffer.hpp').read_bytes()
    diff = (ROOT.parent / 'ImageRegions/reference-source/ImgDiffBuffer.hpp').read_bytes()
    assert sha(merge) == EXPECTED['ImgMergeBuffer.hpp']
    assert sha(diff) == EXPECTED['ImgDiffBuffer.hpp']
    fragments, spans = [], []
    for name, source, marker in [
        ('Resize', merge, b'\tbool Resize(int pane, int width, int height)'),
        ('PasteImageInternal', merge, b'\tvoid PasteImageInternal(int pane, int x, int y, const Image& image)'),
        ('TemporaryTransformation', diff, b'\tclass TemporaryTransformation\r\n'),
        ('TransformImages', diff, b'\tvoid TransformImages(bool reverse)'),
    ]:
        raw, start, end = extract(source, marker)
        fragments.append(raw)
        spans.append(dict(name=name, source='ImgMergeBuffer.hpp' if source is merge else 'ImgDiffBuffer.hpp',
                          startByte=start, endByteExclusive=end, sha256=sha(raw)))
    prefix = (ROOT / 'probe-prefix.cpp').read_bytes()
    suffix = (ROOT / 'probe-suffix.cpp').read_bytes()
    probe = prefix + b''.join(fragments) + suffix
    (out / 'probe.cpp').write_bytes(probe)
    cases = []
    for rotation in [0, 90, 180, 270]:
        for flip_x in [False, True]:
            for flip_y in [False, True]:
                for width, height in [(3, 2), (4, 5), (2, 3), (1, 1), (6, 2), (3, 6)]:
                    pixels = [v for i in range(6) for v in [(17+i*29)%256, (31+i*37)%256, (73+i*41)%256, 0 if i%2==0 else 255]]
                    cases.append(dict(id=f'r{rotation}-x{int(flip_x)}-y{int(flip_y)}-{width}x{height}',
                                      rotation=rotation, flipHorizontal=flip_x, flipVertical=flip_y,
                                      target=dict(width=3, height=2, bgra=pixels), width=width, height=height))
    if not args.record:
        assert cases == json.loads((ROOT / 'cases.json').read_bytes())['cases']
        pinned = json.loads((ROOT / 'source-manifest.json').read_bytes())
        assert pinned['extraction'] == spans and pinned['probeSha256'] == sha(probe)
        assert pinned['prefixSha256'] == sha(prefix) and pinned['suffixSha256'] == sha(suffix)
    protocol = [str(len(cases))]
    for case in cases:
        protocol += [case['id'], str(case['rotation']), str(int(case['flipHorizontal'])), str(int(case['flipVertical'])),
                     str(case['width']), str(case['height']), *map(str, case['target']['bgra'])]
    protocol = (' '.join(protocol) + '\n').encode()
    (out / 'input.txt').write_bytes(protocol)
    compiler = shutil.which('cl')
    if not compiler:
        raise ValueError('MSVC x64 Native Tools環境が必要です')
    command = [compiler, '/nologo', '/std:c++17', '/EHsc', '/MT', '/utf-8', '/W4', '/Od',
               '/Fe' + str(out / 'probe.exe'), '/Fo' + str(out / 'probe.obj'), str(out / 'probe.cpp')]
    result = subprocess.run(command, cwd=out, capture_output=True)
    (out / 'compiler.log').write_bytes(result.stdout + result.stderr)
    write(out / 'compiler.json', dict(command=command, exit=result.returncode))
    assert result.returncode == 0
    runs, stdout = [], []
    for n in [1, 2]:
        process = subprocess.Popen([str(out / 'probe.exe')], cwd=out, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        observed, errors = process.communicate(protocol, timeout=20)
        (out / f'run{n}.txt').write_bytes(observed)
        (out / f'run{n}-stderr.log').write_bytes(errors)
        assert process.returncode == 0 and not errors
        runs.append(dict(run=n, pid=process.pid, exit=process.returncode, stdoutSha256=sha(observed)))
        stdout.append(observed)
    assert stdout[0] == stdout[1]
    observations = []
    for case, line in zip(cases, stdout[0].decode().splitlines(), strict=True):
        ident, changed, history, compares, width, height, raw = line.split()
        assert ident == case['id']
        assert len(bytes.fromhex(raw)) == int(width)*int(height)*4
        observations.append(dict(id=ident, result=bool(int(changed)), historyPushes=int(history), compareCalls=int(compares),
                                 width=int(width), height=int(height), bgraHex=raw))
    write(out / 'observations.json', observations)
    observed = (out / 'observations.json').read_bytes()
    manifest = dict(sourceHashes=EXPECTED, extraction=spans, probeSha256=sha(probe),
                    prefixSha256=sha(prefix), suffixSha256=sha(suffix), observationsSha256=sha(observed),
                    scope='Unmodified Resize and transformation control flow; zero-filled BGRA and exact right-angle rotation shim; not shipped FreeImage/GUI execution')
    write(out / 'source-manifest.json', manifest)
    write(out / 'processes.json', runs)
    write(out / 'summary.json', dict(cases=len(cases), repeatsEqual=True, observationsSha256=sha(observed)))
    if args.record:
        if (ROOT / 'winimerge-resize-golden.json.gz').exists():
            raise ValueError('既存goldenは上書きしません')
        (ROOT / 'winimerge-resize-golden.json.gz').write_bytes(gzip.compress(observed, mtime=0))
        write(ROOT / 'cases.json', dict(license='CC0-1.0', cases=cases))
        for name in ['source-manifest.json', 'compiler.json', 'processes.json', 'summary.json']:
            shutil.copyfile(out / name, ROOT / name)
    else:
        assert observed == gzip.decompress((ROOT / 'winimerge-resize-golden.json.gz').read_bytes())
    print(json.dumps(dict(cases=len(cases), observationsSha256=sha(observed), repeatsEqual=True)))


if __name__ == '__main__':
    main()
