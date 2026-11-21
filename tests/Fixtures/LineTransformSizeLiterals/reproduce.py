"""Reproduce in fresh external output; source fixture and golden are never overwritten."""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,subprocess
BASE=pathlib.Path(__file__).resolve().parent
p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--reference-zip',type=pathlib.Path,required=True);p.add_argument('--compiler',type=pathlib.Path,required=True);p.add_argument('--vcvars',type=pathlib.Path,required=True);a=p.parse_args()
assert not a.output.exists()
for run in ('run1','run2'):
    subprocess.run([sys.executable,'-B',str(BASE/'capture.py'),'--output',str(a.output/run),'--reference-zip',str(a.reference_zip),'--compiler',str(a.compiler),'--vcvars',str(a.vcvars)],check=True)
subprocess.run([sys.executable,'-B',str(BASE/'verify.py'),'--reference-zip',str(a.reference_zip),'--actual1',str(a.output/'run1/golden.ndjson'),'--actual2',str(a.output/'run2/golden.ndjson')],check=True)
