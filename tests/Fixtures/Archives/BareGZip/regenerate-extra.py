# SPDX-License-Identifier: CC0-1.0
"""独立したgzip/ZIP fixtureを指定した外部出力先へ生成する。製品codecを使わない。"""
import argparse, hashlib, json, pathlib, struct, zipfile, zlib


def member(payload, name):
    header = b"\x1f\x8b\x08\x08" + struct.pack("<I", 946684800) + b"\x00\xff" + name + b"\x00"
    codec = zlib.compressobj(9, zlib.DEFLATED, -15)
    return header + codec.compress(payload) + codec.flush() + struct.pack("<II", zlib.crc32(payload), len(payload))


def generate(output):
    output.mkdir(exist_ok=False)
    payload = b"\xef\xbb\xbf" + "gzip text: 日本\r\nnext\n".encode("utf-8")
    binary = bytes(range(256))
    inner = member(payload, "café.txt".encode("utf-8"))
    binary_inner = member(binary, "café.bin".encode("utf-8"))
    data = {
        "nested-mixed.gz": member(inner, "階層.gz".encode("cp932")),
        "nested-binary.gz": member(binary_inner, "階層.gz".encode("cp932")),
        "utf8-inner.gz": inner,
        "binary-inner.gz": binary_inner,
        "windows-1252.gz": member(binary, "Euro-€.bin".encode("cp1252")),
        "windows-1251.gz": member(binary, "имя.bin".encode("cp1251")),
        "encoded-bad-tail.gz": member(binary, "階層.bin".encode("cp932"))[:-8] + b"\x00" * 8,
        "traversal-c1.gz": member(binary, b"../\x93\xfa.bin"),
        "traversal-invalid-utf8.gz": member(binary, b"dir/../\xc3(.bin"),
        "traversal-invalid-dbcs.gz": member(binary, b"dir\\..\\\x81"),
        "dbcs-backslash-trail.gz": member(binary, "表.bin".encode("cp932")),
    }
    manifest = {"license": "CC0-1.0", "textHex": payload.hex().upper(), "binaryHex": binary.hex().upper(), "cases": []}
    with zipfile.ZipFile(output / "extra-inputs.zip", "w", zipfile.ZIP_STORED) as archive:
        for name, content in data.items():
            info = zipfile.ZipInfo(name, (2000, 1, 1, 0, 0, 0))
            archive.writestr(info, content)
            manifest["cases"].append({"input": name, "size": len(content), "sha256": hashlib.sha256(content).hexdigest().upper()})
    (output / "extra-expected.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    generate(pathlib.Path(parser.parse_args().output))
