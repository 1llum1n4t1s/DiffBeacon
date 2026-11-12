import json,pathlib
O=pathlib.Path(__file__).resolve().parent
def lit(v):
 if v is None:return {'op':'none'},'None'
 if isinstance(v,bool):return {'op':'bool','integer':int(v)},str(v).lower()
 if isinstance(v,int) and v in [9223372036854775807,-9223372036854775808]:return f('ToInt',str(v))
 if isinstance(v,int):return {'op':'integer','integer':v},str(v)
 if isinstance(v,list):return call('Array',*[lit(x) for x in v])
 expression=json.dumps(v,ensure_ascii=False)
 if '\r' in v or '\n' in v:expression=expression.replace('\\r','\r').replace('\\n','\n')
 return {'op':'string','text':v},expression
def call(name,*args):return {'op':name.lower(),'children':[x[0] for x in args]},name+'('+', '.join(x[1] for x in args)+')'
def f(name,*args):return call(name,*[lit(x) for x in args])
sym={'add':'+','sub':'-','mul':'*','div':'/','mod':'%','eq':'==','ne':'!=','lt':'<','le':'<=','gt':'>','ge':'>='}
def op(name,a,b):return {'op':name,'children':[a[0],b[0]]},'('+a[1]+' '+sym[name]+' '+b[1]+')'
def b(name,a,v):return op(name,a,lit(v))
cases=[]
def add(id,a,note='',lines=None,mask=7,pane=0,second=None):
 groups=[{'mask':mask,'ast':[a[0]],'expressions':['@cs @noopt '+a[1]]}]
 if second:groups.append({'mask':7,'ast':[second[0]],'expressions':['@cs @noopt '+second[1]]})
 cases.append(dict(id='arrays-'+id,table=False,delimiter=',',quote='"',pane=pane,lines=lines or ['source\r\n'],groups=groups,note=note))
for id,value in [('empty',[]),('ints',[1,2]),('mixed',[1,True,'x',None]),('nested',[[],[1,[2]],False]),('utf8',['あ','😀','é']),('eol-values',['x\ny','z\r\n'])]:add('value-'+id,lit(value))
for idx in [0,1,-1,-2,-3,2,9223372036854775807,-9223372036854775808]:add('at-'+str(idx).replace('-','neg'),f('At',['a','b'],idx))
for id,a,idx in [('empty',[],0),('string','xy',0),('none',None,0),('nonint',[1,2],'1'),('bool-index',[1,2],True),('nested',[[1,2],[3]],0)]:add('at-'+id,f('At',a,idx))
for name in ['AnyOf','AllOf']:
 for id,a in [('empty',[]),('mixed',[True,1,'true',None]),('false',[False,False]),('true',[True,True]),('nested',[[True]]),('scalar-bool',True),('scalar-int',1)]:add(name.lower()+'-'+id,f(name,a))
for id,args in [('empty',([],)),('one',([None],)),('equal',([2,2],)),('unequal',([2,3],)),('strict',([1,True],)),('nested',([[1,None],[1,None]],)),('nested-ne',([[1],[1,2]],)),('scalar-many',(1,1,1)),('scalar-strict',(1,'1')),('array-first-ignore',([1,1],99))]:add('allequal-'+id,f('AllEqual',*args))
for name in ['AndEach','OrEach']:
 for id,a,bv in [('scalar',True,False),('lift-left',[True,False],True),('lift-right',False,[True,False]),('mismatch',[True,False,True],[True]),('mixed',[True,1,None],[False,True,True]),('nested',[[True],[False]],True),('empty',[],True)]:add(name.lower()+'-'+id,f(name,a,bv))
for id,a in [('scalar',True),('mixed',[True,False,1,None]),('nested',[[True],[False],[]])]:add('noteach-'+id,f('NotEach',a))
for id,a,t,z in [('scalar',True,[1,2],[3,4]),('lift',[True,False,True],'Y','N'),('mismatch',[True,False,True],[1],[2]),('mixed',[1,None,True],['a','b','c'],'N'),('nested',[[True],[],[1]],'Y','N')]:add('ifeach-'+id,f('IfEach',a,t,z))
divzero=op('div',lit(1),lit(0))
add('if-lazy-true',call('If',lit(True),lit('ok'),divzero))
add('if-lazy-false',call('If',lit(False),divzero,lit('ok')))
add('if-bool-true-any',f('If',[False,True],'yes','no'))
add('if-bool-int-not-true',f('If',[1,'true'],'yes','no'))
add('ifeach-eager-true',call('IfEach',lit(True),lit('ok'),divzero))
add('ifeach-eager-empty',call('IfEach',lit([]),divzero,lit('ok')))
for id,i in [('neg',-1),('zero',0),('one',1),('max',9223372036854775807),('min',-9223372036854775808),('nonint','1'),('array',[0,1])]:add('choose-'+id,f('Choose',i,'a','b'))
add('choose-lazy',call('Choose',lit(0),lit('ok'),divzero))
add('chooseeach-mixed',f('ChooseEach',[-1,0,1,9,None,True],'a','b'))
add('chooseeach-nested',f('ChooseEach',[[0],1],'a','b'))
# Arithmetic cases isolate original compute; avoid signed-overflow undefined behavior.
for id,name,a,v in [('lift-left','add',[1,2],3),('lift-right','sub',10,[1,2]),('mismatch','mul',[2,3],[4]),('nested','add',[[1],[2,3]],10),('eq-array','eq',[1,None],[1,None]),('eq-length','eq',[1],[1,2]),('ne-length','ne',[],[1]),('eq-lift','eq',[1,2],1),('bool-sub','sub',True,False),('str-int','add','x',2),('none-str','add',None,'x'),('div-int','div',5,2),('mod-negative','mod',-5,2)]:add('compute-'+id,op(name,lit(a),lit(v)))
for id,a in [('empty',[]),('nested',[[1],[2,3]]),('utf8','あ😀é'),('none',None),('int',9)]:add('len-'+id,f('Len',a))
add('trim-lift',f('Trim',[' x ',None,1,[' y ']]))
add('outer-eols',lit([1,False,None]),lines=['a\r\n','b\n','c\r','d'])
add('target-skipped',lit([1,2]),mask=1,pane=1)
add('target-included',lit([1,2]),mask=2,pane=1)
# Group chaining: subsequent Line observes previous transformation.
add('group-chain',lit([1,2]),second=call('Array',({'op':'line'},'Line'),'literal' if False else lit('x')))
omit={'arrays-at-1','arrays-at-bool-index','arrays-anyof-scalar-bool','arrays-allof-scalar-bool','arrays-allequal-one','arrays-allequal-scalar-many','arrays-andeach-scalar','arrays-oreach-scalar','arrays-oreach-empty','arrays-noteach-scalar','arrays-choose-zero','arrays-choose-one','arrays-choose-array','arrays-len-int','arrays-compute-ne-length'}
cases=[c for c in cases if c['id'] not in omit]
assert 50<=len(cases)<=90,len(cases)
(O/'cases.json').write_text(json.dumps(cases,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(len(cases))
