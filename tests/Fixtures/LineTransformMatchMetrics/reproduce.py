"""固定原本を上書きせず、外部fresh-onlyへ二回compile/runしてreaderで照合。"""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,subprocess
BASE=pathlib.Path(__file__).resolve().parent
ap=argparse.ArgumentParser();ap.add_argument('--repo',type=pathlib.Path,required=True);ap.add_argument('--reference-zip',type=pathlib.Path,required=True);ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--compiler',type=pathlib.Path,required=True);ap.add_argument('--vcvars',type=pathlib.Path,required=True);a=ap.parse_args()
from capture import validate
out=validate(a.output,a.repo);assert not out.exists()
for run in ['run1','run2']:
 subprocess.run([sys.executable,'-B',str(BASE/'capture.py'),'--repo',str(a.repo),'--reference-zip',str(a.reference_zip),'--output',str(out/run),'--compiler',str(a.compiler),'--vcvars',str(a.vcvars)],check=True)
 subprocess.run([sys.executable,'-B',str(BASE/'verify.py'),'--actual',str(out/run/'golden.ndjson')],check=True)
