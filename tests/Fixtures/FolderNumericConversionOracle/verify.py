"""独立reader: compiler/adapterをimportせず固定bytes・型・trace・代表境界を照合する。"""
import argparse,hashlib,json,pathlib,re,struct
ROOT=pathlib.Path(__file__).resolve().parent

def sha(b):return hashlib.sha256(b).hexdigest()
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def rows(p):return [json.loads(x) for x in p.read_text(encoding='utf-8-sig').splitlines()]
def input_value(v):
 t=v['type']; x=v.get('value')
 if t in ('none','throw'):return {'type':'none'}
 if t=='array':return {'type':'array','items':[input_value(z) for z in x]}
 if t=='string':return {'type':'string','utf8Hex':x.encode().hex()}
 if t=='int':return {'type':'int','decimal':x}
 if t=='bool':return {'type':'bool','value':x}
 if t=='double':return {'type':'double','bits':None} # NaN payload is CRT-specific; independently validate shape below.
 raise AssertionError(t)
def check_value(v):
 t=v['type']
 if t=='none':assert set(v)=={'type'}
 elif t=='int':assert re.fullmatch(r'-?[0-9]+',v['decimal']) and -(1<<63)<=int(v['decimal'])<(1<<63)
 elif t=='double':assert re.fullmatch(r'[0-9a-f]{16}',v['bits']);struct.unpack('>d',bytes.fromhex(v['bits']))
 elif t=='array':
  for item in v['items']:check_value(item)
 elif t=='string':bytes.fromhex(v['utf8Hex']).decode('utf-8')
 elif t=='bool':assert type(v['value']) is bool
 else:raise AssertionError(t)
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--capture',type=pathlib.Path);ap.add_argument('--actual',type=pathlib.Path);a=ap.parse_args()
 manifest=load(ROOT/'manifest.json')
 for name,e in manifest['files'].items():
  b=(ROOT/name).read_bytes();assert len(b)==e['size'] and sha(b)==e['sha256'],name
 provenance=load(ROOT/'provenance.json')
 for e in provenance['sources']:
  b=(ROOT/e['file']).read_bytes();assert sha(b)==e['sha256'] and len(b)==e['size']
  assert hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==e['blob']
 cases=load(ROOT/'cases.json');gold=rows(ROOT/'golden.ndjson');assert len(cases)==len(gold)==246
 assert len({c['id'] for c in cases})==len(cases)
 for c,g in zip(cases,gold):
  assert c['id']==g['id']
  assert g['trace']==([] if not c['args'] else [0]),c['id']
  assert len(g['input'])==len(c['args'])==len(g['inputStringHex'])
  for v,x in zip(c['args'],g['input']):
   check_value(x);expected=input_value(v)
   if v['type']=='double':
    d=struct.unpack('>d',bytes.fromhex(x['bits']))[0]
    assert (d!=d) if v['value']=='nan' else d==float(v['value'])
   else:assert x==expected
  if c['args'] and c['args'][0]['type']=='throw':assert g['status']=='error' and g['error']=='injected-evaluation-error'
  else:assert g['status']=='ok';check_value(g['result']);bytes.fromhex(g['stringHex']).decode('utf-8')
 by={(c['function'],c['args'][0].get('value')):g for c,g in zip(cases,gold) if len(c['args'])==1 and c['args'][0]['type']=='string'}
 def expect(fn,s,t,field=None,value=None):
  g=by[fn,s];assert g['result']['type']==t,(fn,s,g)
  if field:assert g['result'][field]==value,(fn,s,g)
 for fn in ['ToInt','ToDouble','ToNumber']:
  for s in ['',',','42 ','42x','42\0','\0','１２','1_000','1 2']:expect(fn,s,'none')
  expect(fn,'1,,2','double' if fn=='ToDouble' else 'int')
 for fn in ['ToInt','ToNumber']:
  expect(fn,'9223372036854775807','int','decimal','9223372036854775807');expect(fn,'-9223372036854775808','int','decimal','-9223372036854775808')
  for s in ['9223372036854775808','-9223372036854775809','0x10','0x1p2','nan','inf']:expect(fn,s,'none')
 for fn in ['ToDouble','ToNumber']:
  for s in ['1e309','-1e309','1e-9999','-1e-9999']:expect(fn,s,'none')
  expect(fn,'4.9406564584124654e-324','double','bits','0000000000000001');expect(fn,'-0.0','double','bits','8000000000000000')
  expect(fn,'0x1.8p2','double','bits','4018000000000000');expect(fn,'0xDEAD','double','bits','40ebd5a000000000')
 expect('ToDouble','nan','double','bits','7fffffffffffffff');expect('ToDouble','inf','double','bits','7ff0000000000000')
 expect('ToDouble','0x10','double','bits','4030000000000000');expect('ToNumber','Infinity','none')
 for c,g in zip(cases,gold):
  if c['id'].endswith('typed8'):
   assert bytes.fromhex(g['inputStringHex'][0]).decode()=='[1,234, 1.5, none, 2, true, [9]]'
   assert all(x['type']=='none' for x in g['result']['items'][2:])
 if a.actual:assert a.actual.read_bytes()==(ROOT/'golden.ndjson').read_bytes(),'actual differs from fixed golden'
 if a.capture:
  receipts=load(a.capture/'receipts.json');assert receipts==load(ROOT/'capture.json')
  for name,digest in receipts['inputSha256'].items():assert sha((ROOT/name).read_bytes())==digest,name
  assert sha((a.capture/'build.stdout').read_bytes())==receipts['compilerStdoutSha256']
  assert sha((a.capture/'build.stderr').read_bytes())==receipts['compilerStderrSha256']
  for name,digest in receipts['extractionSha256'].items():assert sha((a.capture/name).read_bytes())==digest,name
  runs=receipts['runs'];assert len(runs)==2 and runs[0]['pid']!=runs[1]['pid']
  assert runs[0]['processCreationFiletime']!=runs[1]['processCreationFiletime']
  for r in runs:
   b=(a.capture/f"run-{r['run']}.ndjson").read_bytes();assert b==(ROOT/'golden.ndjson').read_bytes()
   assert sha(b)==r['stdoutSha256'] and len(b)==r['stdoutBytes'] and r['exitCode']==0 and r['stdoutEof'] and r['stderrEof'] and r['heldNativeProcessHandle']
   assert not (a.capture/f"run-{r['run']}.stderr").read_bytes()
   assert sha((a.capture/'reference.exe').read_bytes())==r['executableSha256']
 print(json.dumps({'ok':True,'cases':len(cases),'errors':sum(g['status']=='error' for g in gold),'goldenSha256':sha((ROOT/'golden.ndjson').read_bytes()),'captureChecked':bool(a.capture)}))
if __name__=='__main__':main()
