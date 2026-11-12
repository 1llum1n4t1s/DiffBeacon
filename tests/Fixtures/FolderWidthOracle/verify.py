"""独立reader。製品/adapterを使わず全byte・型・traceと直接Windows APIを照合する。"""
import argparse,copy,ctypes,hashlib,json,pathlib
ROOT=pathlib.Path(__file__).resolve().parent
def rows(raw):
 r=[json.loads(x) for x in raw.splitlines()]
 assert len({x['id'] for x in r})==len(r),'duplicate id'
 return r
def stringify(v):
 t=v['type']
 if t=='none':return 'none'
 if t=='string':return v['value']
 if t=='bytes':return bytes.fromhex(v['value']).decode('utf-8')
 if t=='int':return str(int(v['value']))
 if t=='bool':return str(v['value']).lower()
 if t=='double':return format(float(v['value']),'.6f')
 if t=='regex':return '<Regex>'
 if t=='file':return '<FileContent:AbC>'
 if t=='array':return '['+', '.join(stringify(x) for x in v['value'])+']'
 raise AssertionError(t)
def text(v):
 if v['type']=='none':assert set(v)=={'type'};return 'none'
 if v['type']=='string':
  assert set(v)=={'type','hex'}
  assert bytes.fromhex(v['hex']).hex()==v['hex']
  return bytes.fromhex(v['hex']).decode('utf-8')
 assert v['type']=='array' and set(v)=={'type','items'}
 return '['+', '.join(text(x) for x in v['items'])+']'
def api_reader():
 dll=ctypes.WinDLL('kernel32',use_last_error=True);fn=dll.LCMapStringEx
 fn.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_wchar_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_ssize_t]
 def convert(s,flag):
  if not s:return s
  n=len(s.encode('utf-16-le'))//2
  size=fn(None,flag,s,n,None,0,None,None,0)
  if size<=0:return s
  result=ctypes.create_unicode_buffer(size)
  used=fn(None,flag,s,n,result,size,None,None,0)
  assert used>0,'second direct API call failed'
  return ctypes.wstring_at(result,size)
 return convert
def check(raw,fixed,cases,functions,convert=None):
 actual=rows(raw);assert len(actual)==len(fixed)==len(cases),'case count'
 api_count=0
 for got,want,c in zip(actual,fixed,cases):
  assert got==want,('golden mismatch',c['id'])
  assert got['id']==c['id']
  idx=c['functionIndex']
  assert 0<=idx<=6
  if idx<6:assert functions[idx]['registeredName']==c['registeredName']
  assert c['projection'] in ['native-only','public-flt-projectable']
  arity=c['nullArgs'] or len(c['args'])!=1
  error=arity or 0 in c['throws']
  assert got['trace']==([] if arity else [0])
  assert got['status']==('error' if error else 'ok')
  if error:continue
  assert text(got['result']).encode('utf-8').hex()==got['stringHex']
  v=c['args'][0]
  if idx==6:assert got['result']==dict(type='string',hex=v['value'].encode('utf-8').hex())
  if v['type']=='none':assert got['result']=={'type':'none'}
  if v['type']=='array':
   assert got['result']['type']=='array'
   assert len(v['value'])==len(got['result']['items'])
  if convert:
   flag=functions[idx]['flagValue'] if idx<6 else 0xffffffff
   def expected(v):
    nonlocal api_count
    if v['type']=='none':return {'type':'none'}
    s=stringify(v);api_count+=1
    return dict(type='string',hex=convert(s,flag).encode('utf-8').hex())
   # malformed UTF8はPoco独自decoderの固定native証拠で照合する。
   try:
    exp=dict(type='array',items=[expected(x) for x in v['value']]) if v['type']=='array' else expected(v)
   except UnicodeDecodeError:continue
   assert got['result']==exp,('independent direct Windows API',c['id'])
 # NFKCとは異なり、無関係な互換文字を一括分解しない。
 byid={x['id']:x for x in actual}
 for name in ['tohalfwidth','tofullwidth']:
  c=next(c for c in cases if c['id']==name+'-unrelated-compatibility')
  assert byid[c['id']]['result']['hex']==c['args'][0]['value'].encode('utf-8').hex()
 return api_count
def main():
 p=argparse.ArgumentParser();p.add_argument('--actual',type=pathlib.Path);p.add_argument('--windows-api',action='store_true');a=p.parse_args()
 manifest=json.loads((ROOT/'manifest.json').read_text(encoding='utf-8'))
 for e in manifest['files']:
  b=(ROOT/e['file']).read_bytes();assert len(b)==e['size'] and hashlib.sha256(b).hexdigest()==e['sha256'],e['file']
 cases=json.loads((ROOT/'cases.json').read_text(encoding='utf-8'));functions=json.loads((ROOT/'functions.json').read_text(encoding='utf-8'))['functions']
 raw=(ROOT/'golden.ndjson').read_bytes();fixed=rows(raw)
 count=check(raw,fixed,cases,functions,api_reader() if a.windows_api else None)
 mutations=[]
 for k,v in [('id','changed'),('trace',[]),('result',{'type':'none'}),('status','error')]:
  m=copy.deepcopy(fixed);m[0][k]=v;mutations.append(m)
 m=copy.deepcopy(fixed);m[0]['result']['hex']='00';mutations.append(m)
 mutations.extend([fixed[:-1],fixed+[fixed[0]],list(reversed(fixed))])
 for m in mutations:
  try:check(('\n'.join(json.dumps(x) for x in m)+'\n').encode(),fixed,cases,functions)
  except AssertionError:pass
  else:raise AssertionError('mutation accepted')
 if a.actual:
  b=a.actual.read_bytes();assert b==raw,'stdout byte mismatch';check(b,fixed,cases,functions)
 print(json.dumps(dict(cases=len(cases),ok=sum(x['status']=='ok' for x in fixed),errors=sum(x['status']=='error' for x in fixed),publicProjectable=sum(c['projection']=='public-flt-projectable' for c in cases),nativeOnly=sum(c['projection']=='native-only' for c in cases),manifestVerified=True,mutationChecks=len(mutations),directWindowsApiValues=count,actualVerified=bool(a.actual))))
if __name__=='__main__':main()
