"""Strict actual App Compile/Transform NDJSON reader; serialized bytes only."""
import sys
sys.dont_write_bytecode=True
import argparse, hashlib, json, pathlib, re, runpy
BASE=pathlib.Path(__file__).resolve().parent
STATIC=runpy.run_path(str(BASE/'verify-public-plan.py'))
require=STATIC['require'];load=STATIC['load']
FIELDS={'id','classification','qualification','contextEquivalent','actualTypedValueObserved','originalNativeKind','originalNumericBitsHex','actualNumericBitsHex','executed','producerCompiled','skipped','reason','nativeGoldenSha256','nativeLineNumber','nativeFirstStep','nativeCase','publicPlan','actualFirstSerializedUtf8Hex','expectedFirstSerializedUtf8Hex','sourceRetained','matched','workUsed','outputCharacters','actualExceptionType','actualErrorUtf8Hex','actualKeys'}
KEY_FIELDS={'text','utf16Length','utf8Length','utf8Hex','sha256'}
PROBE_FIELDS={'plan','executed','qualification','actualExceptionType','actualErrorUtf8Hex'}
QUALIFICATION='public serialized-key bridge only; Native typed AST/context/cache/exception equivalence not observed'
PROBE_QUALIFICATION='separate public argument-count contract; outside Native141'
def records(p):
    raw=p.read_bytes();require(raw.endswith(b'\n') and not raw.startswith(b'\xef\xbb\xbf'),'NDJSON LF termination / no BOM '+str(p))
    lines=raw.split(b'\n')[:-1];require(all(lines),'no blank NDJSON rows')
    return [load(line.decode('utf-8','strict')) for line in lines],raw
def hex_bytes(s):
    require(isinstance(s,str) and re.fullmatch(r'(?:[0-9a-f]{2})*',s) is not None,'canonical lowercase byte hex')
    return bytes.fromhex(s)
