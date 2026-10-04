"""原本固定SHAと製品出力の全byte照合。goldenを書かない。"""
import hashlib, json, sys, struct
from pathlib import Path
root=Path(sys.argv[1]).resolve(); output=Path(sys.argv[2]).resolve()
def sha(data): return hashlib.sha256(data).hexdigest()
manifest=json.loads((root/'manifest.json').read_bytes())
provenance=json.loads((root/'provenance.json').read_bytes())
for source in provenance['sourceFiles']:
 data=(root/source['file']).read_bytes()
 assert sha(data)==source['sha256'] and len(data)==source['size'],source['file']
extract=(root/'original-functions.inc').read_bytes()
assert sha(extract)==provenance['extractedSha256']
lines=(root/'BinTrans.cpp').read_bytes().splitlines(keepends=True)
for function in provenance['functions']:
 data=b''.join(lines[function['startLine']-1:function['endLine']])
 assert sha(data)==function['sha256'] and data in extract,function['name']
assert len(manifest['cases'])==634
for case in manifest['cases']:
 source=(root/case['inputFile']).read_bytes(); golden=(root/case['expectedFile']).read_bytes()
 assert len(source)==case['inputLength'] and sha(source)==case['inputSha256'],case['id']
 assert len(golden)==case['outputLength'] and sha(golden)==case['outputSha256'],case['id']
 actual=(output/(case['id']+'.bin')).read_bytes()
 assert actual==golden,case['id']
for endian in ('little','big'):
 for typ,value in [('f',1.5),('d',-2.25)]:
  actual=(output/('ieee-'+typ+'-'+endian+'.bin')).read_bytes()
  assert actual==struct.pack(('<' if endian=='little' else '>')+typ,value)
print(json.dumps(dict(success=True,originalCases=634,encoder=263,decoder=371,ieeeIndependent=4,oracle='unmodified original golden + Python stdlib struct/full bytes',osClipboardExecuted=False)))
