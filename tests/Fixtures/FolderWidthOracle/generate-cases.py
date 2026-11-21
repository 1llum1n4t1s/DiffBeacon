"""入力と登録情報だけを生成する。goldenはWindows実APIで別採取する。"""
import json,pathlib
ROOT=pathlib.Path(__file__).resolve().parent
functions=[('ToHalfWidth','tohalfwidth','LCMAP_HALFWIDTH',0x400000),('ToFullWidth','tofullwidth','LCMAP_FULLWIDTH',0x800000),('ToHiragana','tohiragana','LCMAP_HIRAGANA',0x100000),('ToKatakana','tokatakana','LCMAP_KATAKANA',0x200000),('ToSimplifiedChinese','tosimplifiedchinese','LCMAP_SIMPLIFIED_CHINESE',0x2000000),('ToTraditionalChinese','totraditionalchinese','LCMAP_TRADITIONAL_CHINESE',0x4000000)]
def val(t,x=None):return dict(type=t,**({'value':x} if x is not None else {}))
def s(x):return val('string',x)
def a(x):return val('array',x)
samples={'empty':'','ascii':'ABC xyz 012 !"#$%&\'()*+,-./:;<=>?@[\\]^_`{|}~','fullwidth-ascii':'ＡＢＣ　ｘｙｚ　０１２　！＂＃＄％＆＇（）＊＋，－．／：；＜＝＞？＠［＼］＾＿｀｛｜｝～','spaces':' \u3000\u00a0\u2002\u2003\u2009\t\r\n','kana':'アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヲンァィゥェォッャュョー','half-kana':'ｱｲｳｴｵｶｷｸｹｺｶﾞｷﾞｸﾞｹﾞｺﾞﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟｳﾞｧｨｩｪｫｯｬｭｮｰ','voiced':'ガギグゲゴザジズゼゾダヂヅデドバビブベボパピプペポヴヷヸヹヺ','dakuten':'カ\u3099ハ\u309a\u309b\u309c\uff9e\uff9f','punctuation':'。「」、・ー『』【】〈〉《》〔〕…￥￦¢£¬¯¦¥₩','hiragana':'あいうえおがぎぐげごぱぴぷぺぽゔぁぃぅぇぉっゃゅょゎゕゖゝゞ','katakana-iteration':'カタカナヵヶヮヽヾヿ','chinese-simplified':'汉语龙门马车书国体发后钟台万东广东','chinese-traditional':'漢語龍門馬車書國體發後鐘臺萬東廣東','unrelated-compatibility':'ﬃ①ⅣÅ㎏㍿神','combining':'e\u0301a\u0315\u0300','hangul':'가각\u1100\u1161\u11a8','supplementary':'\U0001d400\U0001f600\U00020000\U0001f1ef\U0001f1f5','mixed':'ＡB１2　ｶﾞガが漢汉ﬃ①😀','nul':'Ａ\0ガ\0B','nul-leading':'\0Ａ','nul-trailing':'ガ\0'}
cases=[]
def native_value(v):
 if v['type'] in ['bytes','regex','file']:return True
 if v['type']=='array':return any(native_value(x) for x in v['value'])
 return v.get('value') in ['NAN','INFINITY'] or v['type']=='string' and '\0' in v['value']
def add(f,id,args,throws=(),null=False,native=False):
 cases.append(dict(id=f[1]+'-'+id,functionIndex=functions.index(f),function=f[0],registeredName=f[1],args=args,throws=list(throws),nullArgs=null,projection='native-only' if native or throws or null or any(native_value(x) for x in args) else 'public-flt-projectable'))
typed=[val('none'),val('bool',True),val('bool',False),val('int','0'),val('int','-42'),val('int','9223372036854775807'),val('double','1.25'),val('double','-0.0'),val('double','NAN'),val('double','INFINITY'),val('regex'),val('file'),a([]),a([s('Ａｶﾞ'),val('none'),val('int','12'),val('bool',False)]),a([a([s('Ａｶﾞ'),val('none')]),a([]),s('Ａガ')])]
for f in functions:
 for name,text in samples.items():add(f,name,[s(text)])
 for n,v in enumerate(typed):add(f,'type-'+str(n),[v])
 for n,h in enumerate(['00','efbca100e382ac','80','c0af','e282','eda080','f4908080','f09f9880ffefbca1','efbfbd','efbfbf','f48fbfbf']):add(f,'bytes-'+str(n),[val('bytes',h)])
 add(f,'value-throws',[s('unused')],[0])
 add(f,'arity-zero',[],native=True)
 add(f,'arity-two',[s('unused'),s('unused')],native=True)
 add(f,'arity-null',[],null=True)
for n,text in enumerate(['Ａガabc','Ａ\0ガ','']):
 cases.append(dict(id='native-invalid-flags-'+str(n),functionIndex=6,function='NativeInvalidFlags',registeredName='native_invalid_flags',args=[s(text)],throws=[],nullArgs=False,projection='native-only'))
assert len({c['id'] for c in cases})==len(cases)
(ROOT/'cases.json').write_text(json.dumps(cases,ensure_ascii=True,indent=2)+'\n',encoding='utf-8')
(ROOT/'functions.json').write_text(json.dumps(dict(functions=[dict(id=f[1],publicSpelling=f[0],registeredName=f[1],aliases=[],flagName=f[2],flagValue=f[3],minArgs=1,maxArgs=1) for f in functions],aliasBoundary='Fixed registration table has exactly these six names and no additional aliases. PublicSpelling is the usual case variant; parser/case-insensitive name lookup is outside native capture.'),indent=2)+'\n',encoding='utf-8')
print(len(cases))
