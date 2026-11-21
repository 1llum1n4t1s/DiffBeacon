"""Fresh external safe30 reproduction proposal. Default is plan-only, never executes Native31."""
from pathlib import Path
import sys,os,json,argparse,hashlib,zipfile,tempfile,time,shutil,re,ast
sys.dont_write_bytecode=True
from verify import verify,read_archive
BASE=Path(__file__).resolve().parent
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def store(p,v):
 with Path(p).open('x',encoding='utf-8') as f:json.dump(v,f,indent=2)
def external_fresh_output(value):
 logical=Path(value).absolute();assert logical.is_absolute() and not logical.exists(),'output must be new explicit absolute path'
 for p in [logical,*logical.parents]:
  assert p.name.lower() not in ['artifacts','.codex'] and not p.is_symlink() and not (hasattr(p,'is_junction') and p.is_junction()),('forbidden path/reparse',str(p))
  assert not (p/'.git').exists() and not (p/'DiffBeacon.slnx').exists(),('repository ancestor',str(p))
 assert not logical.is_relative_to(BASE) and not BASE.is_relative_to(logical)
 temp=Path(tempfile.gettempdir());allowed=[temp]
 local=os.environ.get('LOCALAPPDATA')
 if local:
  local=Path(local);allowed += [local/'Codex'/'TaskArtifacts'/'DiffBeacon',local/'Packages'/'OpenAI.Codex_2p2nqsd0c76g0'/'LocalCache'/'Local'/'Codex'/'TaskArtifacts'/'DiffBeacon']
 assert any(logical.is_relative_to(x) for x in allowed),'outside approved Temp/LocalAppData retention root'
 parent=logical.parent
 while not parent.exists():parent=parent.parent
 parentResolved=parent.resolve();assert not parent.is_symlink() and not (hasattr(parent,'is_junction') and parent.is_junction())
 logical.mkdir(parents=True);canonical=logical.resolve();assert logical.samefile(canonical)
 assert any(canonical.is_relative_to(x.resolve()) for x in allowed if x.exists()),'canonical path outside approved scope'
 for p in [canonical,*canonical.parents]:assert p.name.lower() not in ['artifacts','.codex'] and not p.is_symlink() and not (hasattr(p,'is_junction') and p.is_junction())
 stat=canonical.stat();return canonical,{'logical':str(logical),'canonical':str(canonical),'samefile':True,'statDevice':stat.st_dev,'statFileId':stat.st_ino,'preexistingParentLogical':str(parent),'preexistingParentCanonical':str(parentResolved)}
def sdk_whitelist(path,out,compiler):
 payload=json.loads(Path(path).read_text());env=payload.get('values',payload);allowed={'COMSPEC','INCLUDE','LIB','LIBPATH','PATH','PATHEXT','SYSTEMDRIVE','SYSTEMROOT','TEMP','TMP','UCRTVERSION','UNIVERSALCRTSDKDIR','VCINSTALLDIR','VCTOOLSINSTALLDIR','VSCMD_ARG_HOST_ARCH','VSCMD_ARG_TGT_ARCH','VSINSTALLDIR','WINDIR','WINDOWSSDKDIR','WINDOWSSDKVERSION'}
 assert set(env)<=allowed and {'INCLUDE','LIB','LIBPATH','PATH','SYSTEMROOT','WINDIR'}<=set(env);assert all(isinstance(x,str) for x in env.values())
 for key in ['INCLUDE','LIB','LIBPATH','PATH']:
  for component in env[key].split(';'):
   if not component:continue
   p=Path(component);assert p.is_absolute() and p.exists() and not p.is_symlink() and not (hasattr(p,'is_junction') and p.is_junction())
   low=str(p).lower();assert ('microsoft visual studio' in low or 'windows kits' in low or p.is_relative_to(Path(env['SYSTEMROOT'])) or p==compiler.parent),'non-SDK/compiler environment path'
 assert str(compiler.parent).lower() in env['PATH'].lower();assert env.get('VSCMD_ARG_TGT_ARCH','x64')=='x64';assert env.get('WINDOWSSDKVERSION','10.0.28000.0\\').strip('\\')=='10.0.28000.0'
 env['TEMP']=env['TMP']=str(out);return env
