"""CC0。標準libraryで固定入力を生成し、製品に依存せず全内容を検証する。"""
import argparse
import bz2
import gzip
import hashlib
import io
import json
import pathlib
import struct
import tarfile
import zipfile
import zlib

ROOT = pathlib.Path(__file__).resolve().parent
LEAF = b"nested archive leaf\r\nUTF-8: \xe6\x97\xa5\xe6\x9c\xac\n"
PASSWORDS = [b"outer-source-fixture", b"inner-source-fixture"]


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def zip_bytes(entries):
    target = io.BytesIO()
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries:
            item = zipfile.ZipInfo(name, (2001, 2, 3, 4, 5, 6))
            item.compress_type = zipfile.ZIP_DEFLATED
            item.create_system = 3
            item.external_attr = (0o40755 if name.endswith("/") else 0o100644) << 16
            archive.writestr(item, data)
    return target.getvalue()


def encrypted_zip(name, data, password):
    # 古典ZipCryptoの独立encoder。検証専用password／固定headerで再現可能にする。
    table = []
    for value in range(256):
        for _ in range(8):
            value = (value >> 1) ^ (0xEDB88320 if value & 1 else 0)
        table.append(value)
    keys = [0x12345678, 0x23456789, 0x34567890]
    def update(value):
        keys[0] = (keys[0] >> 8) ^ table[(keys[0] ^ value) & 255]
        keys[1] = ((keys[1] + (keys[0] & 255)) * 134775813 + 1) & 0xFFFFFFFF
        keys[2] = (keys[2] >> 8) ^ table[(keys[2] ^ (keys[1] >> 24)) & 255]
    for value in password:
        update(value)
    crc = zlib.crc32(data)
    plain = bytes(range(11)) + bytes([crc >> 24]) + data
    encrypted = bytearray()
    for value in plain:
        temp = keys[2] | 2
        encrypted.append(value ^ ((temp * (temp ^ 1) >> 8) & 255))
        update(value)
    encoded_name = name.encode("ascii")
    size = len(encrypted)
    local = struct.pack("<I5H3I2H", 0x04034B50, 20, 1, 0, 0, 0, crc, size, len(data), len(encoded_name), 0)
    central = struct.pack("<I6H3I5H2I", 0x02014B50, 20, 20, 1, 0, 0, 0, crc, size, len(data), len(encoded_name), 0, 0, 0, 0, 0, 0)
    first = local + encoded_name + encrypted
    second = central + encoded_name
    result = first + second + struct.pack("<I4H2IH", 0x06054B50, 0, 0, 1, 1, len(second), len(first), 0)
    with zipfile.ZipFile(io.BytesIO(result)) as archive:
        assert archive.read(name, pwd=password) == data
    return result


def bad_second_crc(data):
    altered = bytearray(data)
    offset = 0
    records = []
    while True:
        offset = altered.find(b"PK\x01\x02", offset)
        if offset < 0:
            break
        name_length = struct.unpack_from("<H", altered, offset + 28)[0]
        name = altered[offset + 46:offset + 46 + name_length]
        if not name.endswith(b"/"):
            records.append(offset)
        offset += 4
    assert len(records) >= 2
    altered[records[-1] + 16] ^= 1
    return bytes(altered)


def tar_bytes(entries):
    target = io.BytesIO()
    with tarfile.open(fileobj=target, mode="w", format=tarfile.USTAR_FORMAT) as archive:
        for name, data in entries:
            item = tarfile.TarInfo(name)
            item.size = len(data)
            item.mtime = 981173106
            item.mode = 0o644
            archive.addfile(item, io.BytesIO(data))
    return target.getvalue()


def manifest_entries(data, kind="zip", password=None):
    result = []
    if kind == "zip":
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            for item in archive.infolist():
                content = b"" if item.is_dir() else archive.read(item, pwd=password)
                result.append({"path": item.filename.rstrip("/"), "directory": item.is_dir(), "size": len(content),
                               "sha256": "" if item.is_dir() else sha(content)})
    else:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
            for item in archive:
                assert item.isfile()
                content = archive.extractfile(item).read()
                result.append({"path": item.name, "directory": False, "size": len(content), "sha256": sha(content)})
    return sorted(result, key=lambda row: row["path"])


class ZeroReader:
    def __init__(self, size):
        self.remaining = size
    def read(self, size):
        size = min(size, self.remaining)
        self.remaining -= size
        return bytes(size)


