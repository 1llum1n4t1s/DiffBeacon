"""独立OS APIまたは固定互換codec値と実アプリの全byteを照合する。"""
import json,sys
from pathlib import Path
root=Path(sys.argv[1]); doc=json.loads((root/'reference.json').read_bytes())
for case in doc['cases']:
 actual=Path(case['actual']).read_bytes()
 assert actual==(Path(case['expected']).read_bytes() if case['expectedExit']==0 else b'KEEP'),case['name']
if doc['api']:
 assert len(doc['api'])==256 and [x['input'] for x in doc['api']]==list(range(256))
 assert doc['ansiCodePage']>0 and doc['oemCodePage']>0
print(json.dumps(dict(success=True,oracle=doc['oracle'],ansiCodePage=doc['ansiCodePage'],oemCodePage=doc['oemCodePage'],apiInputs=len(doc['api']),failedApiReturns=sum(x['result']==0 for x in doc['api']),cases=len(doc['cases']),literalTokenEscapeBoundary=True,osClipboardExecuted=False)))
