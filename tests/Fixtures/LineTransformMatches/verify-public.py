"""公開serialized key比較の間接観測reader。Native型やcacheの同等性は主張しない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,argparse
from verify import verify,BASE
ap=argparse.ArgumentParser();ap.add_argument('--actual',type=pathlib.Path,required=True);a=ap.parse_args();verify()
plan=[x for x in json.loads((BASE/'public-case-plan.json').read_bytes()) if x['expression'] is not None];actual=json.loads(a.actual.read_bytes())
assert len(plan)==len(actual)==102
for p,x in zip(plan,actual,strict=True):
 assert x['id']==p['id'] and x['expression']==p['expression'] and x['expectedSerializedKeys']==p['expectedSerializedKeys']
 assert set(x)=={'id','expression','expectedExpression','expectedSerializedKeys','expectedError','passed','actualTypedValueObserved','contextEquivalent','nativeExceptionEquivalent','exitCode','stdout','stderr'}
 assert x['passed'] is True and x['actualTypedValueObserved'] is False and x['contextEquivalent'] is False and x['nativeExceptionEquivalent'] is False
 rejection=p['expectedOutcome']=='runtime-rejection';assert x['expectedError']==rejection
 if rejection:assert x['exitCode']==2 and x['stdout']=='' and x['stderr'].strip()
 else:
  expected='@noopt Choose(LineNumber-1,'+','.join('"'+bytes.fromhex(k).decode('utf8').replace('"','""')+'"' for k in p['expectedSerializedKeys'])+')'
  assert x['expectedExpression']==expected and x['exitCode']==0 and json.loads(x['stdout'])['different'] is False
print(json.dumps(dict(passed=True,serializedKeyComparisons=91,runtimeRejections=11,nativeOnly=30,actualTypedValueObserved=False,actualSerializedKeysDirectlyObserved=False)))
