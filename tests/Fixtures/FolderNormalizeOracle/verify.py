"""製品/adapterから独立したreader。全byte・型・trace・公開投影区分を照合する。"""
import argparse, copy, hashlib, json, pathlib, unicodedata
ROOT=pathlib.Path(__file__).resolve().parent
def load(data):
 rows=[json.loads(x) for x in data.splitlines()]
 assert len({r['id'] for r in rows})==len(rows),'duplicate id'
 return rows
def invalid_form(v):
 if v['type']=='string':return v['value'].upper() not in ['NFC','NFD','NFKC','NFKD','C','D','KC','KD']
 if v['type']=='int':return int(v['value']) not in [1,2,5,6]
 return False
def shape(v):
 assert v['type'] in ['none','string','array']
 if v['type']=='none':assert set(v)=={'type'}
 if v['type']=='string':
  assert set(v)=={'type','hex'}
  assert bytes.fromhex(v['hex']).hex()==v['hex']
  bytes.fromhex(v['hex']).decode('utf-8')
 if v['type']=='array':
  assert set(v)=={'type','items'}
  for x in v['items']:shape(x)
def stringify(v):
 if v['type']=='none':return 'none'
 if v['type']=='string':return bytes.fromhex(v['hex']).decode('utf-8')
 return '['+', '.join(stringify(x) for x in v['items'])+']'
def check(actual,fixed,cases):
 rows=load(actual);assert len(rows)==len(fixed)==len(cases),'case count'
 for got,want,c in zip(rows,fixed,cases):
  assert got==want,('fixed mismatch',c['id'])
  assert got['id']==c['id']
  args=c['args'];trace=[];error=False
  if c['nullArgs'] or not 1<=len(args)<=2:error=True
  else:
   if len(args)==2:
    trace.append(1)
    if 1 in c['throws']:error=True
   skip=len(args)==2 and invalid_form(args[1])
   if not error and not skip:
    trace.append(0)
    if 0 in c['throws']:error=True
  assert got['trace']==trace,('trace',c['id'])
  assert got['status']==('error' if error else 'ok'),c['id']
  assert c['projection'] in ['native-only','public-flt-projectable']
  if not error:
   shape(got['result']);assert bytes.fromhex(got['stringHex']).decode('utf-8')==stringify(got['result'])
   if len(args)==2 and invalid_form(args[1]):assert got['result']=={'type':'none'}
   # 別のUnicode databaseによる標準的な文字列ケースの二重照合。
   elif args[0]['type']=='string':
    form='NFC'
    if len(args)==2 and args[1]['type']=='string':
     form=args[1]['value'].upper();form=form if form.startswith('NF') else 'NF'+form
    if len(args)==2 and args[1]['type']=='int':form={1:'NFC',2:'NFD',5:'NFKC',6:'NFKD'}[int(args[1]['value'])]
    assert got['result']==dict(type='string',hex=unicodedata.normalize(form,args[0]['value']).encode('utf-8').hex()),('independent Unicode normalization',c['id'])
 return rows
def main():
 p=argparse.ArgumentParser();p.add_argument('--actual',type=pathlib.Path);a=p.parse_args()
 manifest=json.loads((ROOT/'manifest.json').read_text(encoding='utf-8'))
 for e in manifest['files']:
  b=(ROOT/e['file']).read_bytes();assert len(b)==e['size'] and hashlib.sha256(b).hexdigest()==e['sha256'],e['file']
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'));raw=(ROOT/'golden.ndjson').read_bytes();fixed=load(raw)
 check(raw,fixed,cases)
 # 結果byte・型・評価順・例外・ID・件数の改変を個別に拒否する。
 mutations=[]
 for key,val in [('id','changed'),('trace',[0,1]),('result',{'type':'none'}),('status','error')]:
  m=copy.deepcopy(fixed);m[0][key]=val;mutations.append(m)
 m=copy.deepcopy(fixed);m[0]['result']['hex']='00';mutations.append(m)
 mutations.extend([fixed[:-1],fixed+[fixed[0]],list(reversed(fixed))])
 for m in mutations:
  try:check(('\n'.join(json.dumps(x) for x in m)+'\n').encode(),fixed,cases)
  except AssertionError:pass
  else:raise AssertionError('accepted mutation')
 if a.actual:
  b=a.actual.read_bytes();assert b==raw,'stdout bytes differ';check(b,fixed,cases)
 print(json.dumps(dict(cases=len(cases),ok=sum(r['status']=='ok' for r in fixed),errors=sum(r['status']=='error' for r in fixed),publicProjectable=sum(c['projection']=='public-flt-projectable' for c in cases),nativeOnly=sum(c['projection']=='native-only' for c in cases),manifestVerified=True,mutationChecks=len(mutations),unicodeDatabase=unicodedata.unidata_version,actualVerified=bool(a.actual))))
if __name__=='__main__':main()
