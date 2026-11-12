"""静的public bridge。Native出力を参照するだけで期待値は計算しない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,argparse
BASE=pathlib.Path(__file__).resolve().parent

def literal(v):
 t=v['type'];x=v.get('value')
 if t=='none':return 'none'
 if t=='bool':return str(x).lower()
 if t=='integer':return 'ToInt("'+str(x)+'")'
 if t=='double':return repr(x)
 if t=='string':return '"'+x.replace('"','""')+'"'
 if t=='array':return 'Array('+','.join(map(literal,x))+')'
 if t=='throw':return '(1/(LineNumber-LineNumber))'
 raise ValueError(t)

def plan():
 cases=json.loads((BASE/'cases.json').read_bytes());golden=[json.loads(x) for x in (BASE/'golden.ndjson').read_bytes().splitlines()]
 result=[]
 for c,g in zip(cases,golden,strict=True):
  assert c['id']==g['id']
  item=dict(id=c['id'],expression=None,sourceLineCount=len(c['rows']),expectedOutcome='native-only',expectedSerializedKeys=[],actualTypedValueObserved=False,contextEquivalent=False,nativeExceptionEquivalent=False)
  if c['rows'] and c['mode'] in ['scan','nested-match','nested-aggregate','throw-scan'] and not any(v['type'] in ['timestamp','raw-string'] for v in c['rows']):
   rows=c['rows']
   if c['mode']=='throw-scan':rows=[v if i!=1 else dict(type='throw',value='scan-error') for i,v in enumerate(rows)]
   producer='Choose(LineNumber-1,'+','.join(map(literal,rows))+')'
   if c['mode']=='nested-match':producer='MatchCount('+producer+')'
   if c['mode']=='nested-aggregate':producer='Sum('+producer+')'
   item['expression']='@cs @noopt '+c['function']+'('+producer+(','+literal(c['before'])+','+literal(c['after']) if c['function']=='matchcontext' else '')+')'
   first=[s for s in g['steps'] if s['pass']==0]
   item['expectedOutcome']='runtime-rejection' if any(s['errorUtf8Hex'] for s in first) else 'serialized-key-comparison'
   if item['expectedOutcome']=='serialized-key-comparison':item['expectedSerializedKeys']=[s['serializedUtf8Hex'] for s in first]
  result.append(item)
 return result
if __name__=='__main__':
 ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);a=ap.parse_args()
 from capture import validate
 out=validate(a.output,BASE.parents[2]);assert not out.exists();out.parent.mkdir(parents=True,exist_ok=True)
 out.write_text(json.dumps(plan(),ensure_ascii=False,indent=2)+'\n',encoding='utf8',newline='\n')
