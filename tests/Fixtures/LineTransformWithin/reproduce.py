"""Fresh offline native reproduction from sibling ZIP and fixed raw sources."""
import sys
sys.dont_write_bytecode=True
import argparse,pathlib,subprocess,os
base=pathlib.Path(__file__).resolve().parent
p=argparse.ArgumentParser();p.add_argument('--output',required=True,type=pathlib.Path);p.add_argument('--compiler',required=True,type=pathlib.Path);p.add_argument('--vcvars',required=True,type=pathlib.Path);p.add_argument('--repo',type=pathlib.Path,default=base.parents[2]);p.add_argument('--reference-zip',type=pathlib.Path,default=base.parent/'LineTransformReplace/reference-source.zip');a=p.parse_args()
subprocess.run([sys.executable,str(base/'verify_immutable_source.py'),'--reference-zip',str(a.reference_zip)],check=True,env=dict(os.environ,PYTHONDONTWRITEBYTECODE='1'))
subprocess.run([sys.executable,str(base/'capture.py'),'--output',str(a.output),'--compiler',str(a.compiler),'--vcvars',str(a.vcvars),'--repo',str(a.repo),'--reference-zip',str(a.reference_zip)],check=True,env=dict(os.environ,PYTHONDONTWRITEBYTECODE='1'))
