"""Describe native inputs only; never calculate expected search results."""
import json
import pathlib

BASE = pathlib.Path(__file__).resolve().parent
source = (BASE.parent / 'LineTransformMatchesOperator/generate-cases.py').read_text(encoding='utf-8')
# Reuse the established input matrix without executing its file writer.
source = source[:source.index("BASE.joinpath('cases.json')")]
scope = {'__file__': str(BASE / 'generate-cases.py')}
exec(source.replace(' not matches ', ' not recontains ').replace(' matches ', ' recontains '), scope)
cases = scope['cases']
for tag, text, pattern in [
    ('dot-lf', '\n', '.'), ('dot-cr', '\r', '.'), ('dot-crlf', '\r\n', '.'),
    ('dollar-cr', 'a\r', 'a$'), ('dollar-crlf', 'a\r\n', 'a$'),
    ('Z-lf', 'a\n', r'a\Z'), ('Z-cr', 'a\r', r'a\Z'), ('Z-crlf', 'a\r\n', r'a\Z'),
    ('multiline-lf', 'a\nb', '(?m)^b'), ('multiline-cr', 'a\rb', '(?m)^b'),
    ('multiline-crlf', 'a\r\nb', '(?m)^b'), ('multiline-crlf-lf', 'a\r\nb', '(?m)^\nb'),
    ('multiline-dollar-lf', 'a\nb', '(?m)a$'), ('multiline-dollar-cr', 'a\rb', '(?m)a$'),
    ('multiline-dollar-crlf', 'a\r\nb', '(?m)a$'),
    ('comment-lf', 'x', '(?x)#comment\nb'), ('comment-cr', 'x', '(?x)#comment\rb'),
    ('comment-crlf', 'x', '(?x)#comment\r\nb'),
    ('caret-final-cr', 'a\r', r'(?m)(?<=\r)^$'),
    ('caret-final-crlf', 'a\r\n', r'(?m)(?<=\r)^\n$'),
    ('caret-empty', '', '(?m)^$'), ('caret-final-only', '\r', r'(?m)^\z')]:
    for opt in [False, True]:
        scope['add']('newline-'+tag+'-'+str(opt), scope['s'](text), scope['s'](pattern), opt)
for case in cases:
    case['public'] = True
    case['inputHex'] = b'probe'.hex()
    case['encoding'] = 'utf8'

texts = [('empty', ''), ('bom-only', ''), ('no-eol', 'abc'), ('final-lf', 'abc\n'),
         ('final-cr', 'abc\r'), ('final-crlf', 'abc\r\n'), ('empty-line', '\n'),
         ('empty-lines', '\r\n\n\r'), ('mixed', 'abc\rdef\nghi\r\njkl'),
         ('unicode', 'é😀\n日本語'), ('quoted-raw', '"abc\ndef",tail\r\nlast,row')]
patterns = [('empty-pattern', ''), ('empty-anchor', '^$'), ('substring', 'b'),
            ('end', '$'), ('eol', '[\\r\\n]'), ('across', 'c\\rd'),
            ('unicode', '😀|日本'), ('last-line', '^jkl$'), ('invalid', '[')]
for encoding in ['utf8', 'utf8bom', 'utf16le', 'utf16be', 'cp1252']:
    for name, text in texts:
        if encoding == 'cp1252' and name == 'unicode':
            continue
        if name == 'bom-only' and encoding == 'cp1252':
            continue
        codec = dict(utf8='utf-8', utf8bom='utf-8', utf16le='utf-16le', utf16be='utf-16be', cp1252='cp1252')[encoding]
        bom = dict(utf8=b'', utf8bom=b'\xef\xbb\xbf', utf16le=b'\xff\xfe', utf16be=b'\xfe\xff', cp1252=b'')[encoding]
        if name == 'empty':
            bom = b''
        data = bom + text.encode(codec)
        for pattern_name, pattern in patterns:
            for opt in [False, True]:
                # No row means the public transform cannot expose a key; native still measures EOF.
                public = bool(text) and encoding != 'cp1252'
                p = scope['s'](pattern)
                expression = ('@cs ' if opt else '@noopt @cs ') + 'Content recontains ' + scope['expr'](p)
                cases.append(dict(id=f'content-{encoding}-{name}-{pattern_name}-{opt}',
                    left=dict(type='content', value=encoding+':'+data.hex()), right=p,
                    optimize=opt, caseSensitive=True, negate=False, expression=expression,
                    inputHex=data.hex(), encoding=encoding, public=public))
# Explicit legacy-codepage non-ASCII bodies have observable keys and exercise the distinct EOF branch.
for text in ['café', 'café\n', '\n', 'café\r\n']:
    for pattern in ['^$', 'fé', '[\\r\\n]', '']:
        for opt in [False, True]:
            data = text.encode('cp1252')
            cases.append(dict(id=f'codepage-{len(cases)}', left=dict(type='content', value='cp1252:'+data.hex()),
                right=scope['s'](pattern), optimize=opt, caseSensitive=True, negate=False,
                expression=('@cs ' if opt else '@noopt @cs ')+'Content recontains '+scope['expr'](scope['s'](pattern)),
                inputHex=data.hex(), encoding='cp1252', public=True))
for opt in [False, True]:
    for left, right, tag in [(dict(type='content', value='utf8:616263'), scope['d'](scope['s']('[')), 'invalid'),
                             (scope['a'](dict(type='content', value='utf8:616263'), scope['n']()), scope['s']('b'), 'array')]:
        def expression(v):
            if v['type'] == 'content': return 'Content'
            if v['type'] == 'array': return 'Array('+','.join(map(expression,v['value']))+')'
            return scope['expr'](v)
        cases.append(dict(id=f'content-{tag}-{opt}',left=left,right=right,optimize=opt,caseSensitive=True,negate=False,
            expression=('@cs ' if opt else '@noopt @cs ')+expression(left)+' recontains '+expression(right),
            inputHex='616263',encoding='utf8',public=True))
(BASE/'cases.json').write_text(json.dumps(cases, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print(len(cases))
