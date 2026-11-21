"""Actual fresh reproduction of every self-contained frozen Native stage."""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,os,subprocess,json,datetime
BASE=pathlib.Path(__file__).resolve().parent
def main():
 p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--compiler',type=pathlib.Path,required=True);p.add_argument('--vcvars',type=pathlib.Path,required=True);a=p.parse_args();out=a.output.resolve()
 assert out.is_relative_to(pathlib.Path(os.environ['TEMP']).resolve())
 for x in (out,*out.parents):
  assert not x.name.startswith('.') and x.name.lower()!='artifacts' and not x.is_symlink() and not x.is_junction()
  if x.exists():assert not x.stat().st_file_attributes&1024
 assert not out.exists();out.mkdir(parents=True)
 # Verification comes first. No historical absolute producer path is followed.
 subprocess.run([sys.executable,'-B',str(BASE/'verify.py')],check=True)
 receipts=[]
 for stage in ['raw60','supplement128','cpp17-edge']:
  command=[sys.executable,'-B',str(BASE/stage/'reproduce.py'),'--output',str(out/stage),'--reference-zip',str(BASE/'reference-source.zip'),'--compiler',str(a.compiler),'--vcvars',str(a.vcvars)]
  if stage!='raw60':command+=['--raw60-dir',str(BASE/'raw60')]
  if stage=='cpp17-edge':command+=['--supplement128-dir',str(BASE/'supplement128'),'--current-project',str(BASE/'cpp17-edge/current-project-evidence.vcxproj')]
  started=datetime.datetime.now(datetime.timezone.utc).isoformat();r=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
  (out/(stage+'.stdout.raw')).write_bytes(r.stdout);(out/(stage+'.stderr.raw')).write_bytes(r.stderr)
  receipts.append({'stage':stage,'command':command,'exitCode':r.returncode,'startedUtc':started,'endedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()})
  assert r.returncode==0 and not r.stderr,(stage,r.stderr.decode('utf-8','replace'))
  print(r.stdout.decode('utf-8'),end='')
 (out/'reproduce-receipt.json').write_text(json.dumps(receipts,indent=2)+'\n',encoding='utf-8')
 subprocess.run([sys.executable,'-B',str(BASE/'verify.py')],check=True)
 print(json.dumps({'freshReproducedStages':3,'actualCompilerNativeRuns':6,'output':str(out),'publicProducerExecuted':False}))
if __name__=='__main__':main()
