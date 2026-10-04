"""CC0 fixed TAR wrapper fixtures. No external executables or downloads."""
import argparse
import bz2
import gzip
import io
import json
import pathlib
import zipfile
from verify import entries, sha, verify


def literal(data):
    # Existing TarZ/regenerate.py's independent nonblock literal packaging.
    result = bytearray(b"\x1f\x9d\x09")
    accumulator = bits = 0
    for value in data:
        accumulator |= value << bits
        bits += 9
        while bits >= 8:
            result.append(accumulator & 255)
            accumulator >>= 8
            bits -= 8
    if bits:
        result.append(accumulator & 255)
    return bytes(result)


def wrap(data, kind):
    return gzip.compress(data, mtime=0) if kind == ".gz" else bz2.compress(data) if kind == ".bz2" else literal(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parent
    output = pathlib.Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    tar = (root.parent / "TarZ/small_repeated.tar").read_bytes()
    old_z = (root.parent / "TarZ/literal-nonblock-b9.tar.Z").read_bytes()
    assert sha(tar) == "73C8B483CFD3C0DAAC791C931FA5DC871ADF7B8BEA2FF6D6251A9DC22451BF9E"
    assert sha(old_z) == "790E51F22C75EBA2ACC2EE85B25D43782651315618EF23A4E5DAADA891A5439D"
    assert literal(tar) == old_z
    cases = []
    def add(name, kinds, raw=tar, valid=True, reason="", limits=None):
        data = raw
        layers = []
        for kind in kinds:
            layers.insert(0, dict(wrapper=kind, decodedBytes=len(data), decodedSha256=sha(data)))
            data = wrap(data, kind)
        (output / name).write_bytes(data)
        case = dict(name=name, bytes=len(data), sha256=sha(data), valid=valid, layers=layers)
        if valid:
            case.update(tarSha256=sha(raw), entries=entries(raw), format="tar" + "".join(kinds))
        else:
            case["reason"] = reason
        if limits:
            case["limits"] = limits
        cases.append(case)
        return data
    for kinds in [[".gz", ".bz2"], [".bz2", ".gz"], [".Z", ".gz"], [".gz", ".Z"],
                  [".gz", ".gz"], [".bz2", ".bz2"], [".Z", ".Z"], [".Z", ".bz2", ".gz"], [".gz"] * 8]:
        add("payload.tar" + "".join(kinds), kinds)
    for alias, kind in [("tgz", ".gz"), ("tbz", ".bz2"), ("tbz2", ".bz2"), ("taz", ".Z")]:
        for outer in [".gz", ".bz2", ".Z"]:
            add("payload." + alias + outer, [kind, outer])
    add("empty.tar.gz.bz2", [".gz", ".bz2"], bytes(10240))
    add("bad-depth.tar" + ".gz" * 9, [".gz"] * 9, valid=False, reason="shared wrapper depth")
    for kind in [".gz", ".bz2", ".Z"]:
        wrapped = wrap(tar, kind)
        broken = bytearray(wrapped)
        # gzip/bzip CRC; Z invalid header, as Z has no CRC.
        index = len(broken) - 8 if kind == ".gz" else len(broken) - 3 if kind == ".bz2" else 2
        broken[index] ^= 64
        add("bad-inner.tar" + kind + ".gz", [".gz"], bytes(broken), False, "inner " + kind + " integrity")
        outer = bytearray(wrap(wrap(tar, ".gz"), kind))
        index = len(outer) - 8 if kind == ".gz" else len(outer) - 3 if kind == ".bz2" else 2
        outer[index] ^= 64
        name = "bad-outer.tar.gz" + kind
        (output / name).write_bytes(outer)
        cases.append(dict(name=name, bytes=len(outer), sha256=sha(outer), valid=False, layers=[], reason="outer " + kind + " integrity"))
        add("bad-magic.tar" + kind + ".gz", [".gz"], b"wrong-wrapper", False, "inner wrapper format mismatch")
        if kind != ".Z":
            add("bad-inner-short.tar" + kind + ".gz", [".gz"], wrapped[:-5], False, "inner " + kind + " EOF")
            add("bad-inner-trailing.tar" + kind + ".gz", [".gz"], wrapped + b"invalid-tail", False, "inner " + kind + " trailing content")
            truncated = wrap(wrap(tar, ".gz"), kind)[:-5]
            name = "bad-outer-short.tar.gz" + kind
            (output / name).write_bytes(truncated)
            cases.append(dict(name=name, bytes=len(truncated), sha256=sha(truncated), valid=False, layers=[], reason="outer " + kind + " EOF"))
    middle = bytearray(wrap(wrap(tar, ".gz"), ".bz2")); middle[-3] ^= 64
    add("bad-middle.tar.gz.bz2.gz", [".gz"], bytes(middle), False, "middle bzip2 integrity")
    damaged = bytearray(tar); damaged[0] ^= 1
    add("bad-checksum.tar.gz.bz2", [".gz", ".bz2"], bytes(damaged), False, "TAR header checksum")
    add("bad-footer.tar.gz.bz2", [".gz", ".bz2"], tar.rstrip(b"\0"), False, "TAR truncated body/footer")
    add("bad-trailing.tar.gz.bz2", [".gz", ".bz2"], tar + b"not-zero", False, "TAR trailing content")
    add("bad-empty.tar.gz.bz2", [".gz", ".bz2"], b"", False, "empty wrapper terminal")
    add("bad-disguised.tar.gz.bz2", [".gz", ".bz2"], (root.parent / "Wrappers/payload.zip").read_bytes(), False, "ZIP masquerading as TAR")
    sources = []
    for fixture in ["payload.tar.gz.bz2", "payload.taz.gz", "bad-checksum.tar.gz.bz2", "payload.tar" + ".gz" * 8]:
        name = "source-" + fixture + ".zip"
        target = io.BytesIO()
        with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_STORED) as archive:
            item = zipfile.ZipInfo("nested/" + fixture, (2001, 2, 3, 4, 5, 6))
            item.create_system = 3
            item.external_attr = 0o100644 << 16
            archive.writestr(item, (output / fixture).read_bytes())
        data = target.getvalue(); (output / name).write_bytes(data)
        sources.append(dict(name=name, entry="nested/" + fixture, fixture=fixture, bytes=len(data), sha256=sha(data)))
    manifest = dict(schema=1, provenance="CC0 TarZ fixed original/literal; Python stdlib wrappers", cases=cases, sources=sources,
                    budgetCases=[dict(fixture="payload.tar.gz.bz2", maximumEntryBytes=len(tar)-1, reason="intermediate size"),
                                 dict(fixture="payload.tar.gz.bz2", maximumDecodedBytes=len(tar), reason="cumulative decoded"),
                                 dict(fixture="payload.tar.gz.bz2", maximumWorkBytes=64, reason="cumulative work")])
    if output != root:
        fixed = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
        assert manifest == fixed, "regeneration differs from fixed manifest; preserve originals"
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    proof = verify(output)
    (output / "independent-proof.json").write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(dict(cases=proof["cases"], sources=proof["sources"], allMatched=True, manifestSha256=sha((output / "manifest.json").read_bytes()))))


if __name__ == "__main__":
    main()
