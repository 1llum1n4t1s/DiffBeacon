"""Source/golden-only public observation plan. Never reads managed candidate code."""
import datetime,math

class Unprojected(ValueError): pass
def quote(text):
    if '\n' in text or '\r' in text: raise Unprojected('physical-flt-line-break;use-separate-singlequote-extension-observation')
    return '"'+text.replace('"','""')+'"'
def date(value):
    try: value=datetime.datetime(1970,1,1)+datetime.timedelta(microseconds=int(value))
    except (OverflowError,ValueError): raise Unprojected('timestamp-needs-controlled-value-consumer')
    return 'd'+quote(value.isoformat(timespec='microseconds')+'Z')
OPS=dict(EQ='==',NE='!=',LT='<',LE='<=',GT='>',GE='>=',PLUS='+',MINUS='-',STAR='*',SLASH='/',MOD='%',CONTAINS='contains',RECONTAINS='recontains',MATCHES='matches',LIKE='like')
def render(n):
    k=n['node'];v=n.get('value');c=n.get('children',[])
    if k=='Probe': raise Unprojected('internal-probe-needs-public-dynamic-or-fault-witness;not-trace-equivalent')
    if k=='ArrayLiteral': raise Unprojected('internal-array-needs-per-parent-public-Array-construction-witness;not-blanket-impossible')
    if k=='RegularExpressionLiteral': raise Unprojected('internal-regex-needs-consumer-and-caseflags-witness;no-public-regex-literal')
    if k=='IntLiteral': return str(v)
    if k=='DoubleLiteral':
        if not isinstance(v,(float,int)) or not math.isfinite(v):raise Unprojected('nonfinite-literal-needs-conversion-witness')
        return repr(float(v))
    if k=='BoolLiteral':return str(v).lower()
    if k=='NoneLiteral':return 'None'
    if k=='StringLiteral':return quote(v)
    if k in ('SizeLiteral','DurationLiteral'):return v
    if k=='VersionLiteral':return 'v'+quote(v)
    if k=='DateTimeLiteral':return date(v)
    if k=='NegateNode':return '(-'+render(c[0])+')'
    if k=='NotNode':return '(not '+render(c[0])+')'
    if k in ('OrNode','AndNode'):return '('+(' or ' if k=='OrNode' else ' and ').join(map(render,c))+')'
    if k=='BinaryOpNode':return '('+render(c[0])+' '+OPS[n['op']]+' '+render(c[1])+')'
    if k=='FunctionNode':
        if n['name'] in ('now','today'):raise Unprojected('controlled-clock-host-required')
        return n['name']+'('+','.join(map(render,c))+')'
    raise Unprojected('unsupported-source-node-'+k)
def value(v):
    k=v['kind']
    if k=='none':return 'None'
    if k=='bool':return str(v['value']).lower()
    if k=='integer':return v['decimal']
    if k=='timestamp':return date(v['epochMicroseconds'])
    if k=='string':
        try:return quote(bytes.fromhex(v['utf8Hex']).decode('utf8','strict'))
        except UnicodeDecodeError:raise Unprojected('invalid-utf8-needs-byte-consumer')
    if k=='array':return 'Array('+','.join(map(value,v['items']))+')'
    if k=='double':return repr(float.fromhex(v['hexfloat']))
    raise Unprojected('opaque-native-value-needs-consumer-'+k)

