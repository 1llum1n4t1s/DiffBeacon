"""固定結果を二回fresh-onlyで再採取する。製品build/testは呼ばない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,subprocess
BASE=pathlib.Path(__file__).resolve().parent
ap=argparse.ArgumentParser();ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--compiler',required=True);ap.add_argument('--vcvars',required=True);a=ap.parse_args()
from capture import validate
out=validate(a.output);assert not out.exists()
for run in ['run1','run2']:
 subprocess.run([sys.executable,'-B',str(BASE/'capture.py'),'--output',str(out/run),'--compiler',a.compiler,'--vcvars',a.vcvars],check=True)
 subprocess.run([sys.executable,'-B',str(BASE/'verify.py'),'--actual',str(out/run/'golden.ndjson')],check=True)
