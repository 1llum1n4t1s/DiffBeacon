"""Independent stdlib/explicit ncompress verification; never called by product."""
import argparse
import bz2
import hashlib
import io
import json
import pathlib
import subprocess
import zlib
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def decode_members(data, kind):
    output = bytearray()
    members = 0
    while data:
        if kind == ".gz":
            if data[:3] != b"\x1f\x8b\x08":
                raise ValueError("invalid gzip next header/trailing")
            decoder = zlib.decompressobj(31)
        else:
            if len(data) < 4 or data[:3] != b"BZh" or not ord("1") <= data[3] <= ord("9"):
                raise ValueError("invalid bzip2 next header/trailing")
            decoder = bz2.BZ2Decompressor()
        output.extend(decoder.decompress(data))
        if not decoder.eof:
            raise ValueError("missing wrapper footer")
        data = decoder.unused_data
        members += 1
    return bytes(output), members


parser = argparse.ArgumentParser()
parser.add_argument("--root", required=True)
parser.add_argument("--decoder", required=True)
parser.add_argument("--output", required=True)
parser.add_argument("--products")
args = parser.parse_args()
root = pathlib.Path(args.root).resolve()
output = pathlib.Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
manifest = json.loads((root / "manifest.json").read_bytes())
proofs = []
products = []
rejections = []
upstream = json.loads((root / "../manifest.json").read_bytes())
for case in manifest["cases"]:
    original = (root / case["name"]).read_bytes()
    if sha(original) != case["sha256"] or len(original) != case["bytes"]:
        raise ValueError("fixed input mismatch")
    if not case["valid"]:
        if case["name"] in ("bad-inner-crc.zip.gz", "bad-inner-size.zip.gz"):
            decoded, _ = decode_members(original, ".gz")
            try:
                with zipfile.ZipFile(io.BytesIO(decoded)) as archive:
                    for entry in archive.infolist():
                        archive.read(entry)
            except zipfile.BadZipFile as error:
                rejections.append(dict(name=case["name"], inputSha256=sha(original), errorType=type(error).__name__, diagnostic=str(error)))
            else:
                raise ValueError("independent ZIP accepted corrupt central metadata")
        continue
    data = original
    layers = []
    for index, layer in enumerate(case["layers"]):
        if layer["wrapper"] == ".Z":
            result = subprocess.run([args.decoder, "-d", "-c"], input=data, capture_output=True, timeout=30)
            logname = case["name"] + "-" + str(index)
            (output / (logname + ".stderr.txt")).write_bytes(result.stderr)
            layers.append(dict(wrapper=".Z", exitCode=result.returncode, decoder=str(pathlib.Path(args.decoder).resolve()),
                               decoderSha256=sha(pathlib.Path(args.decoder).read_bytes()), inputSha256=sha(data)))
            if result.returncode or result.stderr:
                raise ValueError("official Z decoder failed")
            data = result.stdout
        else:
            data, members = decode_members(data, layer["wrapper"])
            layers.append(dict(wrapper=layer["wrapper"], members=members))
        layers[-1].update(decodedSha256=sha(data), decodedBytes=len(data))
        if sha(data) != layer["decodedSha256"] or len(data) != layer["decodedBytes"]:
            raise ValueError("full decoded layer mismatch")
    if data != (root / case["source"]).read_bytes():
        raise ValueError("full terminal bytes mismatch")
    entries = []
    if case["source"] == "payload.zip":
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            for entry in archive.infolist():
                content = archive.read(entry)
                entries.append(dict(path=entry.filename.rstrip("/"), directory=entry.is_dir(), size=len(content),
                                    sha256="" if entry.is_dir() else sha(content),
                                    time="%04d-%02d-%02dT%02d:%02d:%02d" % entry.date_time))
        if sorted(entries, key=lambda e: e["path"]) != sorted(manifest["terminal"]["entries"], key=lambda e: e["path"]):
            raise ValueError("independent all-entry bytes/time mismatch")
    if args.products:
        product_root = pathlib.Path(args.products)
        expected = manifest["terminal"]["entries"] if case["source"] == "payload.zip" else upstream["expectedEntries"]
        expected = sorted([e for e in expected if not e.get("directory", False)], key=lambda e: e["path"])
        repack_path = product_root / (case["name"] + ".zip")
        with zipfile.ZipFile(repack_path) as archive:
            observed = sorted([dict(path=e.filename, size=e.file_size, sha256=sha(archive.read(e))) for e in archive.infolist() if not e.is_dir()], key=lambda e: e["path"])
        expected_bytes = [dict(path=e["path"], size=e["size"], sha256=e["sha256"]) for e in expected]
        if observed != expected_bytes:
            raise ValueError("independent repacked ZIP all bytes mismatch")
        extract_root = product_root / (case["name"] + "-extracted")
        extracted = sorted([dict(path=e.relative_to(extract_root).as_posix(), size=e.stat().st_size, sha256=sha(e.read_bytes())) for e in extract_root.rglob("*") if e.is_file()], key=lambda e: e["path"])
        if extracted != expected_bytes:
            raise ValueError("independent extracted all bytes mismatch")
        products.append(dict(name=case["name"], repackedSha256=sha(repack_path.read_bytes()), allFilesMatch=True, files=observed))
    proofs.append(dict(name=case["name"], inputSha256=sha(original), layers=layers, terminalSha256=sha(data), entries=entries))
proof = dict(manifestSha256=sha((root / "manifest.json").read_bytes()), cases=proofs, products=products, centralMetadataRejections=rejections, allLayersMatch=True)
(output / "proof.json").write_text(json.dumps(proof, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
print(json.dumps(dict(cases=len(proofs), allLayersMatch=True)))
