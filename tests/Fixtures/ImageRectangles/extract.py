"""無改変C++核の二回観測。自作入力CC0-1.0、probeと抽出核GPL。"""
import argparse
import gzip
import hashlib
import json
import pathlib
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=pathlib.Path, required=True)
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parent
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=True)
if any(out.iterdir()):
    raise RuntimeError('採取先は空の専用ディレクトリが必要です')

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def write(name, value):
    (out / name).write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')

manifest = json.loads((root / 'source-manifest.json').read_bytes())
source = (root.parent / 'ImageCopy/reference-source/ImgMergeBuffer.hpp').read_bytes()
assert sha(source) == manifest['sourceSha256']
probe = (root / 'kernel-probe.cpp').read_bytes()
assert sha(probe) == manifest['probeSha256']
for span in manifest['extraction']:
    raw = source[span['startByte']:span['endByteExclusive']]
    assert sha(raw) == span['sha256']
    assert probe.count(raw) == 1
    assert (root / (span['name'] + '.inc')).read_bytes() == raw
cases_bytes = (root / 'cases.json').read_bytes()
assert sha(cases_bytes) == '371961F9AE6E88689C76DDFB76CB995F3E5239E1E1450DDE58FA3F7F588CFCE1'
cases = json.loads(cases_bytes)['cases']
(out / 'kernel-probe.cpp').write_bytes(probe)
cl = shutil.which('cl')
if not cl:
    raise RuntimeError('MSVC x64 Native Tools環境で実行してください')
exe = out / 'kernel-probe.exe'
command = [cl, '/nologo', '/std:c++17', '/EHsc', '/MT', '/Y-', '/utf-8', '/W3', '/Od',
           '/Fe' + str(exe), '/Fo' + str(out / 'kernel-probe.obj'), str(out / 'kernel-probe.cpp')]
compiled = subprocess.run(command, cwd=out, capture_output=True)
(out / 'compiler.log').write_bytes(compiled.stdout + compiled.stderr)
write('compiler.json', dict(command=command, exit=compiled.returncode))
assert compiled.returncode == 0
words = [str(len(cases))]
for case in cases:
    words += [case['id'], case['op'], str(case['pane']), str(int(case['readOnly'])), *map(str, case['rect'])]
    for im in [case['target'], case['source']]:
        words += [str(im['width']), str(im['height']), *map(str, im['bgra'])]
protocol = (' '.join(words) + '\n').encode()
(out / 'probe-input.txt').write_bytes(protocol)
outputs, runs = [], []
for run in [1, 2]:
    process = subprocess.Popen([str(exe)], cwd=out, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    stdout, stderr = process.communicate(protocol, timeout=20)
    (out / f'run{run}.txt').write_bytes(stdout)
    (out / f'run{run}-stderr.log').write_bytes(stderr)
    runs.append(dict(run=run, pid=process.pid, exit=process.returncode, sha256=sha(stdout)))
    assert process.returncode == 0
    outputs.append(stdout)
assert outputs[0] == outputs[1]
lines = outputs[0].decode().splitlines()
assert len(lines) == len(cases)
observations = []
for case, line in zip(cases, lines):
    ident, result, history, compare, hexbytes = line.split(' ')
    assert ident == case['id']
    raw = bytes.fromhex(hexbytes)
    assert len(raw) == len(case['target']['bgra'])
    observations.append(dict(id=ident, result=int(result), historyPushes=int(history), compareCalls=int(compare),
                             width=case['target']['width'], height=case['target']['height'], bgraHex=hexbytes,
                             bgraSha256=sha(raw), changed=raw != bytes(case['target']['bgra'])))
write('observations.json', observations)
observed = (out / 'observations.json').read_bytes()
expected = gzip.decompress((root / 'winimerge-rectangles-golden.json.gz').read_bytes())
assert observed == expected, '原本出力が固定goldenと不一致'
write('processes.json', runs)
write('verification.json', dict(cases=len(cases), sourceSha256=sha(source), observationsSha256=sha(observed), allBytesRepeat=True))
print(json.dumps(dict(cases=len(cases), observationsSha256=sha(observed))))