def make_plan(cases,golden):
    plan=[];classification=[]
    for c in cases:
        g=golden[c['id']]
        try:
            call=render(c['tree'])
            failure=g['compileCode']!=0 or bool(g['runtimeError1Utf8Hex'])
            # 外側2要素の配列で原典valueEqualsの再帰型比較を使う。callは一度だけ評価する。
            expression='AllEqual(Array('+call+','+('None' if failure else value(g['value1']))+'))'
            mode=c['mode'];expression=('@noopt ' if mode=='noopt' else '@opt ' if mode=='opt' else '')+('@cs ' if c['caseSensitive'] else '@ci ')+expression
            scope='native-bound-public-value-consumer' if not failure else 'native-bound-public-failure-boundary;compile-vs-runtime-not-instrumented'
            if c['listio']:scope+=';first-evaluation-only;list-lifetime-not-instrumented'
            if len(expression)>16384:raise Unprojected('public-length-limit')
            plan.append(dict(id=c['id'],count=c['dirs'],expression=expression,rule='fe',selected=True,rejected=failure,scope=scope,
                nativeIds=[c['id']],nativeMode=mode,nativePhase=g['phase'],nativeCompileCode=g['compileCode'],nativeValue1=g['value1'],nativeValue2=g['value2'],
                nativeRuntimeError1Utf8Hex=g['runtimeError1Utf8Hex'],nativeRuntimeError2Utf8Hex=g['runtimeError2Utf8Hex'],listFixture=c['listio'],
                actualTypedValueObserved=False,nativeFullAstEquivalent=False,nativeExceptionEquivalent=False,compilePhaseObserved=False,listReadInstrumented=False))
            classification.append(dict(id=c['id'],scope=scope,publicCandidate=True))
        except Unprojected as error:classification.append(dict(id=c['id'],scope=str(error),publicCandidate=False))
    # These predicates and outcomes are fixed from source before managed execution.
    # They are not relabeled as direct native AST observations.
    derived=[
        ('root-number','7',False,False),('root-none','None',False,False),('not-none','not None',False,False),
        ('typed-mixed-equality','1==true',False,False),('typed-mixed-inequality','1!=true',True,False),
        ('typed-nonstring-match','AllEqual(Array(None matches "a",None))',True,False),
        ('opt-eager-zero','@opt true or (1/0)',False,True),('noopt-short-zero','@noopt true or (1/0)',True,False),
        ('opt-eager-range','@opt AllEqual(Array(LineAt(ColumnRange(Substr("0",0))),Array(None,None)))',False,True),
        ('noopt-provider-range','@noopt AllEqual(Array(LineAt(ColumnRange(Substr("0",0))),Array(None,None)))',True,False),
        ('opt-size-provenance','@opt AllEqual(Array(LineAt(ColumnRange(Substr("0",At(Array(1KB),0)-1024))),Array(None,None)))',True,False),
        ('opt-negated-size-provenance','@opt AllEqual(Array(LineAt(ColumnRange(Substr("0",At(Array(-1KB),0)+1024))),Array(None,None)))',False,True),
        ('opt-bool-subtract','@opt (true-false)==1',True,False),('noopt-bool-subtract','@noopt (true-false)==-1',True,False),
        ('native-size-truncation','@noopt 1.5B==1',True,False),('native-double-precision','@noopt 1.0000000000000001==1',True,False),
        ('native-large-double-accepted-and-true','@noopt 1e100>0',True,False),
        ('extension-positive-int','+1==1',True,False),('extension-positive-double','+.5==.5',True,False),('extension-positive-size','+1KB==1024',True,False),
        ('extension-int64-min','-9223372036854775808==ToInt("-9223372036854775808")',True,False),
        ('extension-positive-int64-overflow','9223372036854775808==0',False,True),
        ('extension-plus-identifier','+Size==1',False,True),('extension-plus-parenthesized','+(1)==1',False,True),('extension-plus-plus','++1==1',False,True),
        ('extension-kib','1KiB==1024',True,False),('native-duration-version','1ms==1000 and v"1.2.3.4"==ToInt("281483566841860")',True,False)
    ]
    for count in (2,3):
        for identity,expression,selected,rejected in derived:
            if 'Array(None,None)' in expression and count==3:expression=expression.replace('Array(None,None)','Array(None,None,None)')
            plan.append(dict(id='source-'+identity,count=count,expression=expression,rule='fe',selected=selected,rejected=rejected,scope='source-derived-public-control',nativeIds=[],listFixture=False,
                actualTypedValueObserved=False,nativeFullAstEquivalent=False,nativeExceptionEquivalent=False,compilePhaseObserved=False,listReadInstrumented=False))
        def add(identity,expression,dataset='missing-side',rule='fe',windowsOnly=False):
            plan.append(dict(id='source-'+identity,count=count,expression=expression,rule=rule,selected=True,rejected=False,scope='source-derived-side-wrapper-and-caller-control',nativeIds=[],listFixture=False,dataset=dataset,windowsOnly=windowsOnly,
                actualTypedValueObserved=False,nativeFullAstEquivalent=False,nativeExceptionEquivalent=False,compilePhaseObserved=False,listReadInstrumented=False))
        add('missing-none-no-prop','((Size>0)==None)')
        add('missing-none-with-prop','((Size>0)==None) and Prop("System.Size")!=None',windowsOnly=True)
        for name in ('Size','Name','BaseName','Extension','Path','RelPath','FullPath','Attributes','AttrStr','Date','Modified','DateStr'):
            right='Right'+('Date' if name=='Modified' else 'RelPath' if name=='Path' else name)
            # DateStr is an existing unprefixed extension; RightDateStr is not public.
            if name=='DateStr':continue
            expected='Array('+','.join(['None']*(count-1)+[right])+')'
            add('missing-unprefixed-'+name,'AllEqual(Array('+name+','+expected+'))')
        add('missing-prefixes','LeftSize==None and MiddleSize=='+('1' if count==2 else 'None')+' and RightSize==1')
        add('missing-exists','AllEqual(Array(Exists,Array('+','.join(['false']*(count-1)+['true'])+')))')
        add('missing-folder','AllEqual(Array(Folder,Array('+','.join(['""']*count)+')))')
        add('missing-datestr','AllEqual(Array(DateStr,Array('+','.join(['None']*(count-1)+['ToDateStr(RightDate)'])+')))')
        add('missing-folder-kind','AllEqual(Array(IsFolder,Array('+','.join(['false']*count)+'))) and AllEqual(Array(IsDirectory,IsFolder))')
        add('kind-collision-expression-all','IsFolder',dataset='kind-collision',rule='e')
        add('kind-collision-file-rule','IsFolder',dataset='kind-collision',rule='fe')
        add('kind-collision-directory-rule','IsFolder',dataset='kind-collision',rule='de')
    return plan,classification