def os_profile_before_native(expected):
 import ctypes
 from ctypes import wintypes
 kernel=ctypes.WinDLL('kernel32',use_last_error=True)
 class TZ(ctypes.Structure):_fields_=[('bias',wintypes.LONG),('standardName',wintypes.WCHAR*32),('standardDate',wintypes.WORD*8),('standardBias',wintypes.LONG),('daylightName',wintypes.WCHAR*32),('daylightDate',wintypes.WORD*8),('daylightBias',wintypes.LONG)]
 tz=TZ();state=kernel.GetTimeZoneInformation(ctypes.byref(tz));lcid=kernel.GetUserDefaultLCID();actual={'userLcid':lcid,'timezoneState':state,'timezoneStandardNameUtf8Hex':tz.standardName.encode('utf-8').hex(),'timezoneBiasMinutes':tz.bias,'timezoneStandardBiasMinutes':tz.standardBias,'timezoneDaylightBiasMinutes':tz.daylightBias}
 assert actual=={key:expected[key] for key in actual},'sameProfile required before Native; no timezone or locale setting is changed';return actual
def main():
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--compiler',type=Path);parser.add_argument('--linker',type=Path);parser.add_argument('--sdk-env',type=Path);parser.add_argument('--execute',action='store_true');parser.add_argument('--go-file',type=Path);args=parser.parse_args()
 qualification=verify();plan=json.loads((BASE/'portable-compile-plan.json').read_text());scope=json.loads((BASE/'reproduction-scope.json').read_text());assert scope['excluded']==['embedded-nul'] and plan['requiresNewSafe30ScopeApproval']
 if args.execute:
  assert os.name=='nt' and args.go_file and args.compiler and args.linker and args.sdk_env,'new exact safe30 GO + explicit installed compiler/linker/SDK env required'
  go=json.loads(args.go_file.read_text());assert go.get('approved') is True and go.get('scope')=='timestamp-safe30-full-fresh' and go.get('fixtureManifestSha256')==sha(BASE/'fixed-manifest.json') and go.get('reproduceScriptSha256')==sha(BASE/'reproduce.py') and go.get('unsafeNulSerializationApproved') is False
  assert sha(args.compiler)==plan['compilerSha256'] and sha(args.linker)==plan['linkerSha256']
 out,identity=external_fresh_output(args.output);bundle=out/'bundle';bundle.mkdir();sources=read_archive('reference-source.zip','source-zip-manifest.json')
 for name,raw in sources.items():
  target=bundle/name;assert target.resolve().is_relative_to(bundle.resolve());target.parent.mkdir(parents=True,exist_ok=True)
  with target.open('xb') as f:f.write(raw)
 with (bundle/'reproduce-safe30-adapter.cpp').open('xb') as f:f.write((BASE/'reproduce-safe30-adapter.cpp').read_bytes())
 for name in ['obj','exe','proof']:(out/name).mkdir()
 compiler=str(args.compiler.resolve()) if args.compiler else '{explicit-installed-cl.exe}';linker=str(args.linker.resolve()) if args.linker else '{explicit-installed-link.exe}';commands=[];objs=[]
 for row in plan['compileTUs']:
  src=bundle/row['sourceRelative'];assert sha(src)==row['sourceSha256'];obj=out/'obj'/(row['id']+'.obj');objs.append(str(obj));commands.append({'id':row['id'],'source':str(src),'sourceSha256':row['sourceSha256'],'command':[compiler,*row['abiFlags'],*['/I'+str(bundle/root) for root in plan['includeRoots']],'/Fo'+str(obj),str(src)],'outputs':[str(obj)],'timeoutSeconds':180})
 exe=out/'exe'/'timestamp-safe30.exe';commands.append({'id':'link-safe30','command':[linker,*plan['linkFlags'],'/OUT:'+str(exe),*objs,*plan['systemLibraries']],'outputs':[str(exe)],'timeoutSeconds':180})
 for i in [1,2]:commands.append({'id':'safe30-native-'+str(i),'command':[str(exe)],'outputs':[],'timeoutSeconds':60})
 assert len(commands)==39 and len(set(objs))==36 and all(Path(x).is_relative_to(out) for x in objs)
 receipt={'mode':'execute-new-safe30-exact-go' if args.execute else 'plan-only','outputIdentity':identity,'sourceZipSha256':sha(BASE/'reference-source.zip'),'scope':scope,'commands':commands,'oldObjReuse':False,'budgetBytes':67108864,'overallTimeoutSeconds':7200,'runtimeJobCaptureQualification':'unmeasured in fixture PREP','noResubmission':True,'noCleanup':True};store(out/'fresh-plan.json',receipt)
 if not args.execute:print(json.dumps({'planOnly':True,'commands':39,'compileTUs':36,'nativeRunsPlanned':2,'safeNativeCases':30,'output':str(out),'execution':False}));return
 from owned_windows_process import run_owned
 env=sdk_whitelist(args.sdk_env,out,args.compiler.resolve());store(out/'reproduction-started.json',{'fixtureManifestSha256':sha(BASE/'fixed-manifest.json'),'goSha256':sha(args.go_file),'sameProfileRequired':True,'noResubmission':True});deadline=time.monotonic()+7200;observed=[];processReceipts=[]
 try:
  for command in commands:
   assert time.monotonic()<deadline
   if 'source' in command:assert sha(command['source'])==command['sourceSha256']
   if command['id'].startswith('safe30-native-'):
    os_profile_before_native(plan['sameProfile']);assert exe.exists();beforeExe=sha(exe)
   r=run_owned(command,env,out,plan['byteBudget'],deadline);processReceipts.append(r)
   if 'source' in command:assert sha(command['source'])==command['sourceSha256']
   if command['id'].startswith('safe30-native-'):
    assert sha(exe)==beforeExe;rows=[json.loads(v) for v in Path(r['stdout']['path']).read_bytes().splitlines()];assert len(rows)==30 and all(x['id']!='embedded-nul' for x in rows)
    expected=[x for x in json.loads((BASE/'native-semantic31-joined.json').read_text())['cases'] if x['publicproposal'] is not None];transient={'processId','processBirthFileTimeDecimal','clockBeforeEpochMicrosecondsInt64Decimal','clockAfterEpochMicrosecondsInt64Decimal'}
    for raw,old in zip(rows,expected):
     assert raw['id']==old['id'] and raw['processId']==r['pid'] and int(raw['processBirthFileTimeDecimal'])==r['birthFileTime'];assert {key:raw[key] for key in plan['sameProfile']}==plan['sameProfile'];assert {key:value for key,value in raw.items() if key not in transient}==old['rawNativeObservation']
    observed.append(rows)
  assert len(observed)==2 and observed[0][0]['processId']!=observed[1][0]['processId'];store(out/'reproduction-result.json',{'completed':True,'freshCompileCount':36,'newLinkCount':1,'freshNativeRuns':2,'casesPerRun':30,'sameProfileAndSemantic':True,'goldenGenerated':False,'originalUnsafeNulNotExecuted':True})
 except Exception as exc:
  store(out/'reproduction-result.json',{'completed':False,'error':str(exc),'partialEvidenceRetained':True,'noResubmission':True,'goldenGenerated':False});raise
 print(json.dumps({'completed':True,'output':str(out),'safe30':True,'goldenGenerated':False}))
if __name__=='__main__':main()
