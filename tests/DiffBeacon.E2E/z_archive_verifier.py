"""Explicit official decoder + Python stdlib TAR/content/metadata verification.

Never used by the product. Decoder build/source binding is kept separately.
"""
import argparse
import hashlib
import io
import json
import pathlib
import subprocess
import tarfile


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def entries(data):
    result = []
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:", encoding="utf-8", errors="strict") as archive:
        for entry in archive:
            if not (entry.isdir() or entry.isfile()):
                raise ValueError("Unexpected special entry")
            content = b"" if entry.isdir() else archive.extractfile(entry).read()
            result.append(dict(path=entry.name.rstrip("/"), directory=entry.isdir(), size=len(content),
                               sha256="" if entry.isdir() else sha(content), mode=entry.mode,
                               uid=entry.uid, gid=entry.gid, mtime=entry.mtime))
    return sorted(result, key=lambda entry: entry["path"])


parser = argparse.ArgumentParser()
parser.add_argument("--archive", required=True)
parser.add_argument("--expected-tar", required=True)
parser.add_argument("--decoder", required=True)
parser.add_argument("--sevenzip")
parser.add_argument("--output", required=True)
args = parser.parse_args()
output = pathlib.Path(args.output)
output.mkdir(parents=True, exist_ok=True)
expected = entries(pathlib.Path(args.expected_tar).read_bytes())
decoded = []
commands = []
for label, executable, arguments in [
    ("ncompress", args.decoder, ["-d", "-c", args.archive]),
    *(([("sevenzip", args.sevenzip, ["x", "-so", args.archive])]) if args.sevenzip else []),
]:
    process = subprocess.run([executable, *arguments], capture_output=True, timeout=30)
    (output / (label + ".stderr.txt")).write_bytes(process.stderr)
    (output / (label + ".tar")).write_bytes(process.stdout)
    command = dict(executable=str(pathlib.Path(executable).resolve()),
                   executableSha256=sha(pathlib.Path(executable).read_bytes()), arguments=arguments,
                   exitCode=process.returncode, stderr=process.stderr.decode(errors="replace"),
                   stdoutBytes=len(process.stdout), stdoutSha256=sha(process.stdout))
    commands.append(command)
    if process.returncode or process.stderr or len(process.stdout) > 4 * 1024 * 1024:
        raise ValueError("Official decoder failed or exceeded verification capture budget")
    decoded.append(process.stdout)
actual = entries(decoded[0])
content_fields = ("path", "directory", "size", "sha256", "mtime")
contents_match = len(actual) == len(expected) and all(
    all(a[key] == b[key] for key in content_fields) for a, b in zip(actual, expected))
writer_metadata_match = all(e["mode"] == (0o700 if e["directory"] else 0o600)
                            and e["uid"] == 0 and e["gid"] == 0 for e in actual)
proof = dict(archive=str(pathlib.Path(args.archive).resolve()),
             archiveSha256=sha(pathlib.Path(args.archive).read_bytes()),
             expectedTarSha256=sha(pathlib.Path(args.expected_tar).read_bytes()),
             commands=commands, entries=actual, expectedEntries=expected,
             contentsAndTimesMatch=contents_match, writerMetadataMatch=writer_metadata_match,
             allDecoderTarBytesMatch=all(value == decoded[0] for value in decoded),
             secondDecoderObserved=bool(args.sevenzip))
(output / "proof.json").write_text(json.dumps(proof, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
print(json.dumps(proof, ensure_ascii=True))
if not (contents_match and writer_metadata_match and proof["allDecoderTarBytesMatch"]):
    raise ValueError("Independent TAR verification mismatch")
