# 観測pass有無で最終原文結果とraw pair scriptが同一か、全bodyから独立照合する。
import argparse, hashlib, json, pathlib
p=argparse.ArgumentParser();p.add_argument('--observed',required=True,type=pathlib.Path);p.add_argument('--plain',required=True,type=pathlib.Path);p.add_argument('--output',required=True,type=pathlib.Path);a=p.parse_args()
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
s0=read(a.observed/'capture-status.json');s1=read(a.plain/'capture-status.json')
assert s0['complete'] and s1['complete'] and s0['observerEnabled'] and not s1['observerEnabled']
assert s0['executable']==s1['executable'] and s0['executableSha256']==s1['executableSha256']
assert (a.observed/'transport.ndjson').read_bytes()==(a.plain/'transport.ndjson').read_bytes()
left=[json.loads(x) for x in (a.observed/'expected.ndjson').read_text().splitlines()];right=[json.loads(x) for x in (a.plain/'expected.ndjson').read_text().splitlines()]
assert len(left)==len(right)==63
for x,y in zip(left,right):
    assert x['id']==y['id']
    for pair in x['original']['pairs']:pair.pop('ranges')
    for pair in y['original']['pairs']:assert pair.pop('ranges')==[]
    assert x==y, x['id']
    r0=read(a.observed/x['id']/'process.json');r1=read(a.plain/y['id']/'process.json')
    assert r0['pid']!=r1['pid'] or r0['birthWindowsFileTime']!=r1['birthWindowsFileTime']
    assert r0['arguments'][-1]!='--no-observer' and r1['arguments'][-1]=='--no-observer'
result={'complete':True,'cases':63,'sameExecutableSha256':s0['executableSha256'],'separateProcess':True,'mergedFlagsCountsRawScriptsIdentical':True,'observerPairRangesExcludedFromEquivalence':'plain intentionally does not run observer pass','observedExpectedSha256':sha(a.observed/'expected.ndjson'),'plainExpectedSha256':sha(a.plain/'expected.ndjson')}
with a.output.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2)
print(json.dumps(result))
