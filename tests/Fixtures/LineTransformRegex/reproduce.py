"""Recompile the fixed native oracle in a fresh external Temp task directory."""
import argparse
import os
import pathlib
import subprocess
import sys

BASE = pathlib.Path(__file__).resolve().parent
p = argparse.ArgumentParser()
p.add_argument('--output', type=pathlib.Path, required=True)
p.add_argument('--compiler', default='cl')
p.add_argument('--vcvars')
a = p.parse_args()
env = dict(os.environ, PYTHONDONTWRITEBYTECODE='1')
subprocess.run([sys.executable, str(BASE/'verify.py'), '--verify', '--git'], env=env, check=True)
command = [sys.executable, str(BASE/'capture.py'), '--output', str(a.output), '--compiler', a.compiler]
if a.vcvars:
    command += ['--vcvars', a.vcvars]
subprocess.run(command, env=env, check=True)
subprocess.run([sys.executable, str(BASE/'verify.py'), '--actual', str(a.output/'golden.ndjson')], env=env, check=True)
