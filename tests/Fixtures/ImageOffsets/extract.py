"""固定DLLの採取結果を製品に依存しない回帰fixtureへまとめる。"""
import base64, hashlib, json, sys
from pathlib import Path

source = Path(sys.argv[1]).resolve()
destination = Path(__file__).resolve().parent/'winimerge-offsets-golden.json'
sha = lambda data: hashlib.sha256(data).hexdigest().upper()
summary = json.loads((source/'summary.json').read_text('utf-8-sig'))
assert summary['failed'] == 0 and summary['cases'] == 12 and summary['states'] == 43
observations = (source/'observations.json').read_bytes()
assert sha(observations) == '3E24388CA2994F08D0AB20E4A29503DBEA9AB97B9130074C34BA340CE8B63D76'
cases = json.loads(observations)
for item in cases:
    folder = source/f"case-{item['case']:02d}"
    for pane, value in enumerate(item['inputs']):
        data = (folder/f'pane{pane}.png').read_bytes()
        assert sha(data) == value['pngSha256']
        value['pngBase64'] = base64.b64encode(data).decode('ascii')
    for value in item['exports']:
        data = (folder/f"final-pane{value['pane']}.png").read_bytes()
        assert sha(data) == value['pngSha256']
        value['pngBase64'] = base64.b64encode(data).decode('ascii')
root = {'sourceRevision':'da639cdfaeca87aaad0eaceec509afa11ad61421',
        'dllSha256':summary['dllSha256'], 'observationsSha256':sha(observations),
        'extractorSha256':sha(Path(__file__).read_bytes()), 'cases':cases}
data = (json.dumps(root, ensure_ascii=False, separators=(',', ':'))+'\n').encode('utf-8')
if destination.exists() and destination.read_bytes()!=data: raise RuntimeError('existing fixture differs')
destination.write_bytes(data)
print(json.dumps({'bytes':len(data),'sha256':sha(data),'cases':len(cases)}))
