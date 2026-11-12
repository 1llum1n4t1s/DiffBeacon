"""固定入力のみを生成する。期待値はWindows native採取から固定する。"""
import json, pathlib
ROOT=pathlib.Path(__file__).resolve().parent
def v(t,x=None):
 return {'type':t,**({'value':x} if x is not None else {})}
def s(x):return v('string',x)
def i(x):return v('int',str(x))
cases=[]
def add(id,args,throws=(),native=False,null=False):
 def native_value(x):
  if x['type'] in ['bytes','regex','file']:return True
  if x['type']=='array':return any(native_value(y) for y in x['value'])
  return x.get('value') in ['NAN','INFINITY'] or x['type']=='string' and '\0' in x['value']
 native=native or any(native_value(x) for x in args)
 cases.append(dict(id=id,args=args,throws=list(throws),nullArgs=null,projection='native-only' if native else 'public-flt-projectable'))
samples={'combining':'e\u0301','precomposed':'\u00e9','compatibility':'\ufb03\uff21\u2460\u212b\u00a0','hangul':'\u1100\u1161\u11a8','ordering':'a\u0315\u0300','supplementary':'\U0001d400\U0001f600\U0001d15e','japanese':'\u30ab\u3099\uff76\uff9e','empty':'','ascii':'ABC xyz 123'}
for name,text in samples.items():
 add('default-'+name,[s(text)])
 for form in ['NFC','NFD','NFKC','NFKD']:add(name+'-'+form,[s(text),s(form)])
for form in ['C','D','KC','KD','nfc','nfd','nfkc','nfkd','c','d','kc','kd']:
 add('alias-'+form,[s('e\u0301\ufb03'),s(form)])
for n in [1,2,5,6]:add('int-alias-'+str(n),[s('e\u0301\ufb03'),i(n)])
for j,form in enumerate(['',' NFC','NFC ','NFK','1','Ｃ','NFC\0','\u00e9']):
 add('invalid-string-'+str(j),[s('e\u0301'),s(form)])
 add('invalid-string-skips-value-'+str(j),[s('unused'),s(form)],[0],True)
for n in [0,3,4,-1,7,9223372036854775807,-9223372036854775808]:
 add('invalid-int-'+str(n),[s('e\u0301'),i(n)])
 add('invalid-int-skips-value-'+str(n),[s('unused'),i(n)],[0],True)
for j,form in enumerate([v('double','2.0'),v('double','5.0'),v('double','0.0'),v('double','NAN'),v('none'),v('bool',True),v('bool',False),v('array',[]),v('array',[s('NFD')]),v('regex'),v('file')]):
 add('wrong-form-default-'+str(j),[s('e\u0301\ufb03'),form],native=form['type'] in ['regex','file'] or form.get('value')=='NAN')
for j,value in enumerate([v('none'),v('bool',True),v('bool',False),i(0),i(-42),i(9223372036854775807),v('double','1.25'),v('double','-0.0'),v('double','NAN'),v('double','INFINITY'),v('regex'),v('file'),v('array',[]),v('array',[s('e\u0301'),v('none'),i(12),v('bool',False)]),v('array',[v('array',[s('e\u0301'),s('\ufb03'),v('none')]),v('array',[]),s('e\u0301')])]):
 add('value-type-'+str(j),[value],native=value['type'] in ['regex','file'] or value.get('value') in ['NAN','INFINITY'] if not isinstance(value.get('value'),list) else False)
add('nested-NFKC',[v('array',[v('array',[s('\ufb03'),v('array',[s('e\u0301'),v('none')])]),v('none')]),s('NFKC')])
for j,h in enumerate(['00','6500cc81','65cc810078','80','c0af','e282','eda080','f4908080','f09f9880ff65cc81','efbfbd','f48fbfbf','c280','efbfbf']):
 for form in ['NFC','NFD','NFKC','NFKD']:add('bytes-'+str(j)+'-'+form,[v('bytes',h),s(form)],native=True)
add('one-value-throws',[s('unused')],[0],True)
add('form-throws',[s('unused'),s('NFC')],[1],True)
add('both-throw-form-first',[s('unused'),s('NFC')],[0,1],True)
add('value-throws-after-valid-form',[s('unused'),s('NFD')],[0],True)
add('value-throws-after-ignored-form',[s('unused'),v('none')],[0],True)
add('arity-zero',[],native=True)
add('arity-three',[s('unused'),s('NFC'),s('unused')],native=True)
add('arity-null',[],native=True,null=True)
assert len({x['id'] for x in cases})==len(cases)
(ROOT/'cases.json').write_text(json.dumps(cases,ensure_ascii=True,indent=2)+'\n',encoding='utf-8')
print(len(cases))
