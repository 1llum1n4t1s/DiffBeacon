"""二回別build・別processの再採取と独立readerによる固定値照合。"""
import sys
sys.dont_write_bytecode = True
import argparse,pathlib,subprocess
from capture import BASE,validate
ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--compiler',required=True);ap.add_argument('--vcvars',required=True);a=ap.parse_args()
out=validate(a.output);assert not out.exists(),str(out)
for run in ['run1','run2']:
    subprocess.run([sys.executable,'-B',str(BASE/'capture.py'),'--output',str(out/run),'--compiler',a.compiler,'--vcvars',a.vcvars],check=True)
    subprocess.run([sys.executable,'-B',str(BASE/'verify.py'),'--actual',str(out/run/'golden.ndjson')],check=True)
assert (out/'run1/golden.ndjson').read_bytes()==(out/'run2/golden.ndjson').read_bytes()
