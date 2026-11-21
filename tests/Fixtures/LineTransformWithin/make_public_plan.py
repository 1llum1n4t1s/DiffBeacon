import json,pathlib,hashlib
SOURCE_COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
def expression_node(n):
 t=n['type'];v=n.get('value')
 if t=='integer':return str(v) if -(2**31)<=v<=2**31-1 else 'ToInt('+json.dumps(str(v))+')'
 if t=='double':return str(v)
 if t=='string':return None if any(c in v for c in ['\0','\r','\n']) else json.dumps(v,ensure_ascii=False)
 if t=='bool':return str(v).lower()
 if t=='none':return 'none'
 if t=='array':
  items=[expression_node(x) for x in v]
  return 'Array('+', '.join(items)+')' if all(x is not None for x in items) else None
 return None
def bridges(n,path):
 if n['type']=='integer' and not -(2**31)<=n['value']<=2**31-1:return [dict(astPath=path,nativeType='integer/int64',nativeValue=str(n['value']),publicExpression=expression_node(n),kind='typed-int64-public-bridge',reason='Existing public integer literal parser is int32; ToInt decimal-string constructs exact int64. This oracle captures typed argument evaluation, not old parser/ToInt compatibility.')]
 if n['type']=='array':return [b for i,x in enumerate(n['value']) for b in bridges(x,path+'.value['+str(i)+']')]
 return []
def build(cases,rows):
 plan=[]
 for c,r in zip(cases,rows):
  forms=[expression_node(n) for n in c['args']];representable=all(x is not None for x in forms)
  expression=('@cs ' if c['caseSensitive'] else '@ci ')+('@noopt ' if c['mode']=='function-body-noopt' else '')+'IsWithin('+', '.join(forms)+')' if representable else None
  outcome='compile-constructor-rejection' if expression is not None and len(forms)!=3 else ('serialized-value-equivalence' if expression is not None else 'unrepresentable-not-executed')
  plan.append(dict(id=c['id'],expression=expression,publicExecution='not-executed',expectedPublicOutcome=outcome,classification=c['classification'],sourceCommit=SOURCE_COMMIT,context=dict(kind='literal-no-getter',sourceLineHex='',sourceLineCount=1,rowIndex=0,outerEol=0,tableMode=False,pane='Left',targetMask='Left',forbidRetargeting=True),originalArgsAST=c['args'],caseSensitive=c['caseSensitive'],mode=c['mode'],nativeOracle=r,publicBridges=[b for i,n in enumerate(c['args']) for b in bridges(n,'args['+str(i)+']')],reason='Exact native AST cannot be represented in a public expression.' if not representable else ('Native direct args->at throws out_of_range; public registration requires exactly three and should reject during compile. No value-equivalence claim.' if outcome=='compile-constructor-rejection' else None)))
 context=dict(kind='literal-no-getter',sourceLineHex='',sourceLineCount=1,rowIndex=0,outerEol=0,tableMode=False,pane='Left',targetMask='Left',forbidRetargeting=True)
 return dict(schema='iswithin-public-case-plan-v2',status='captured fixed native AST; proposed public expressions have not run',sourceCommit=SOURCE_COMMIT,count=len(plan),cases=plan,publicConstructorProbes=[dict(id='public-arity-4',expression='@cs @noopt IsWithin(1, 0, 3, none)',context=context,expectedPublicOutcome='compile-constructor-rejection',publicExecution='not-executed',nativeCase='arity-4',relation='Native fourth arg is unrepresentable ThrowNode but not evaluated. This separate public probe changes fourth argument to none and only tests public arity rejection; it is not exact AST/value equivalence.')],additionalBoundaries=[dict(id='null-args',expression=None,executed=False,reason='Native body dereferences args before checking; do not invoke UB.'),dict(id='public-depth-and-value-budget',expression=None,executed=False,reason='Future public compile/evaluate budget rejection E2E; original body has no shared budget, no equivalence claim.'),dict(id='timestamp-parser',expression=None,executed=False,reason='Typed native Timestamp cases captured; public representation and complete date parser are out of scope.')])
if __name__=='__main__':
 raise SystemExit('Imported by capture only; frozen public-case-plan.json must never be overwritten.')
