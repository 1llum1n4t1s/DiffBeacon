"""Independent stdlib reader; Z scope is fixed nonblock 9-bit literal fixtures only."""
import argparse
import bz2
import gzip
import hashlib
import io
import json
import pathlib
import tarfile
import datetime
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def literal_z_decode(data):
    if data[:3] != b"\x1f\x9d\x09":
        raise ValueError("not the fixture's fixed nonblock 9-bit stream")
    packed = data[3:]
    result = bytearray()
    for bit in range(0, len(packed) * 8 - 8, 9):
        byte, shift = divmod(bit, 8)
        code = int.from_bytes(packed[byte:byte + 2], "little") >> shift & 511
        if code > 255:
            raise ValueError("nonliteral code outside this independent fixture reader")
        result.append(code)
    return bytes(result)


def decode(data, wrapper):
    if wrapper == ".gz":
        return gzip.decompress(data)
    if wrapper == ".Z":
        return literal_z_decode(data)
    result = bytearray()
    while data:
        if not data.startswith(b"BZh"):
            raise ValueError("bzip2 trailing content/header")
        reader = bz2.BZ2Decompressor()
        result.extend(reader.decompress(data))
        if not reader.eof:
            raise EOFError("bzip2 member truncated")
        data = reader.unused_data
    return bytes(result)


def entries(data):
    result = []
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:", encoding="utf-8", errors="strict") as archive:
        for item in archive:
            if not (item.isdir() or item.isfile()):
                raise ValueError("unexpected special entry")
            content = b"" if item.isdir() else archive.extractfile(item).read()
            result.append(dict(path=item.name.rstrip("/"), directory=item.isdir(), size=len(content),
                               sha256="" if item.isdir() else sha(content), mode=item.mode,
                               uid=item.uid, gid=item.gid, mtime=item.mtime))
    return sorted(result, key=lambda entry: entry["path"])


def verify(root):
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    proofs = []
    for case in manifest["cases"]:
        data = (root / case["name"]).read_bytes()
        assert sha(data) == case["sha256"] and len(data) == case["bytes"], case["name"]
        if not case["valid"]:
            name = case["name"]
            depth = 0
            failure = None
            try:
                while True:
                    wrapper = next((kind for kind in [".gz", ".bz2", ".Z"] if name.endswith(kind)), None)
                    if wrapper is None:
                        break
                    name = name[:-len(wrapper)]
                    depth += 1
                    data = decode(data, wrapper)
                if depth > 8:
                    raise ValueError("fixed depth limit exceeded")
                entries(data)
                if len(data) % 512 or not data.endswith(bytes(1024)):
                    raise ValueError("TAR block/terminator contract")
            except (ValueError, OSError, EOFError, tarfile.TarError) as error:
                failure = type(error).__name__
            assert failure is not None, case["name"]
            proofs.append(dict(name=case["name"], inputShaVerified=True, productMustReject=case["reason"],
                               independentFailure=failure))
            continue
        layers = []
        for layer in case["layers"]:
            data = decode(data, layer["wrapper"])
            assert sha(data) == layer["decodedSha256"] and len(data) == layer["decodedBytes"], case["name"]
            layers.append(dict(wrapper=layer["wrapper"], decodedSha256=sha(data), decodedBytes=len(data)))
        assert sha(data) == case["tarSha256"], case["name"]
        assert entries(data) == case["entries"], case["name"]
        proofs.append(dict(name=case["name"], inputShaVerified=True, layers=layers, allTarBytesMatch=True,
                           allEntryBytesAndMetadataMatch=True, entries=len(case["entries"])))
    for source in manifest["sources"]:
        import zipfile
        data = (root / source["name"]).read_bytes()
        assert sha(data) == source["sha256"]
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            assert archive.namelist() == [source["entry"]]
            assert archive.read(source["entry"]) == (root / source["fixture"]).read_bytes()
        proofs.append(dict(name=source["name"], allContainerEntryBytesMatch=True))
    return dict(cases=len(manifest["cases"]), sources=len(manifest["sources"]), proofs=proofs)


