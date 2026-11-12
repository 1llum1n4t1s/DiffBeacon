"""Deterministic native input plan; no expected values are calculated."""
import sys
sys.dont_write_bytecode = True
import json, pathlib, argparse
BASE = pathlib.Path(__file__).resolve().parent

def cases():
    def n(t, value=None): return dict(type=t, **({} if value is None else dict(value=value)))
    def b(v): return n('bool', v)
    functions = [('matchcount','MatchCountFunc'),('matchnumber','LineMatchNumberFunc'),('matchdistance','LineMatchDistanceFunc'),('matchdistancebefore','LineMatchDistanceBeforeFunc'),('matchdistanceafter','LineMatchDistanceAfterFunc')]
    shapes = [
        ('empty', []), ('single-true',[b(True)]), ('single-false',[b(False)]),
        ('none-match',[b(False)]*5), ('all-match',[b(True)]*5),
        ('first',[b(True),b(False),b(False),b(False),b(False)]),
        ('last',[b(False),b(False),b(False),b(False),b(True)]),
        ('middle',[b(False),b(False),b(True),b(False),b(False)]),
        ('edges-tie',[b(True),b(False),b(False),b(False),b(True)]),
        ('alternating',[b(True),b(False),b(True),b(False),b(True)]),
        ('blocks',[b(False),b(True),b(True),b(False),b(False),b(True),b(True),b(False)]),
        ('typed-none',[n('none')]*3), ('typed-integer',[n('integer',1),n('integer',0),n('integer',-1)]),
        ('typed-double',[n('double',1.0),n('double',0.0),n('double',-1.0)]),
        ('typed-string',[n('string','true'),n('string','false'),n('string','')]),
        ('array-direct',[n('array',[b(False),b(True)]),n('array',[b(False)]),n('array',[])]),
        ('array-nested',[n('array',[n('array',[b(True)])]),n('array',[n('array',[b(True)]),b(False)]),n('array',[n('array',[b(False)]),b(True)])]),
        ('mixed',[b(True),n('integer',1),n('none'),n('string','true'),n('array',[b(True)]),b(False)]),
        ('timestamp',[n('timestamp',0),n('timestamp',1)]),
        ('raw-string',[n('raw-string',[255,0,97]),n('string','true')]),
    ]
    result=[]
    for public,native in functions:
        def add(name,rows,mode='scan'):
            result.append(dict(id=public+'-'+name,function=public,nativeFunction=native,rows=rows,before=n('none'),after=n('none'),mode=mode))
        for name,rows in shapes:add(name,rows)
        for mode in ['throw-scan','different-ast','new-context','mutate-cached','nested-match','nested-aggregate','outer-throw-after-inner','null-provider','shared-functions','outside-lines']:
            add(mode,[b(False),b(True),b(False),b(True),b(False)],mode)
        add('throw-first',[n('throw','predicate-first'),b(True),b(False)])
        add('throw-last',[b(True),b(False),n('throw','predicate-last')])
    assert len({x['id'] for x in result})==len(result)
    return result

if __name__ == '__main__':
    p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);a=p.parse_args()
    from capture import validate
    out=validate(a.output,BASE.parents[2]);assert not out.exists();out.parent.mkdir(parents=True,exist_ok=True)
    out.write_text(json.dumps(cases(),ensure_ascii=False,indent=2)+'\n',encoding='utf8',newline='\n')
    print(len(cases()))
