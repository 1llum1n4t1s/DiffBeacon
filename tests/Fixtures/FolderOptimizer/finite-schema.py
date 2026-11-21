"""Finite producer schema only; not an expression-result semantics oracle."""
import math,re
VALUE_FIELDS={'none':{'kind'},'bool':{'kind','value'},'integer':{'kind','decimal'},'double':{'kind','hexfloat'},'string':{'kind','utf8Hex'},'timestamp':{'kind','epochMicroseconds'},'opaque-native-regex':{'kind'},'array':{'kind','items'}}
LITERAL_KINDS={'BoolLiteral':'bool','IntLiteral':'integer','SizeLiteral':'integer','DurationLiteral':'integer','VersionLiteral':'integer','DoubleLiteral':'double','StringLiteral':'string','DateTimeLiteral':'timestamp','RegularExpressionLiteral':'opaque-native-regex','ArrayLiteral':'array'}
ROW_FIELDS={'id','phase','errorUtf8Hex','compileCode','compileMessageUtf8Hex','before','after','optimizeTrace','evaluateTrace1','evaluateTrace2','value1','value2','runtimeError1Utf8Hex','runtimeError2Utf8Hex','readsCompile','reads1','reads2','context'}
def exact_keys(obj,keys):assert type(obj) is dict and set(obj)==keys,(obj,keys)
def text(value):assert type(value) is str
def hex_text(value):
 text(value);assert len(value)%2==0 and re.fullmatch('[0-9a-f]*',value) is not None,value
def int64_text(value):
 text(value);assert re.fullmatch(r'0|-?[1-9][0-9]*',value) is not None,value
 assert -(2**63)<=int(value)<=2**63-1,value
def typed_value(obj):
 assert type(obj) is dict and type(obj.get('kind')) is str and obj['kind'] in VALUE_FIELDS,obj
 kind=obj['kind'];exact_keys(obj,VALUE_FIELDS[kind])
 if kind=='bool':assert type(obj['value']) is bool
 elif kind=='integer':int64_text(obj['decimal'])
 elif kind=='timestamp':int64_text(obj['epochMicroseconds'])
 elif kind=='string':hex_text(obj['utf8Hex'])
 elif kind=='double':
  value=obj['hexfloat'];text(value)
  assert re.fullmatch(r'-?0x[0-9a-f]+(?:\.[0-9a-f]*)?p[+-][0-9]+',value) is not None,value
  try:converted=float.fromhex(value)
  except (ValueError,OverflowError):raise AssertionError(('invalid finite double',value))
  assert math.isfinite(converted),value
 elif kind=='array':
  assert type(obj['items']) is list
  for item in obj['items']:typed_value(item)
def ast(obj,ids,functions,operators,allow_null=True):
 if obj is None:assert allow_null;return
 assert type(obj) is dict and type(obj.get('class')) is str,obj
 kind=obj['class'];identity=obj.get('id')
 assert 'id' in obj and (identity is None or (type(identity) is str and identity in ids)),identity
 if kind in LITERAL_KINDS:
  exact_keys(obj,{'id','class','value'});typed_value(obj['value']);assert obj['value']['kind']==LITERAL_KINDS[kind]
  return
 if kind=='NoneLiteral':exact_keys(obj,{'id','class'});return
 if kind=='Probe':
  exact_keys(obj,{'id','class','value','fault'});typed_value(obj['value'])
  assert type(obj['fault']) is str and obj['fault'] in ('','optimize','evaluate')
  return
 if kind=='FunctionNode':
  exact_keys(obj,{'id','class','name','children'});name=obj['name']
  assert type(name) is str and name in functions,name
  minimum,maximum=functions[name]
 elif kind=='BinaryOpNode':
  exact_keys(obj,{'id','class','op','children'})
  assert type(obj['op']) is int and obj['op'] in operators
  minimum=maximum=2
 elif kind in ('OrNode','AndNode'):
  exact_keys(obj,{'id','class','children'});minimum=maximum=2
 elif kind in ('NotNode','NegateNode'):
  exact_keys(obj,{'id','class','children'});minimum=maximum=1
 else:raise AssertionError(('unknown AST producer class',kind))
 children=obj['children'];assert type(children) is list
 assert (minimum<0 or len(children)>=minimum) and (maximum<0 or len(children)<=maximum)
 for child in children:ast(child,ids,functions,operators)
def trace(events,allowed,ids):
 assert type(events) is list
 for event in events:
  text(event);assert len(event)>=2 and event[:2] in allowed,event
  if event[:2]=='L:':
   m=re.fullmatch(r'L:(0|-?[1-9][0-9]*):([0-9a-f]*)',event);assert m,event
   assert -(2**31)<=int(m[1])<=2**31-1;hex_text(m[2])
  else:assert event[2:] in ids,event
def row_schema(row,case,functions,operators,ids):
 exact_keys(row,ROW_FIELDS)
 assert type(row['id']) is str and row['id']==case['id']
 assert type(row['phase']) is str and row['phase'] in ('complete','compile-rejected')
 assert type(row['compileCode']) is int and row['compileCode'] in (0,8,12,15)
 for field in ('errorUtf8Hex','compileMessageUtf8Hex','runtimeError1Utf8Hex','runtimeError2Utf8Hex'):hex_text(row[field])
 assert row['errorUtf8Hex']=='',row['errorUtf8Hex']
 context=row['context'];exact_keys(context,{'dirs','providerNull','sharedContextNull','lineIndex'})
 assert type(context['dirs']) is int and context['dirs']==case['dirs']
 assert type(context['lineIndex']) is int and context['lineIndex']==-1
 assert type(context['providerNull']) is bool and context['providerNull'] is True
 assert type(context['sharedContextNull']) is bool and context['sharedContextNull'] is True
 ast(row['before'],ids,functions,operators,False);ast(row['after'],ids,functions,operators,False)
 trace(row['optimizeTrace'],('O:','E:','L:'),ids)
 trace(row['evaluateTrace1'],('E:','L:'),ids);trace(row['evaluateTrace2'],('E:','L:'),ids)
 for field in ('readsCompile','reads1','reads2'):
  assert type(row[field]) is list
  for path in row[field]:assert type(path) is str and path=='inputs/'+case['id']+'.tsv'
 if row['compileCode']==0:
  assert row['phase']=='complete' and row['compileMessageUtf8Hex']==''
  for index in (1,2):
   result=row['value'+str(index)];error=row['runtimeError'+str(index)+'Utf8Hex']
   assert (result is not None) != bool(error),(index,result,error)
   if result is not None:typed_value(result)
 else:
  assert row['phase']=='compile-rejected' and row['compileMessageUtf8Hex']!=''
  assert row['value1'] is None and row['value2'] is None
  for field in ('evaluateTrace1','evaluateTrace2','readsCompile','reads1','reads2'):assert row[field]==[],field
  assert row['runtimeError1Utf8Hex']==row['runtimeError2Utf8Hex']==''
