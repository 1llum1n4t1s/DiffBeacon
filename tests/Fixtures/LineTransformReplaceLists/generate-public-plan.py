"""Native goldenを参照する静的公開plan。製品は実行しない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,argparse
BASE=pathlib.Path(__file__).resolve().parent
def literal(n):
 t=n['type'];v=n.get('value')
 if t=='string':return '"'+v.replace('"','""')+'"'
 if t=='integer':return 'ToInt("'+str(v)+'")'
 if t=='bool':return str(v).lower()
 if t=='none':return 'none'
 if t=='array':return 'Array('+','.join(map(literal,v))+')'
 if t=='throw':return '(1/(LineNumber-LineNumber))'
 raise ValueError(t)
def representable(n):return n['type'] not in ['regex','path'] and (n['type']!='array' or all(map(representable,n['value'])))
def plan():
 cases=json.loads((BASE/'cases.json').read_bytes());golden=[json.loads(l) for l in (BASE/'golden.ndjson').read_bytes().splitlines()];result=[]
 for c,g in zip(cases,golden,strict=True):
  assert c['id']==g['id'];public=not c['nullArgs'] and all(map(representable,c['args']))
  expression='@'+('cs' if c['caseSensitive'] else 'ci')+' @noopt '+c['function']+'('+','.join(map(literal,c['args']))+')' if public else '@cs @noopt '+c['function']+'(none,Array())'
  outcome='native-only';keys=[]
  if public:
   outcome='compile-rejection' if len(c['args'])!=2 else 'runtime-rejection' if g['errorUtf8Hex'] else 'serialized-key-comparison'
   if outcome=='serialized-key-comparison':keys=[g['serializedUtf8Hex']]
  result.append(dict(id=c['id'],expression=expression,sourceLineCount=1,expectedOutcome=outcome,expectedSerializedKeys=keys))
 return result
if __name__=='__main__':
 ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);a=ap.parse_args()
 from capture import validate
 out=validate(a.output);assert not out.exists();out.parent.mkdir(parents=True,exist_ok=True);out.write_text(json.dumps(plan(),ensure_ascii=False,indent=2)+'\n',encoding='utf8')
