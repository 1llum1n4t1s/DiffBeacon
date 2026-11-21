"""Deterministic input plan. No expected regex results are computed here."""
import json
import pathlib


def cases():
    rows = []
    def n(t, v=None, **extra):
        return dict(type=t, **({} if v is None else dict(value=v)), **extra)
    def s(v): return n('string', v)
    def i(v): return n('integer', v)
    def add(fn, name, args, *, cs=True, opt=False, null=False):
        rows.append(dict(id=fn+'-'+name, function='regex'+fn,
                         mode='optimizer-selected-branch' if opt else 'function-body-noopt',
                         caseSensitive=cs, nullArgs=null, args=args))

    common = [
        ('default', 'abc abc', 'abc'), ('no-match', 'abc', 'xyz'),
        ('empty-input', '', '.'), ('empty-pattern', 'abc', ''),
        ('empty-both', '', ''), ('non-overlap', 'ababa', 'aba'),
        ('non-overlap-dense', 'aaaaa', 'aa'), ('greedy', 'ab12ab34', 'ab.*'),
        ('lazy', 'ab12ab34', 'ab.*?'), ('alternation', 'ababa', 'ab|a'),
        ('anchor-start', 'a\na', '^a'), ('anchor-end', 'a\na', 'a$'),
        ('cr-newline-default', 'a\ra', '^a'), ('multiline', 'a\ra', '(?m)^a'),
        ('dot-no-newline', 'a\rb', '.'), ('dotall', 'a\rb', '(?s).'),
        ('byte-offset-bmp', 'é猫a猫', '猫'), ('byte-offset-nonbmp', '😀a😀', 'a|😀'),
        ('unicode-word', '猫 é 123 😀', r'\w+'), ('unicode-digit', 'a١２3', r'\d+'),
        ('unicode-space', 'a\u2003b', r'\s'), ('unicode-property', 'a猫😀', r'\p{Han}'),
        ('zero-start', 'abc', '^'), ('zero-end', 'abc', '$'),
        ('zero-lookahead', 'aba', '(?=a)'), ('zero-ascii', 'abc', '(?:)'),
        ('zero-bmp', 'éa', '(?:)'), ('zero-nonbmp', '😀a', '(?:)'),
        ('lookbehind', 'xabxab', '(?<=x)ab'), ('backreference', 'aa bb cc', r'(\w)\1'),
        ('named-group', '猫12', r'(?<word>\w+?)(?<num>\d+)'),
        ('nul-subject', 'a\0b', '.'), ('nul-pattern', 'a\0b', '\0'),
        ('invalid-open', 'abc', '['), ('invalid-close', 'abc', '('),
        ('invalid-backref', 'abc', r'\9'), ('invalid-property', 'abc', r'\p{NoSuchProperty}'),
    ]
    for fn in ['find', 'count', 'extract']:
        for name, text, pattern in common:
            add(fn, name, [s(text), s(pattern)])
        for name, text, pattern in [('ascii', 'AaA', 'a'), ('kelvin', 'KkK', 'k'),
                                    ('sigma', 'Σσς', 'σ'), ('long-s', 'ſsS', 's'),
                                    ('sharp-s', 'ßẞss', 'ß'), ('turkish', 'Iİıi', 'i')]:
            for cs in [True, False]:
                add(fn, name+('-cs' if cs else '-ci'), [s(text), s(pattern)], cs=cs)
        for cs in [True, False]:
            for typed_cs in [True, False]:
                add(fn, f'typed-context-{cs}-regex-{typed_cs}',
                    [s('AaKk'), n('regex', 'a|k', caseSensitive=typed_cs)], cs=cs)
        for name, source in [('none', n('none')), ('integer', i(123)),
                             ('bool', n('bool', True)), ('double', n('double', 1.25)),
                             ('array-empty', n('array', [])),
                             ('array-mixed', n('array', [s('aba'), n('none'), i(101), n('bool', True), s('')])),
                             ('array-nested', n('array', [n('array', [s('aba'), s('aba')]), s('aba')])),
                             ('array-invalid', n('array', [s('abc'), s('def')]))]:
            add(fn, name, [source, s('[' if name == 'array-invalid' else 'a|1|true')])
        for name, pattern in [('none', n('none')), ('integer', i(1)), ('bool', n('bool', True)),
                              ('array', n('array', [s('a')]))]:
            add(fn, 'pattern-'+name, [s('a'), pattern])
        add(fn, 'raw-invalid-utf8', [n('raw-string', [97, 255, 98]), s('.')])
        add(fn, 'raw-invalid-pattern-utf8', [s('abc'), n('raw-string', [255])])
        add(fn, 'null-args', [], null=True)
        for count in [0, 1, 3, 4, 5]:
            if (fn != 'count' and count in [3, 4]): continue
            add(fn, 'arity-'+str(count), [s('abc'), s('a'), i(0), i(0), i(0)][:count])
        add(fn, 'eval-first-throws', [n('throw', 'first'), n('throw', 'second')])
        add(fn, 'eval-pattern-throws', [s('abc'), n('throw', 'pattern')])
        for name, pattern in [('literal', s('a')), ('typed', n('regex', 'a')),
                              ('invalid', s('['))]:
            add(fn, 'optimizer-'+name, [s('Aa'), pattern], opt=True)
        add(fn, 'optimizer-none-source', [n('none'), s('a')], opt=True)
        add(fn, 'optimizer-dynamic-source', [n('dynamic', s('Aa')), s('a')], opt=True, cs=False)
        add(fn, 'optimizer-dynamic-pattern', [s('Aa'), n('dynamic', s('a'))], opt=True, cs=False)

    for fn in ['find', 'extract']:
        for name, text, pattern, group in [
            ('group-one', 'xxab12', r'(ab)(\d+)', 1),
            ('group-two', 'éab12', r'(ab)(\d+)', 2),
            ('unmatched-middle', 'b', '(a)?(b)', 1),
            ('unmatched-trailing', 'b', '(b)(a)?', 2),
            ('empty-group', 'b', '()(b)', 1),
            ('group-outside', 'a', '(a)', 2),
            ('group-negative', 'a', '(a)', -1),
            ('group-max', 'a', '(a)', 9223372036854775807),
            ('group-min', 'a', '(a)', -9223372036854775808),
            ('group-zero', 'é😀ab', '(ab)', 0),
        ]:
            add(fn, name, [s(text), s(pattern), i(group)])
        for group in [n('none'), s('0'), n('double', 0.0), n('bool', False), n('array', [])]:
            add(fn, 'group-type-'+group['type'], [s('abc'), s('(a)'), group])
        for occurrence in [-9223372036854775808, -1, 0, 1, 2, 3, 9223372036854775807]:
            add(fn, 'occurrence-'+str(occurrence), [s('éa😀a'), s('(a)'), i(1), i(occurrence)])
        for occurrence in [n('none'), s('0'), n('double', 0.0), n('bool', False), n('array', [])]:
            add(fn, 'occurrence-type-'+occurrence['type'], [s('abc'), s('a'), i(0), occurrence])
        for name, text, pattern, occurrence in [
            ('zero-ascii-last', 'ab', '(?:)', 2), ('zero-ascii-past-end', 'ab', '(?:)', 3),
            ('zero-empty-first', '', '', 0), ('zero-empty-past-end', '', '', 1),
            ('zero-end-past-end', 'ab', '$', 1), ('zero-bmp-second', 'éa', '(?:)', 1),
            ('zero-nonbmp-second', '😀a', '(?:)', 1),
            ('zero-lookahead-nonbmp', '😀a', '(?=a)', 1),
        ]:
            add(fn, name, [s(text), s(pattern), i(0), i(occurrence)])
        add(fn, 'eval-group-throws', [s('a'), s('a'), n('throw', 'group')])
        add(fn, 'eval-occurrence-throws', [s('a'), s('a'), i(0), n('throw', 'occurrence')])
        add(fn, 'bad-group-stops-occurrence', [s('a'), s('a'), s('bad'), n('throw', 'unreached')])
        add(fn, 'optimizer-group', [s('éab'), s('(ab)'), i(1)], opt=True)
        add(fn, 'optimizer-occurrence', [s('éab ab'), s('(ab)'), i(1), i(1)], opt=True)
        add(fn, 'optimizer-no-match', [s('a'), s('b')], opt=True)
        add(fn, 'optimizer-dynamic-occurrence', [s('a a'), s('a'), i(0), n('dynamic', i(1))], opt=True)
        add(fn, 'optimizer-negative-occurrence', [s('a'), s('a'), i(0), i(-1)], opt=True)
    # Append only: preserve the 325 original inputs and their ordering.
    for name,text,pattern,replacement in [
        ('default','aba aba','a','X'),('no-match','abc','z','X'),('delete','aaaa','a',''),
        ('empty-input','','a','X'),('empty-pattern','abc','','X'),('empty-both','','','X'),
        ('non-overlap','ababa','aba','X'),('rematches','aa','a','aa'),
        ('terminal','ab','$','X'),('terminal-nonempty','ab','b$','X'),
        ('zero-before','ab','(?=a)','X'),('zero-middle','abc','(?=b)','X'),
        ('zero-all','abc','(?:)','X'),('zero-delete','abc','(?:)',''),
        ('zero-unicode','é😀a','(?:)','X'),('lookbehind','ab','(?<=a)b','X'),
        ('nul-subject','a\0b\0a','a','X'),('nul-pattern','a\0b','\0','X'),
        ('nul-replacement','aba','a','X\0Y'),('unicode','é😀日本é','é|😀','界'),
        ('invalid-pattern','abc','[','X'),('groups','ab12 ab34',r'(ab)(\d+)','$2:$1:$0'),
        ('unmatched-middle','b','(a)?(b)','$1'),('unmatched-middle-zero','b','(a)?(b)','$0:$2'),
        ('unmatched-trailing','b','(b)(a)?','$2'),('unmatched-trailing-zero','b','(b)(a)?','$0:$1'),
        ('empty-capture','b','()(b)','$1:$2'),
        ('ten-captures','abcdefghij','(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)','$0:$1:$9:$10:$99')]:
        add('replace',name,[s(text),s(pattern),s(replacement)])
    for replacement in ['$'+str(i) for i in range(10)]+['$10','$99','$$','${1}',r'\1',r'\$1','$','$$1','$x','$01']:
        add('replace','syntax-'+replacement.encode().hex(),[s('ab'),s('(a)(b)'),s(replacement)])
    for name,source in [('none',n('none')),('int',i(101)),('bool',n('bool',True)),('double',n('double',1.25)),('array-empty',n('array',[])),('array-mixed',n('array',[s('aba'),n('none'),i(101),n('bool',True)])),('array-nested',n('array',[n('array',[s('aba')]),s('aba')]))]:
        add('replace','source-'+name,[source,s('a|1|true'),s('X')])
        add('replace','invalid-pattern-source-'+name,[source,s('['),s('X')])
    for arg in [n('none'),i(1),n('bool',False),n('double',1.0),n('array',[])]:
        add('replace','pattern-type-'+arg['type'],[s('a'),arg,s('X')])
        add('replace','replacement-type-'+arg['type'],[s('a'),s('a'),arg])
    for cs in [True,False]:
        add('replace','context-'+str(cs),[s('AaÉé'),s('a|é'),s('X')],cs=cs)
        for typed_cs in [True,False]:
            add('replace','typed-'+str(cs)+'-'+str(typed_cs),[s('AaÉé'),n('regex','a|é',caseSensitive=typed_cs),s('X')],cs=cs)
    add('replace','raw-invalid-utf8',[n('raw-string',[97,255,98]),s('.'),s('X')])
    add('replace','raw-invalid-pattern',[s('a'),n('raw-string',[255]),s('X')])
    add('replace','raw-replacement',[s('a'),s('a'),n('raw-string',[255,0,254])])
    add('replace','null-args',[],null=True)
    for count in [0,1,2,4,5]:
        add('replace','arity-'+str(count),[s('a'),s('a'),s('X'),n('throw','extra'),i(0)][:count])
    for index in range(3):
        args=[s('a'),s('a'),s('X')]
        for j in range(index,3):args[j]=n('throw','argument-'+str(j))
        add('replace','eval-throw-'+str(index),args)
    add('replace','invalid-pattern-evaluates-replacement',[s('a'),s('['),n('throw','replacement')])
    add('replace','bad-pattern-type-evaluates-replacement',[s('a'),i(1),n('throw','replacement')])
    add('replace','none-source-evaluates-pattern',[n('none'),n('throw','pattern'),s('X')])
    for name,args in [
        ('literal',[s('Aa'),s('a'),s('X')]),('typed',[s('Aa'),n('regex','a'),s('X')]),
        ('invalid',[s('a'),s('['),s('X')]),('no-match',[s('a'),s('b'),s('X')]),
        ('unmatched-middle',[s('b'),s('(a)?(b)'),s('$1')]),('unmatched-trailing',[s('b'),s('(b)(a)?'),s('$2')]),
        ('none-source',[n('none'),s('a'),s('X')]),('dynamic-source',[n('dynamic',s('Aa')),s('a'),s('X')]),
        ('dynamic-pattern',[s('Aa'),n('dynamic',s('a')),s('X')]),('dynamic-replacement',[s('Aa'),s('a'),n('dynamic',s('X'))]),
        ('none-replacement',[s('a'),s('a'),n('none')]),('extra-arity',[s('a'),s('a'),s('X'),i(0)]),
        ('throw-replacement',[s('a'),s('a'),n('throw','replacement')])]:
        add('replace','optimizer-'+name,args,opt=True,cs=False)
    add('replace','optimizer-zero-unicode',[s('é😀a'),s('(?:)'),s('X')],opt=True)
    assert len({c['id'] for c in rows}) == len(rows)
    return rows


if __name__ == '__main__':
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument('--output', type=pathlib.Path, required=True)
    a = p.parse_args()
    if a.output.exists(): raise ValueError('refuse overwrite')
    a.output.write_text(json.dumps(cases(), ensure_ascii=False, indent=2)+'\n', encoding='utf-8', newline='\n')
    print(len(cases()))