def count(v):return type(v) is int and v>=0
def verify(actual,probe_actual,fixture=BASE,plan_dir=BASE):
    plan,probes,_=STATIC['verify_plan'](fixture,plan_dir)
    rows,raw=records(actual);require(len(rows)==141,'actual main141')
    matched=0;excluded=0
    for n,(r,item) in enumerate(zip(rows,plan['cases']),1):
        require(isinstance(r,dict) and set(r)==FIELDS,'exact diagnostics field set')
        first=item['nativeFirstStepReference']['originalStep'];value=first.get('value')
        expected_kind=value.get('kind') if isinstance(value,dict) else None
        expected_bits=value.get('bitsHex') if isinstance(value,dict) else None
        require(r['id']==item['id'] and r['classification']==item['classification'] and r['reason']==item['reason'],'ID/order/classification/reason')
        require(r['qualification']==QUALIFICATION and r['contextEquivalent'] is False and r['actualTypedValueObserved'] is False and r['actualNumericBitsHex'] is None,'serialized-only observation boundary')
        require(r['originalNativeKind']==expected_kind and r['originalNumericBitsHex']==expected_bits,'original kind/bits are reference-only')
        require(r['nativeGoldenSha256']==STATIC['GOLDEN'] and type(r['nativeLineNumber']) is int and r['nativeLineNumber']==n,'fixed Native line reference')
        require(r['nativeFirstStep']==first and r['nativeCase']==item['nativeCase'] and r['publicPlan']==item,'all original and plan fields preserved')
        require(r['expectedFirstSerializedUtf8Hex']==first.get('serializedUtf8Hex') and r['sourceRetained'] is True,'original optional serialized bytes/source retention')
        require(count(r['workUsed']) and r['workUsed']<=128*1024*1024 and count(r['outputCharacters']) and r['outputCharacters']<=64*1024*1024,'budget diagnostic domains')
        bridge=item['classification']=='serialized-key/value-bridge-planned'
        require(r['executed'] is bridge and r['producerCompiled'] is bridge and r['skipped'] is (not bridge),'actual execution/compilation/skip flags')
        require(r['actualExceptionType'] is None and r['actualErrorUtf8Hex'] is None,'bridge compile/transform must succeed; excluded rows are not executed')
        require(isinstance(r['actualKeys'],list),'actual key list')
        if not bridge:
            require(r['matched'] is False and r['actualFirstSerializedUtf8Hex'] is None and r['actualKeys']==[] and r['workUsed']==r['outputCharacters']==0,'37 exclusions are validated as unexecuted, never compatibility pass')
            excluded+=1;continue
        require(r['matched'] is True,'bridge matched=false is a failure')
        expected=hex_bytes(first['serializedUtf8Hex'])
        require(len(r['actualKeys'])==len(item['publicInvocationPlan']['providerLines'])>0,'all public rows have outputs')
        total_utf16=0
        for key in r['actualKeys']:
            require(isinstance(key,dict) and set(key)==KEY_FIELDS and isinstance(key['text'],str),'exact actual key fields')
            b=key['text'].encode('utf-8','strict');utf16=len(key['text'].encode('utf-16-le','strict'))//2
            require(type(key['utf16Length']) is int and key['utf16Length']==utf16 and type(key['utf8Length']) is int and key['utf8Length']==len(b),'actual encoding lengths')
            require(hex_bytes(key['utf8Hex'])==b==expected and key['sha256']==hashlib.sha256(b).hexdigest(),'every actual key byte / SHA equals frozen first result')
            total_utf16+=utf16
        require(r['actualFirstSerializedUtf8Hex']==r['actualKeys'][0]['utf8Hex']==first['serializedUtf8Hex'],'actual first serialized bytes')
        require(r['outputCharacters']>=total_utf16 and r['workUsed']>0,'cumulative output counter covers serialized UTF16 character count')
        matched+=1
    probe_rows,probe_raw=records(probe_actual);require(len(probe_rows)==8,'eight actual separate probes')
    for r,p in zip(probe_rows,probes['probes']):
        require(isinstance(r,dict) and set(r)==PROBE_FIELDS,'exact arity probe fields')
        require(r['plan']==p and r['executed'] is True and r['qualification']==PROBE_QUALIFICATION,'separate public probe plan/execution')
        require(r['actualExceptionType'] in {'System.ArgumentException','System.ArgumentOutOfRangeException','System.ArgumentNullException'},'public argument-count rejection required')
        error=hex_bytes(r['actualErrorUtf8Hex']);require(bool(error) and bool(error.decode('utf-8','strict')),'actual public error message bytes')
    require(matched==104 and excluded==37,'actual104 / unexecuted37 remain separate')
    return {'passed':True,'mainCases':141,'bridgeCases':104,'unrepresentedCases':37,'separateProbes':8,'nativeGoldenSha256':STATIC['GOLDEN'],'actualSha256':hashlib.sha256(raw).hexdigest(),'publicPlanSha256':hashlib.sha256((plan_dir/'public-case-plan.json').read_bytes()).hexdigest(),'probesSha256':hashlib.sha256(probe_raw).hexdigest(),'publicProbePlanSha256':hashlib.sha256((plan_dir/'probes.json').read_bytes()).hexdigest(),'contextEquivalent':False,'actualTypedValueObserved':False,'actualNumericBitsObserved':False,'originalNativeBitsReferenceOnly':True,'qualification':'App public Compile/Transform serialized UTF8 output bytes only;37 are validated unexecuted records, not compatibility passes; Native AST/cache/context/error-class equivalence and engineering/UI diagnostics are outside this reader'}
def main():
    p=argparse.ArgumentParser();p.add_argument('--actual',type=pathlib.Path,required=True);p.add_argument('--public-plan',type=pathlib.Path,required=True);p.add_argument('--probes',type=pathlib.Path,required=True);p.add_argument('--fixture',type=pathlib.Path,default=BASE);p.add_argument('--receipt',type=pathlib.Path);a=p.parse_args()
    require(a.public_plan.name=='public-case-plan.json','public plan filename')
    result=verify(a.actual,a.probes,a.fixture,a.public_plan.parent)
    if a.receipt:
        with a.receipt.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2);f.write('\n')
    print(json.dumps(result))
if __name__=='__main__':main()
