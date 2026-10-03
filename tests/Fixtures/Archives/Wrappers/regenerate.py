"""CC0 fixture construction. Existing reference tools are explicit, collection-only inputs."""
import argparse
import bz2
import gzip
import hashlib
import io
import json
import pathlib
import subprocess
import zipfile
import zlib


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def z(data, reference):
    result = subprocess.run([reference, "-c"], input=data, capture_output=True, timeout=30)
    if result.returncode not in (0, 2) or not result.stdout.startswith(b"\x1f\x9d"):
        raise ValueError("explicit ncompress encoder failed")
    decoded = subprocess.run([reference, "-d", "-c"], input=result.stdout, capture_output=True, timeout=30)
    if decoded.returncode or decoded.stdout != data:
        raise ValueError("official decoder full-byte mismatch")
    return result.stdout


def zip_bytes(entries):
    target = io.BytesIO()
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, content in entries:
            item = zipfile.ZipInfo(name, (2001, 2, 3, 4, 5, 6))
            item.compress_type = zipfile.ZIP_DEFLATED
            item.create_system = 3
            item.external_attr = (0o40755 if name.endswith("/") else 0o100644) << 16
            archive.writestr(item, content)
    return target.getvalue()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--z-reference", required=True)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parent
    nested = zip_bytes([("inside.txt", b"leaf archive content\n")])
    entries = [("folder/", b""), ("folder/text.txt", b"wrapper chain\r\nUTF-8: \xe6\x97\xa5\xe6\x9c\xac\n"),
               ("binary.bin", bytes(range(256)) * 17), ("contained.zip", nested), ("empty.txt", b"")]
    base = zip_bytes(entries)
    (root / "payload.zip").write_bytes(base)
    expected = [{"path": name.rstrip("/"), "directory": name.endswith("/"), "size": len(data),
                 "sha256": "" if name.endswith("/") else sha(data), "time": "2001-02-03T04:05:06"} for name, data in entries]
    cases = []
    def add(name, layers, source="payload.zip", data=None, valid=True, note=""):
        current = (root / source).read_bytes() if data is None else data
        chain = []
        for suffix in layers:
            previous = current
            current = gzip.compress(current, mtime=0) if suffix == ".gz" else bz2.compress(current) if suffix == ".bz2" else z(current, args.z_reference)
            chain.insert(0, {"wrapper": suffix, "decodedBytes": len(previous), "decodedSha256": sha(previous)})
        (root / name).write_bytes(current)
        cases.append({"name": name, "source": source, "bytes": len(current), "sha256": sha(current),
                      "layers": chain, "valid": valid, "note": note})
    for suffix in [".gz", ".bz2", ".Z"]:
        add("payload.zip" + suffix, [suffix])
    for layers in [[".gz", ".gz"], [".bz2", ".bz2"], [".Z", ".Z"], [".Z", ".bz2", ".gz"], [".gz", ".Z", ".bz2"], [".gz"] * 8]:
        add("payload.zip" + "".join(layers), layers)
    for extension in ["jar", "ear", "war", "xpi"]:
        add("payload." + extension + ".gz", [".gz"])
    for name in ["7Zip.solid.7z", "Rar5.solid.rar", "Zip.deflate.WinzipAES.zip", "7Zip.LZMA2.Aes.7z", "Rar5.encrypted_filesAndHeader.rar"]:
        for suffix in [".gz", ".bz2", ".Z"]:
            add(name + suffix, [suffix], source="../" + name)
    special = io.BytesIO()
    with gzip.GzipFile(filename="../../outside.tar", fileobj=special, mode="wb", mtime=0) as encoder:
        encoder.write(base)
    specials = {"fname.zip.gz": special.getvalue(),
                "concat.zip.gz": gzip.compress(base[:79], mtime=0) + gzip.compress(base[79:], mtime=0),
                "empty-member.zip.gz": gzip.compress(b"", mtime=0) + gzip.compress(base, mtime=0),
                "concat.zip.bz2": bz2.compress(base[:79]) + bz2.compress(base[79:]),
                "empty-member.zip.bz2": bz2.compress(b"") + bz2.compress(base)}
    gz = gzip.compress(base, mtime=0)
    header = bytearray(gz[:10]); header[3] = 2
    header_crc = (zlib.crc32(header) & 0xffff).to_bytes(2, "little")
    specials["fhcrc.zip.gz"] = bytes(header) + header_crc + gz[10:]
    for name, data in specials.items():
        (root / name).write_bytes(data)
        cases.append({"name": name, "source": "payload.zip", "bytes": len(data), "sha256": sha(data),
                      "layers": [{"wrapper": ".bz2" if name.endswith("bz2") else ".gz", "decodedBytes": len(base), "decodedSha256": sha(base)}], "valid": True})
    bad = {"bad-gzip-crc.zip.gz": gzip.compress(base, mtime=0)[:-8] + b"\x00" * 4 + gzip.compress(base, mtime=0)[-4:],
           "bad-gzip-size.zip.gz": gzip.compress(base, mtime=0)[:-4] + b"\x00" * 4,
           "bad-gzip-short.zip.gz": gzip.compress(base, mtime=0)[:-3],
           "bad-gzip-trailing.zip.gz": gzip.compress(base, mtime=0) + b"garbage",
           "bad-gzip-late.zip.gz": gzip.compress(base, mtime=0) + gzip.compress(b"", mtime=0)[:-2],
           "bad-bzip-footer.zip.bz2": bz2.compress(base)[:-5],
           "bad-bzip-trailing.zip.bz2": bz2.compress(base) + b"garbage",
           "bad-bzip-level.zip.bz2": bz2.compress(base) + b"BZh0",
           "bad-bzip-short.zip.bz2": bz2.compress(base) + b"BZh",
           "bad-z-header.zip.Z": b"\x1f\x9d\xe8\x00\x00",
           "bad-z-first-code.zip.Z": b"\x1f\x9d\x90\x01\x01",
           "bad-magic.zip.bz2": gzip.compress(base, mtime=0),
           "bad-empty.zip.gz": gzip.compress(b"", mtime=0)}
    flags = bytearray(gz); flags[3] = 0x20
    crc_header = bytearray(specials["fhcrc.zip.gz"]); crc_header[10] ^= 1
    bzip_crc = bytearray(bz2.compress(base)); bzip_crc[-6] ^= 0x80
    bad.update({"bad-gzip-flags.zip.gz": bytes(flags), "bad-gzip-fhcrc.zip.gz": bytes(crc_header),
                "bad-bzip-crc.zip.bz2": bytes(bzip_crc), "bad-z-future.zip.Z": bytes.fromhex("1f9d90000402")})
    inner_crc = bytearray(base)
    central = inner_crc.index(b"PK\x01\x02", inner_crc.index(b"PK\x01\x02") + 4)
    inner_crc[central + 16] ^= 0x80
    bad["bad-inner-crc.zip.gz"] = gzip.compress(inner_crc, mtime=0)
    inner_size = bytearray(base)
    inner_size[central + 24:central + 28] = (int.from_bytes(inner_size[central + 24:central + 28], "little") - 1).to_bytes(4, "little")
    bad["bad-inner-size.zip.gz"] = gzip.compress(inner_size, mtime=0)
    inner_link = io.BytesIO()
    with zipfile.ZipFile(inner_link, "w") as archive:
        item = zipfile.ZipInfo("link"); item.create_system = 3; item.external_attr = 0o120777 << 16
        archive.writestr(item, b"outside")
    bad["bad-inner-link.zip.gz"] = gzip.compress(inner_link.getvalue(), mtime=0)
    for name, data in bad.items():
        (root / name).write_bytes(data)
        cases.append({"name": name, "bytes": len(data), "sha256": sha(data), "valid": False})
    add("bad-depth.zip" + ".gz" * 9, [".gz"] * 9, valid=False, note="default depth exceeds eight")
    add("bad-terminal.7z.gz", [".gz"], valid=False, note="ZIP bytes with explicit 7z terminal")
    for name, bad_entries in [("bad-path.zip.gz", [("../outside", b"unsafe")]), ("bad-collision.zip.gz", [("A.txt", b"a"), ("a.txt", b"b")])]:
        add(name, [".gz"], data=zip_bytes(bad_entries), valid=False)
    manifest = {"license": "CC0-1.0 for constructed payload and scripts; source archives retain ../LICENSE.txt (MIT)",
                "terminal": {"name": "payload.zip", "bytes": len(base), "sha256": sha(base), "entries": expected},
                "reference": {"name": "ncompress 5.1", "binarySha256": sha(pathlib.Path(args.z_reference).read_bytes()),
                              "sourceProvenance": "../TarZ/manifest.json"},
                "cases": cases}
    (root / "manifest.json").write_bytes((json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