def large_tar_gzip():
    target = io.BytesIO()
    raw_hash = hashlib.sha256()
    raw_size = 0
    expected = []
    with gzip.GzipFile(fileobj=target, mode="wb", mtime=0, filename="") as compressed:
        class HashWriter:
            def write(self, data):
                nonlocal raw_size
                raw_hash.update(data)
                raw_size += len(data)
                return compressed.write(data)
        with tarfile.open(fileobj=HashWriter(), mode="w|", format=tarfile.USTAR_FORMAT) as archive:
            for name in ["first.bin", "second.bin"]:
                size = 140 * 1024 * 1024
                item = tarfile.TarInfo(name)
                item.size = size
                item.mtime = 981173106
                item.mode = 0o644
                archive.addfile(item, ZeroReader(size))
                content_hash = hashlib.sha256()
                for _ in range(140):
                    content_hash.update(bytes(1024 * 1024))
                expected.append({"path": name, "directory": False, "size": size, "sha256": content_hash.hexdigest().upper()})
    return target.getvalue(), raw_size, raw_hash.hexdigest().upper(), expected


def generate():
    leaf = zip_bytes([("leaf.txt", LEAF), ("empty.txt", b""), ("folder/", b"")])
    nested = zip_bytes([("inner.zip", leaf), ("after.txt", b"validated later sibling\n")])
    small_tar = tar_bytes([("leaf.txt", LEAF), ("empty.txt", b"")])
    large, large_size, large_hash, large_expected = large_tar_gzip()
    files = {
        "leaf.zip": leaf,
        "nested.zip": nested,
        "late-bad-sibling.zip": bad_second_crc(nested),
        "bad-inner.zip": zip_bytes([("inner.zip", bad_second_crc(leaf))]),
        "unsupported-inner.zip": zip_bytes([("inner.zip", b"unsupported inner bytes")]),
        "raw-tar.zip": zip_bytes([("inner.tar", small_tar)]),
        "small.tar": small_tar,
        "unknown.gz": gzip.compress(small_tar, mtime=0),
        "unknown.bz2": bz2.compress(small_tar),
        "wrapped-inner.zip": zip_bytes([("inner.zip.gz.bz2", bz2.compress(gzip.compress(leaf, mtime=0)))]),
        "large.tar.gz": large,
        "large-tar.zip": zip_bytes([("large.tar.gz", large)]),
        "root-a.zip": zip_bytes([("same.txt", b"first root bytes A\n")]),
        "root-b.zip": zip_bytes([("same.txt", b"first root bytes B\n")]),
    }
    encrypted_inner = encrypted_zip("leaf.txt", LEAF, PASSWORDS[1])
    files["two-passwords.zip"] = encrypted_zip("inner.zip", encrypted_inner, PASSWORDS[0])
    files["plain-encrypted.zip"] = zip_bytes([("inner.zip", encrypted_inner)])
    for depth in [8, 9]:
        content = leaf
        for _ in range(depth):
            content = zip_bytes([("inner.zip", content)])
        files[f"depth-{depth}.zip"] = content
    for name, data in files.items():
        (ROOT / name).write_bytes(data)
    entries = manifest_entries(leaf)
    cases = [
        {"name": "nested", "file": "nested.zip", "chain": ["inner.zip"], "entries": entries,
         "decodedBytes": sum(len(data) for _, data in [("inner.zip", leaf), ("after.txt", b"validated later sibling\n")]) + len(LEAF),
         "maximumEntries": 5},
        {"name": "raw-tar", "file": "raw-tar.zip", "chain": ["inner.tar"], "entries": manifest_entries(small_tar, "tar"), "decodedBytes": len(small_tar)},
        {"name": "root-tar", "file": "small.tar", "chain": [], "entries": manifest_entries(small_tar, "tar"), "decodedBytes": len(small_tar)},
        {"name": "unknown-gzip", "file": "unknown.gz", "chain": [], "entries": manifest_entries(small_tar, "tar"), "decodedBytes": len(small_tar)},
        {"name": "unknown-bzip", "file": "unknown.bz2", "chain": [], "entries": manifest_entries(small_tar, "tar"), "decodedBytes": len(small_tar)},
        {"name": "wrapped-inner", "file": "wrapped-inner.zip", "chain": ["inner.zip.gz.bz2"], "entries": entries},
        {"name": "depth-eight", "file": "depth-8.zip", "chain": ["inner.zip"] * 8, "entries": entries},
        {"name": "large-tar", "file": "large-tar.zip", "chain": ["large.tar.gz"], "entries": large_expected,
         "decodedBytes": len(large) + large_size, "listOnly": True},
        {"name": "large-root-tar", "file": "large.tar.gz", "chain": [], "entries": large_expected,
         "decodedBytes": large_size, "listOnly": True},
        {"name": "two-passwords", "file": "two-passwords.zip", "chain": ["inner.zip"], "entries": manifest_entries(encrypted_inner, password=PASSWORDS[1]), "passwords": [p.decode() for p in PASSWORDS]},
        {"name": "plain-encrypted", "file": "plain-encrypted.zip", "chain": ["inner.zip"], "entries": manifest_entries(encrypted_inner, password=PASSWORDS[1]), "passwords": ["", PASSWORDS[1].decode()]},
    ]
    record = {"license": "CC0-1.0", "generator": "Python standard library; independent zipfile/tarfile decoders",
              "files": [{"name": name, "bytes": len(data), "sha256": sha(data)} for name, data in sorted(files.items())],
              "cases": cases, "largeTar": {"rawBytes": large_size, "rawSha256": large_hash, "entries": large_expected}}
    (ROOT / "manifest.json").write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def verify():
    record = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    for row in record["files"]:
        data = (ROOT / row["name"]).read_bytes()
        assert len(data) == row["bytes"] and sha(data) == row["sha256"]
    for case in record["cases"]:
        data = (ROOT / case["file"]).read_bytes()
        if case.get("listOnly"):
            if case["chain"]:
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    data = archive.read(case["chain"][0])
            raw_hash = hashlib.sha256()
            raw_size = 0
            with gzip.GzipFile(fileobj=io.BytesIO(data)) as raw:
                while chunk := raw.read(1024 * 1024):
                    raw_hash.update(chunk)
                    raw_size += len(chunk)
            assert raw_size == record["largeTar"]["rawBytes"] and raw_hash.hexdigest().upper() == record["largeTar"]["rawSha256"]
            expected = []
            with tarfile.open(fileobj=io.BytesIO(data), mode="r|gz") as archive:
                for item in archive:
                    content_hash = hashlib.sha256()
                    size = 0
                    stream = archive.extractfile(item)
                    while chunk := stream.read(1024 * 1024):
                        size += len(chunk)
                        content_hash.update(chunk)
                    expected.append({"path": item.name, "directory": False, "size": size, "sha256": content_hash.hexdigest().upper()})
        else:
            passwords = case.get("passwords", [])
            for index, name in enumerate(case["chain"]):
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    # 選択entryだけでなく外側の全entryのCRCを独立検証する。
                    for item in archive.infolist():
                        archive.read(item, pwd=passwords[index].encode() if passwords else None)
                    data = archive.read(name, pwd=passwords[index].encode() if passwords else None)
                if name.endswith(".bz2"):
                    data = gzip.decompress(bz2.decompress(data))
            if case["file"] == "unknown.gz":
                data = gzip.decompress(data)
            elif case["file"] == "unknown.bz2":
                data = bz2.decompress(data)
            kind = "tar" if case["name"] in {"root-tar", "raw-tar", "unknown-gzip", "unknown-bzip"} else "zip"
            expected = manifest_entries(data, kind, passwords[-1].encode() if passwords else None)
        assert expected == case["entries"], case["name"]
    for name in ["late-bad-sibling.zip", "bad-inner.zip"]:
        data = (ROOT / name).read_bytes()
        if name == "bad-inner.zip":
            with zipfile.ZipFile(io.BytesIO(data)) as outer:
                data = outer.read("inner.zip")
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                for item in archive.infolist():
                    archive.read(item)
        except zipfile.BadZipFile:
            pass
        else:
            raise AssertionError("bad CRC independently accepted")
    assert len((ROOT / "root-a.zip").read_bytes()) == len((ROOT / "root-b.zip").read_bytes())
    return {"allFileShaSizeVerified": True, "allNormalContainerCrcAndContentVerified": True,
            "largeTarRawBytes": record["largeTar"]["rawBytes"], "normalCases": len(record["cases"]), "badCrcRejected": 2}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--generate", action="store_true")
    parser.add_argument("--proof")
    parser.add_argument("--products")
    options = parser.parse_args()
    if options.generate:
        generate()
    proof = verify()
    if options.products:
        record = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
        products = json.loads(pathlib.Path(options.products).read_text(encoding="utf-8-sig"))
        for product in products:
            case = next(row for row in record["cases"] if row["name"] == product["caseName"])
            entry = next(row for row in case["entries"] if row["path"] == product["entry"] and not row["directory"])
            content = pathlib.Path(product["path"]).read_bytes()
            assert len(content) == entry["size"] and sha(content) == entry["sha256"]
        proof["allExportProductsVerified"] = True
        proof["exportProducts"] = len(products)
    if options.proof:
        pathlib.Path(options.proof).write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(proof))
