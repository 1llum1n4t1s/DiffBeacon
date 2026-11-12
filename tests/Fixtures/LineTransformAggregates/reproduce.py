"""固定原本を上書きせず、新規外部Tempで二回compile/runし全stdout bytes照合。"""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,subprocess,json
BASE=pathlib.Path(__file__).resolve().parent
ap=argparse.ArgumentParser();ap.add_argument('--repo',type=pathlib.Path,required=True);ap.add_argument('--reference-zip',type=pathlib.Path,required=True);ap.add_argument('--output',type=pathlib.Path,required=True);ap.add_argument('--compiler',type=pathlib.Path,required=True);ap.add_argument('--vcvars',type=pathlib.Path,required=True);a=ap.parse_args()
from capture import validate
out=validate(a.output,a.repo);assert not out.exists();out.mkdir(parents=True)
for run in ['run1','run2']:
    command=[sys.executable,str(BASE/'capture.py'),'--repo',str(a.repo),'--reference-zip',str(a.reference_zip),'--output',str(out/run),'--compiler',str(a.compiler),'--vcvars',str(a.vcvars)]
    subprocess.run(command,check=True)
subprocess.run([sys.executable,str(BASE/'verify.py'),'--reference-zip',str(a.reference_zip),'--actual1',str(out/'run1/golden.ndjson'),'--actual2',str(out/'run2/golden.ndjson')],check=True)
