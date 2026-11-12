#!/usr/bin/env python3
"""Read-only verification; Native compilers and reproduce.py are never invoked."""
from pathlib import Path
import subprocess, sys
root = Path(__file__).resolve().parent
for name in ('primary31', 'guarded48'):
    subprocess.run([sys.executable, '-B', str(root / name / 'verify.py')], check=True)
