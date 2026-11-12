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

P=O
cases=[]
def add(id,a,ci=False,lines=None,table=False,mask=7,pane=0,second=None):
 groups=[dict(mask=mask,caseSensitive=not ci,ast=[a[0]],expressions=[('@ci' if ci else '@cs')+' @noopt '+a[1].replace('range(', 'ColumnRange(')])]
 if second:groups.append(dict(mask=7,caseSensitive=True,ast=[second[0]],expressions=['@cs @noopt '+second[1].replace('range(', 'ColumnRange(')]))
 cases.append(dict(id='search-'+id,table=table,delimiter=',',quote='"',pane=pane,lines=lines or ['source\r\n'],groups=groups,note='fixed explicit AST; parser/optimizer not executed'))
for name in ['StrCount','StrFind']:
 for id,src,needle in [('empty','', 'a'),('absent','abc','x'),('overlap','aaaaa','aa'),('utf8','あx😀あ','あ'),('utf8-byte','あx😀あ','😀'),('int',12121,'21'),('bool',True,'r'),('none',None,'a'),('array',['aaaa',None,123,True], 'a'),('nested',[['ab','ab'],[],['abab']], 'ab'),('needle-empty','abc',''),('needle-int','123',123),('needle-bool','true',True),('needle-array','abc',['a']),('needle-none','abc',None)]:add(name.lower()+'-'+id,f(name,src,needle))
 for id,src,needle in [('ascii','aAbBAA','Aa'),('nonascii','ÄäÉéA','ä'),('mixed','あA😀a','A')]:add(name.lower()+'-ci-'+id,f(name,src,needle),ci=True)
 for args in [(),('abc',),('abc','a',0,1)]:add(name.lower()+'-arity-'+str(len(args)),f(name,*args))
for id,occ in [('zero',0),('one',1),('two',2),('neg',-1),('max',9223372036854775807),('min',-9223372036854775808),('str','1'),('bool',True),('none',None),('array',[1])]:add('find-occ-'+id,f('StrFind','aaaaa','aa',occ))
add('find-occ-toint',call('StrFind',lit('aaaaa'),lit('aa'),f('ToInt','1')))
div=op('div',lit(1),lit(0))
for name in ['StrCount','StrFind']:
 add(name.lower()+'-source-error',call(name,div,lit('')))
 add(name.lower()+'-needle-error',call(name,lit(None),div))
 add(name.lower()+'-lazy',call('If',lit(True),f(name,'aaaa','aa'),div))
add('find-occ-eager',call('StrFind',lit(None),lit(''),div))
add('find-badneedle-before-occ',call('StrFind',lit('a'),div,lit(None)))
line=({'op':'line'},'Line');count=({'op':'count'},'ColumnCount');number=({'op':'number'},'LineNumber')
add('dynamic-line',call('StrFind',line,lit('a')),lines=['あa\r\n','ba\n','zz\r','a'])
add('dynamic-occ',call('StrFind',lit('aaaaa'),lit('a'),number),lines=['x\r\n','y\n','z'])
add('table-raw',call('StrCount',line,lit(',')),table=True,lines=['"a,b",c\r\n','a,"b,c"\n'])
add('table-range',call('StrFind',call('range',lit('2,1')),lit(',')),table=True,lines=['"a,b",c\r\n'])
add('skip-mask',f('StrCount','aaaa','a'),mask=1,pane=1)
add('include-mask',f('StrFind','aaaa','a',1),mask=2,pane=1)
add('group-chain',f('StrCount','aaaa','a'),second=call('StrFind',line,lit('4')))
(P/'cases.json').write_text(json.dumps(cases,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
