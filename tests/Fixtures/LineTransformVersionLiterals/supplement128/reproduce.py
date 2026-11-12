"""Two fresh external real captures followed by independent source/process reader."""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,subprocess,os
BASE=pathlib.Path(__file__).resolve().parent
def main():
 p=argparse.ArgumentParser();p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--reference-zip',type=pathlib.Path,required=True);p.add_argument('--raw60-dir',type=pathlib.Path,required=True);p.add_argument('--compiler',type=pathlib.Path,required=True);p.add_argument('--vcvars',type=pathlib.Path,required=True);a=p.parse_args();out=a.output.resolve()
 assert out.is_relative_to(pathlib.Path(os.environ['TEMP']).resolve())
 for x in (out,*out.parents):
  assert not x.name.startswith('.') and x.name.lower()!='artifacts' and not x.is_symlink() and not x.is_junction()
  if x.exists():assert not x.stat().st_file_attributes&1024
 assert not out.exists();out.mkdir(parents=True)
 for name in ['fresh1','fresh2']:
  subprocess.run([sys.executable,'-B',str(BASE/'capture.py'),'--output',str(out/name),'--compiler',str(a.compiler),'--vcvars',str(a.vcvars)],check=True)
 subprocess.run([sys.executable,'-B',str(BASE/'verify.py'),'--reference-zip',str(a.reference_zip),'--raw60-dir',str(a.raw60_dir),'--actual1',str(out/'fresh1'),'--actual2',str(out/'fresh2')],check=True)
if __name__=='__main__':main()
