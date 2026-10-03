"""Reproduce fixed inputs with explicitly supplied, separately built official tools.

The product and normal .NET build never invoke this script.
"""
import argparse
import hashlib
import json
import pathlib
import subprocess


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def literal(data):
    output = bytearray(b"\x1f\x9d\x09")
    accumulator = bits = 0
    for value in data:
        accumulator |= value << bits
        bits += 9
        while bits >= 8:
            output.append(accumulator & 255)
            accumulator >>= 8
            bits -= 8
    if bits:
        output.append(accumulator & 255)
    return output


parser = argparse.ArgumentParser()
parser.add_argument("--ncompress", required=True)
parser.add_argument("--legacy-ncompress", required=True)
parser.add_argument("--sevenzip", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parent
output = pathlib.Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
commands = []
for case in manifest["normalCases"]:
    source = next(item for item in manifest["inputs"] if item["kind"] == case["kind"])
    raw = (root / source["tarName"]).read_bytes()
    assert sha(raw) == source["tarSha256"]
    if case["name"] == "literal-nonblock-b9.tar.Z":
        data = literal(raw)
    else:
        executable = args.ncompress if case["block"] else args.legacy_ncompress
        arguments = [f'-b{case["maximumBits"]}', "-c"]
        if not case["block"]:
            arguments.insert(0, "-C")
        process = subprocess.run([executable, *arguments], input=raw, capture_output=True, timeout=30)
        # ncompress returns2 when compression does not save space; product error2 has different semantics.
        assert process.returncode in (0, 2) and not process.stderr
        data = process.stdout
        commands.append(dict(executable=executable, executableSha256=sha(pathlib.Path(executable).read_bytes()),
                             arguments=arguments, stdinSha256=sha(raw), exitCode=process.returncode,
                             stdoutSha256=sha(data), stderr=process.stderr.decode(errors="replace")))
    assert sha(data) == case["sha256"], case["name"]
    path = output / case["name"]
    path.write_bytes(data)
    for executable, arguments in [(args.ncompress, ["-d", "-c", str(path)]),
                                  (args.sevenzip, ["x", "-so", str(path)])]:
        process = subprocess.run([executable, *arguments], capture_output=True, timeout=30)
        assert process.returncode == 0 and not process.stderr and process.stdout == raw
        commands.append(dict(executable=executable, executableSha256=sha(pathlib.Path(executable).read_bytes()),
                             arguments=arguments, exitCode=process.returncode, stdoutSha256=sha(process.stdout),
                             allRawTarBytesMatch=True, stderr=process.stderr.decode(errors="replace")))
(output / "regeneration-proof.json").write_text(json.dumps(commands, indent=2) + "\n", encoding="utf-8")
print(json.dumps(dict(cases=len(manifest["normalCases"]), commands=len(commands), output=str(output))))
