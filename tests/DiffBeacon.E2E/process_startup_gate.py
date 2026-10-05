"""Test-only macOS readiness/startup gate; exec preserves process identity."""

import os
import sys


def main():
    if len(sys.argv) < 3:
        print("E2E startup gate arguments are missing.", file=sys.stderr)
        return 125
    # wrapperのreadyを一回のwriteで通知し、通常stdinはbufferへ先読みしない。
    ready = f"DIFFBEACON_E2E_GATE_READY_V2\t{sys.argv[1]}\t{os.getpid()}\n".encode("ascii")
    if os.write(1, ready) != len(ready):
        return 125
    if os.read(0, 1) != b"\0":
        print("E2E startup gate was not released.", file=sys.stderr)
        return 125
    try:
        os.execvp(sys.argv[2], sys.argv[2:])
    except OSError as error:
        print(f"E2E startup gate exec failed: {error}", file=sys.stderr)
        return 127


if __name__ == "__main__":
    raise SystemExit(main())
