"""Reproduce two native stdout files in a fresh external output; do not mutate the fixed fixture."""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,subprocess
BASE=pathlib.Path(__file__).resolve().parent
p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--reference-zip',type=pathlib.Path,required=True);p.add_argument('--compiler',type=pathlib.Path,required=True);p.add_argument('--vcvars',type=pathlib.Path,required=True);p.add_argument('--proof-dir',type=pathlib.Path,required=True);a=p.parse_args()
assert not a.output.exists()
for run in ('final-run1','final-run2'):
    subprocess.run([sys.executable,'-B',str(BASE/'capture.py'),'--output',str(a.output/run),'--reference-zip',str(a.reference_zip),'--compiler',str(a.compiler),'--vcvars',str(a.vcvars)],check=True)
subprocess.run([sys.executable,'-B',str(BASE/'verify.py'),'--reference-zip',str(a.reference_zip),'--proof-dir',str(a.proof_dir),'--actual1',str(a.output/'final-run1/golden.ndjson'),'--actual2',str(a.output/'final-run2/golden.ndjson')],check=True)
