"""Run both native captures into fresh external directories; originals stay immutable."""
import argparse
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=pathlib.Path, required=True)
parser.add_argument('--compiler', default='cl')
parser.add_argument('--vcvars')
parser.add_argument('--repo', type=pathlib.Path, default=ROOT.parents[2])
args = parser.parse_args()
output = args.output.absolute()
for ancestor in [output, *output.parents]:
    if ancestor.name.lower() == 'artifacts' or ancestor.is_symlink() or ancestor.is_junction():
        raise ValueError('unsafe output ancestor')
if output.exists() or output.is_relative_to(args.repo.resolve()) or output.is_relative_to(pathlib.Path.home() / '.codex'):
    raise ValueError('output must be fresh and external to repository/Codex home')
env = dict(os.environ, PYTHONDONTWRITEBYTECODE='1')
for name, extra in [('native', []), ('serialized', ['--serialized'])]:
    command = [sys.executable, str(ROOT / 'capture.py'), '--output', str(output / name), '--compiler', args.compiler, '--repo', str(args.repo)] + extra
    if args.vcvars:
        command += ['--vcvars', args.vcvars]
    subprocess.run(command, env=env, check=True)
print('Both independently compiled native stdout captures match the fixed golden bytes.')
