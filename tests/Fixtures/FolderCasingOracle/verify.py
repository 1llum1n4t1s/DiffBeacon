"""独立reader: 製品/native adapterをimportせず全byte・型・traceを照合する。"""
import argparse,hashlib,json,pathlib
ROOT=pathlib.Path(__file__).resolve().parent

def load_rows(data):
 rows=[json.loads(x) for x in data.splitlines()]
 assert all(isinstance(x.get('id'),str) for x in rows),'invalid id'
 assert len({x['id'] for x in rows})==len(rows),'duplicate id'
 return rows

def validate_value(v):
 assert v['type'] in ('none','string','array')
 if v['type']=='string':
  h=v['hex'];b=bytes.fromhex(h);assert h==b.hex();assert b.decode('utf-8').encode('utf-8')==b
 if v['type']=='array':
  for x in v['items']:validate_value(x)

def check(actual,expected,cases):
 rows=load_rows(actual);assert len(rows)==len(cases)==len(expected)
 for got,want,c in zip(rows,expected,cases):
  assert got==want,('native result differs',c['id'],got,want)
  assert got['id']==c['id']
  arity=len(c['args'])!=1 or c['nullArgs']
  assert got['trace']==([] if arity else [0]),c['id']
  assert got['status']==('error' if arity or c['throws'] else 'ok')
  if got['status']=='ok':
   validate_value(got['result']);assert bytes.fromhex(got['stringHex']).hex()==got['stringHex']
 return rows

def main():
 p=argparse.ArgumentParser();p.add_argument('--actual',type=pathlib.Path);a=p.parse_args()
 manifest=json.loads((ROOT/'manifest.json').read_text(encoding='utf-8'))
 for e in manifest['files']:
  b=(ROOT/e['file']).read_bytes();assert len(b)==e['size'] and hashlib.sha256(b).hexdigest()==e['sha256'],e['file']
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'));fixed=(ROOT/'golden.ndjson').read_bytes();expected=load_rows(fixed)
 check(fixed,expected,cases)
 # reader自体が値、型、trace、欠落を拒否することを確認する。
 for field in ['id','trace','result']:
  mutation=json.loads(json.dumps(expected));mutation[0][field]={'changed':True}
  try:check(('\n'.join(json.dumps(x) for x in mutation)+'\n').encode(),expected,cases)
  except AssertionError:pass
  else:raise AssertionError('mutation accepted: '+field)
 try:check(b'\n'.join(fixed.splitlines()[:-1])+b'\n',expected,cases)
 except AssertionError:pass
 else:raise AssertionError('truncation accepted')
 if a.actual:check(a.actual.read_bytes(),expected,cases)
 print(json.dumps(dict(cases=len(cases),ok=sum(r['status']=='ok' for r in expected),errors=sum(r['status']=='error' for r in expected),manifestVerified=True,mutationChecks=4,actualVerified=bool(a.actual))))
if __name__=='__main__':main()