def verify_evidence(root, evidence_path):
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    cases = {case["name"]: case for case in manifest["cases"] if case["valid"]}
    evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
    full_cases = set()
    sources = {source["name"]: source for source in manifest["sources"]
               if source["fixture"] in cases and len(cases[source["fixture"]]["layers"]) < 8}
    source_cases = set()
    proofs = []
    def verify_list(actual, expected, require_time=False):
        assert actual["format"] == expected["format"]
        assert len(actual["entries"]) == len(expected["entries"])
        by_name = {item["path"]: item for item in actual["entries"]}
        assert len(by_name) == len(expected["entries"])
        for item in expected["entries"]:
            value = by_name[item["path"]]
            assert all(value[field] == item[field] for field in ["directory", "size", "sha256"])
            assert value["encrypted"] is False
            if require_time or "modifiedTime" in value:
                assert int(datetime.datetime.fromisoformat(value["modifiedTime"].replace("Z", "+00:00")).timestamp()) == item["mtime"]
    def verify_files(paths, expected):
        files = [item for item in expected["entries"] if not item["directory"]]
        assert set(paths) == {item["path"] for item in files}
        for item in files:
            data = pathlib.Path(paths[item["path"]]).read_bytes()
            assert len(data) == item["size"] and sha(data) == item["sha256"]
    for record in evidence:
        name = record["caseName"]
        case = cases[name]
        kind = record["kind"]
        assert kind in ("physical", "source"), "unknown evidence kind"
        if kind == "physical":
            assert name not in full_cases, "duplicate physical case"
            assert "extracted" in record and "repacked" in record
        else:
            source_name = record["sourceName"]
            assert source_name in sources and source_name not in source_cases, "unknown or duplicate source case"
            assert sources[source_name]["fixture"] == name, "wrong source fixture mapping"
            assert "extracted" not in record, "source evidence replaced by physical evidence"
            source_cases.add(source_name)
        verify_list(record["list"], case, require_time=kind == "source")
        verify_files(record["exports"], case)
        if kind == "physical":
            assert record["typedList"] is not None
            verify_list(record["typedList"], case, require_time=True)
            verify_files(record["typedExports"], case)
        if kind == "physical":
            extracted = pathlib.Path(record["extracted"])
            assert extracted.is_dir()
            actual_files = {path.relative_to(extracted).as_posix(): str(path) for path in extracted.rglob("*") if path.is_file()}
            verify_files(actual_files, case)
            actual_dirs = {path.relative_to(extracted).as_posix() for path in extracted.rglob("*") if path.is_dir()}
            assert actual_dirs == {item["path"] for item in case["entries"] if item["directory"]}
            with zipfile.ZipFile(record["repacked"]) as archive:
                items = archive.infolist()
                assert len(items) == len(case["entries"])
                actual = {item.filename.rstrip("/"): item for item in items}
                assert len(actual) == len(case["entries"])
                for item in case["entries"]:
                    zipped = actual[item["path"]]
                    assert zipped.is_dir() == item["directory"]
                    data = archive.read(zipped)
                    assert len(data) == item["size"]
                    assert ("" if item["directory"] else sha(data)) == item["sha256"]
            full_cases.add(name)
        proofs.append(dict(caseName=name, allListEntryBytesTimeMatch=True, allExportBytesMatch=True,
                           extractionAndRepackedZipIndependent="extracted" in record))
    assert full_cases == set(cases), "missing full normal CLI case"
    assert source_cases == set(sources), "missing normal typed source case"
    assert len(evidence) == len(cases) + len(sources), "unexpected evidence records"
    return dict(fullNormalCases=len(full_cases), evidenceRecords=len(evidence), proofs=proofs)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--evidence")
    args = parser.parse_args()
    proof = verify(pathlib.Path(__file__).resolve().parent)
    if args.evidence:
        proof["cliEvidence"] = verify_evidence(pathlib.Path(__file__).resolve().parent, pathlib.Path(args.evidence))
    pathlib.Path(args.output).write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(dict(cases=proof["cases"], sources=proof["sources"], allMatched=True)))
