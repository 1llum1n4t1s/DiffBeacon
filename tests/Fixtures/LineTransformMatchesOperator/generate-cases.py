"""Describe inputs only; expected results come exclusively from the native process."""
import json
import pathlib
BASE = pathlib.Path(__file__).resolve().parent
def s(v): return dict(type='string', value=v)
def a(*v): return dict(type='array', value=list(v))
def n(): return dict(type='none')
def i(v): return dict(type='integer', value=v)
def d(v): return dict(type='dynamic', value=v)
def expr(v):
    t = v['type']; x = v.get('value')
    if t == 'string': return '"'+x.replace('"', '""')+'"'
    if t == 'none': return 'none'
    if t == 'integer': return 'ToInt('+str(x)+')'
    if t == 'bool': return str(x).lower()
    if t == 'array': return 'Array('+','.join(map(expr, x))+')'
    if t == 'dynamic': return 'If(LineNumber>0,'+expr(x)+',none)'
    if t == 'throw': return '1/(LineNumber-LineNumber)'
    raise ValueError(t)
cases = []
def add(name, left, right, opt=True, ci=False, neg=False):
    expression = expr(left)+(' not matches ' if neg else ' matches ')+expr(right)
    cases.append(dict(id=name, left=left, right=right, optimize=opt, caseSensitive=not ci, negate=neg,
                      expression=('@noopt ' if not opt else '')+('@ci ' if ci else '@cs ')+expression))
scalar = [('exact','abc','abc'),('prefix','abc','ab'),('suffix','abc','bc'),('substring','abc','b'),
 ('empty-empty','',''),('empty-star','','a*'),('empty-anchor','','^$'),('empty-pattern','a',''),
 ('empty-first','a','|a'),('nonempty-first','a','a|'),('first-partial','ab','a|ab'),('first-full','ab','ab|a'),
 ('lazy-partial','aa','a*?'),('greedy-full','aa','a*'),('zero-first','a',r'\b|a'),('lookahead','a','(?=a)|a'),
 ('anchor-newline','a\n','a$'),('crlf-dot','a\r\nb','a.*b'),('capture-backref','abab',r'(ab)\1'),
 ('unicode-dot','😀','.'),('unicode-pair','é😀','..'),('case-sensitive','AbC','abc'),
 ('inline-ci','AbC','(?i)abc'),('optional','ab','a?b'),('class','123','[0-9]+')]
for name, text, pattern in scalar:
    for opt in [False, True]: add(name+('-opt' if opt else '-raw'), s(text), s(pattern), opt)
for opt in [False, True]:
    add('ci'+str(opt), s('AbC'), s('abc'), opt, ci=True)
    add('ci-unicode'+str(opt), s('É'), s('é'), opt, ci=True)
for name, left, right in [
 ('left-array',a(s('a'),s('b'),n(),i(1)),s('a')),('right-array',s('a'),a(s('a'),s('b'),n())),
 ('both-array',a(s('a'),s('b'),s('c')),a(s('a'),s('x'))),('nested-array',a(a(s('a')),s('b')),s('a')),
 ('empty-left',a(),s('a')),('empty-right',s('a'),a()),
 ('mixed-types',a(i(1),n(),dict(type='bool',value=True),s('1')),s('1'))]:
    for opt in [False, True]:
        for neg in [False, True]: add(name+str(opt)+str(neg),left,right,opt,neg=neg)
for name, left, right in [
 ('none-left',n(),s('a')),('number-left',i(1),s('1')),('none-right',s('a'),n()),('number-right',s('1'),i(1)),
 ('invalid',s('a'),s('[')),('invalid-nonstring',n(),s('[')),('invalid-empty-array',a(),s('[')),
 ('dynamic-invalid',s('a'),d(s('['))),('dynamic-invalid-none',n(),d(s('['))),('dynamic-valid',s('aa'),d(s('a+')))]:
    for opt in [False, True]: add(name+str(opt),left,right,opt)
for name, left, right in [('order',s('a'),s('a')),('left-throw',dict(type='throw',value='left'),s('a')),
 ('right-throw',s('a'),dict(type='throw',value='right')),('none-right-throw',n(),dict(type='throw',value='right'))]:
    add(name,left,right,False)
BASE.joinpath('cases.json').write_text(json.dumps(cases,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(len(cases))
