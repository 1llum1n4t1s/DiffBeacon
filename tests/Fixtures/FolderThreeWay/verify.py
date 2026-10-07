"""固定原本と実アプリ成果物の独立reader。比較算法を期待値に再利用しない。"""
import base64
import hashlib
import json
import os
import pathlib
import struct
import sys
import zlib


def sha(body):
    return hashlib.sha256(body).hexdigest().upper()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(value, message):
    if not value:
        raise AssertionError(message)


def png(path, size):
    data = path.read_bytes()
    require(data[:8] == b"\x89PNG\r\n\x1a\n", f"PNG signature: {path}")
    offset, pixels, ended, header = 8, bytearray(), False, None
    while offset < len(data):
        require(offset + 12 <= len(data), "truncated PNG chunk")
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        require(length <= len(data) - offset - 12, "PNG chunk outside file")
        kind = data[offset + 4:offset + 8]
        body = data[offset + 8:offset + 8 + length]
        crc = struct.unpack(">I", data[offset + 8 + length:offset + 12 + length])[0]
        require(zlib.crc32(kind + body) & 0xffffffff == crc, "PNG CRC")
        if kind == b"IHDR":
            require(header is None and offset == 8 and length == 13, "PNG IHDR order")
            header = struct.unpack(">IIBBBBB", body)
            require(header[:2] == size and header[2] == 8 and header[3] in (2, 6)
                    and header[4:] == (0, 0, 0), "PNG geometry/format")
        elif kind == b"IDAT":
            pixels.extend(body)
        elif kind == b"IEND":
            require(length == 0, "PNG IEND length")
            ended = True
        offset += length + 12
        if ended:
            break
    require(ended and offset == len(data) and header is not None, "PNG complete end")
    decoder = zlib.decompressobj()
    stride = size[0] * (4 if header[3] == 6 else 3) + 1
    decoded = decoder.decompress(pixels, stride * size[1] + 1)
    require(decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail
            and len(decoded) == stride * size[1], "PNG independent zlib/scanline length")
    require(all(decoded[index] <= 4 for index in range(0, len(decoded), stride)), "PNG scanline filter")
    return {"path": str(path), "bytes": len(data), "sha256": sha(data), "dimensions": size}


def verify(fixture, work):
    original = fixture / "original"
    golden_body = (original / "expected.ndjson").read_bytes()
    transport_body = (original / "transport.ndjson").read_bytes()
    require(sha(golden_body) == "C9B4165F49D74704828734E47D3AB7073233B301A733E96D0739302DE92978AB", "fixed golden SHA")
    require(sha(transport_body) == "A251B307151D073AA6F36B5A7434F9EBFDC99386D20744C20A240DF4C742FEE7", "fixed transport SHA")
    for item in read(original / "sha256-manifest.json"):
        body = (original / item["path"]).read_bytes()
        require(len(body) == item["bytes"] and sha(body) == item["sha256"].upper(), "original harness manifest " + item["path"])
    require(read(original / "capture-status.json")["complete"], "original capture complete")
    require(read(original / "capture-reader.json")["complete"], "original independent acceptance")
    # 原文copy/sliceはrepo原本へbyte照合。採取済みgoldenを再生成しない。
    repo = fixture.parents[2]
    for item in read(original / "provenance.json"):
        body = (repo / item["source"]).read_bytes()
        require(sha(body) == item["sourceSha256"].upper(), "upstream source SHA " + item["source"])
        if "copy" in item:
            require((original / item["copy"]).read_bytes() == body, "source copy bytes")
        if "slice" in item:
            expected = b"".join(body.splitlines(keepends=True)[item["first"] - 1:item["last"]])
            if "byteStart" in item:
                expected = body[item["byteStart"]:item["byteStart"] + item["byteLength"]]
            require((original / item["slice"]).read_bytes() == expected, "source slice bytes")
    cases = [json.loads(line) for line in transport_body.decode("utf-8-sig").splitlines()]
    golden = {row["id"]: row["original"] for row in map(json.loads, golden_body.decode("utf-8-sig").splitlines())}
    require(len(cases) == len(golden) == 63, "exact 63 original cases")
    records = read(work / "commands.json")
    commands = {row["id"]: row for row in records}
    protection_names = ["third-root-trailing"] + (["third-root-extended-double-trailing", "third-root-device-double-trailing"] if os.name == "nt" else [])
    gui_protection_names = protection_names.copy()
    if os.name == "nt":
        environment = read(work / "physical-alias-environment.json")
        third = str(work / "third-root-localhost-share" / "right" / "third")
        require(environment["canonicalRoot"] == third and environment["middleInput"] == "\\\\localhost\\" + third[0] + "$" + third[2:], "CLI exact existing local share candidate")
        require(environment["eligible"] == os.path.isdir(environment["middleInput"]), "CLI observed share availability")
        if environment["eligible"]:
            require(os.path.samefile(third, environment["middleInput"]), "CLI share actual directory identity")
            protection_names.append("third-root-localhost-share")
    long_names = []
    long_aliases = []
    if os.name == "nt":
        environment = read(work / "long-alias-environment.json")
        canonical = environment["canonicalRoot"]
        share = "\\\\localhost\\" + canonical[0] + "$" + canonical[2:]
        require(environment["shareInput"] == share, "long existing share candidate")
        require(len(canonical) >= 270 and all(len(part) <= 80 for part in pathlib.Path(canonical).parts), "long probe canonical length/components")
        extended = "\\\\?\\UNC\\" + share[2:]
        require(environment["eligible"] == os.path.isdir(extended), "long share extended availability")
        if environment["eligible"]:
            require(os.path.samefile(canonical, extended), "long share actual directory identity")
        long_aliases = ["device"] + (["device-unc"] if environment["eligible"] else [])
        long_names = ["long-" + alias + "-" + kind for alias in long_aliases
                      for kind in ["two-read-0", "two-read-2", "three-read-0", "three-read-1", "three-read-2", "two-copy-left-to-right", "two-copy-right-to-left"]]
        protection_names.append("third-root-long-device-double-trailing")
        gui_protection_names.append("third-root-long-device-double-trailing")
    expected_cli_count = 27 + len(protection_names) + len(long_names)
    require(len(records) == len(commands) == expected_cli_count, "exact permanent CLI cases for platform")
    model_record = commands["original63"]
    require(model_record["command"]["ExitCode"] == 1, "model exit")
    model = json.loads(model_record["command"]["Stdout"])
    entries = {row["path"]: row for row in model["entries"]}
    require(len(entries) == 63 and model["threeWay"] is True, "model leaf count/threeWay")
    roots = [pathlib.Path(model_record["work"]) / name for name in ("left", "middle", "right")]
    comparisons = 0
    for case in cases:
        source = golden[case["id"]]
        row = entries[case["id"] + ".txt"]
        flags = source["flagsAfter"]
        same = flags & 0x7000 == 0x2000
        classification = None if same else {0: "AllChanged", 0x8000: "OnlyLeft", 0x10000: "OnlyMiddle", 0x18000: "OnlyRight"}[flags & 0x18000]
        require(row["presence"] == case["presenceMask"], case["id"] + " presence")
        require((row["status"] == "Equal") == same and row["status"] != "Error", case["id"] + " status")
        require(row["classification"] == classification, case["id"] + " classification")
        for key, pair in zip(("middleLeft", "middleRight", "leftRight"), source["pairs"]):
            equal = not any(not change["trivial"] for change in pair["rawChanges"])
            require((row[key]["status"] == "Equal") == equal and row[key]["status"] != "Error", case["id"] + " " + key)
        for side, payload in enumerate(case["sides"]):
            path = roots[side] / (case["id"] + ".txt")
            body = base64.b64decode(payload["bytesBase64"], validate=True)
            require(sha(body) == payload["sha256"].upper(), "transport payload SHA")
            present = bool(case["presenceMask"] & (1 << side))
            require(path.exists() == present, "exact presence bytes")
            if present:
                require(path.read_bytes() == body, "original source bytes retained")
        comparisons += 6

    copied = 0
    for record in records:
        before = {entry["Path"]: entry for entry in record["before"]}
        after = {entry["Path"]: entry for entry in record["after"]}
        require(set(before) == set(after), record["id"] + " exact input file set")
        location = pathlib.Path(record["work"])
        for entry in record["after"]:
            body = (location / entry["Path"]).read_bytes()
            require(len(body) == entry["Bytes"] and sha(body) == entry["Sha256"], "actual after bytes/SHA")
        if record["source"] is not None:
            source, target = record["source"], record["target"]
            names = ("left", "middle", "right")
            require(record["command"]["ExitCode"] == 0, record["id"] + " copy exit")
            result = json.loads(record["command"]["Stdout"])
            require(result["published"] == 1, record["id"] + " actual publication")
            expected = [b"left\n", b"middle\n", b"right\n"]
            if record["id"].startswith("equal-pair-"):
                expected[target] = expected[source]
            expected[target] = expected[source]
            for side, name in enumerate(names):
                require((location / name / "entry.txt").read_bytes() == expected[side], record["id"] + " bytes " + name)
                if side != target:
                    require(before[name + "/entry.txt"] == after[name + "/entry.txt"], record["id"] + " noncopy metadata")
            copied += 1
        else:
            require(before == after, record["id"] + " input metadata preserved")
            expected_exit = 1 if record["id"] == "original63" else 0 if record["id"].startswith("workspace-") or record["id"] in {"source-absent", "filtered", "error-skip"} else 1 if record["id"] in long_names else 2
            require(record["command"]["ExitCode"] == expected_exit, record["id"] + " rejection/roundtrip exit")
            if record["id"] in {"source-absent", "filtered", "error-skip"}:
                require(json.loads(record["command"]["Stdout"])["published"] == 0, "absent/filtered/error no publication")
    require(copied == 18 + 2 * len(long_aliases), "18 baseline and two long alias copy publications per namespace")
    for name in protection_names:
        record = commands[name]
        location = pathlib.Path(record["work"])
        expected = {"left/third/entry.txt": b"new\n", "right/third/entry.txt": b"protected\n", "right/entry.txt": b"right\n"}
        require({entry["Path"] for entry in record["after"]} == set(expected), name + " exact protected file set")
        for relative, body in expected.items():
            require((location / relative).read_bytes() == body, name + " fixed bytes " + relative)
        arguments = record["command"]["Arguments"]
        actual_middle = arguments[arguments.index("--middle") + 1]
        third = str(location / "right" / "third")
        prefix = {"third-root-extended-double-trailing": "\\\\?\\", "third-root-device-double-trailing": "\\\\.\\", "third-root-long-device-double-trailing": "\\\\.\\"}.get(name)
        expected_middle = "\\\\localhost\\" + third[0] + "$" + third[2:] if name == "third-root-localhost-share" else prefix + third + os.sep * 2 if prefix else third + os.sep
        require(actual_middle == expected_middle, name + " exact namespace and trailing separators")
    def long_alias(root, alias):
        return '\\\\.\\UNC\\localhost\\' + root[0] + "$" + root[2:] if alias == "device-unc" else '\\\\.\\' + root

    for name in long_names + (["third-root-long-device-double-trailing"] if os.name == "nt" else []):
        record = commands[name]
        command = record["command"]
        location = pathlib.Path(record["work"])
        roots = [str(location / side) for side in ("left", "middle", "right")]
        require(all(len(root) >= 270 and all(len(part) <= 80 for part in pathlib.Path(root).parts) for root in roots), name + " long canonical roots/components")
        require(command["Pid"] > 0 and command["CreationUtc"] and command["LaunchUtc"] and command["ExitObservedUtc"], name + " actual process identity and completion")
        arguments = command["Arguments"]
        if name == "third-root-long-device-double-trailing":
            require(record["before"] == record["after"], name + " all metadata retained")
            stdout = command["Stdout"].strip()
            require(not stdout or json.loads(stdout).get("published", 0) == 0, name + " zero publication")
            require(command["ExitCode"] == 2, name + " protection refusal exit")
            continue
        alias = "device-unc" if name.startswith("long-device-unc-") else "device"
        kind = name[len("long-" + alias + "-"):]
        if "read" in kind:
            side = int(kind[-1])
            expected_arguments = ["--directory", roots[0], roots[2]]
            if kind.startswith("three"):
                expected_arguments += ["--middle", roots[1]]
            expected_arguments[{0: 1, 2: 2, 1: 4}[side]] = long_alias(roots[side], alias)
            require(arguments == expected_arguments, name + " raw alias exact side/namespace")
            result = json.loads(command["Stdout"])
            require(command["ExitCode"] == 1 and result.get("threeWay", False) == kind.startswith("three"), name + " successful different comparison")
            require(len(result["entries"]) == 1 and result["entries"][0]["path"] == "entry.txt"
                    and result["entries"][0]["status"] == "Modified", name + " read all expected leaf")
            if kind.startswith("three"):
                require(result["entries"][0]["classification"] == "AllChanged"
                        and all(result["entries"][0][key]["status"] == "Modified" for key in ("middleLeft", "middleRight", "leftRight")), name + " all three pair reads")
            expected = [b"left\n", b"middle\n", b"right\n"]
        else:
            direction = kind[len("two-copy-"):]
            require(arguments == ["--folder-sync", long_alias(roots[0], alias), long_alias(roots[2], alias),
                                  "--direction", direction, "--copy", "all", "--select", "entry.txt"], name + " raw source and destination aliases")
            result = json.loads(command["Stdout"])
            require(command["ExitCode"] == 0 and result["published"] == 1, name + " copy publication")
            expected = [b"left\n", b"middle\n", b"right\n"]
            source, target = (0, 2) if direction == "left-to-right" else (2, 0)
            expected[target] = expected[source]
        for root, body in zip(roots, expected):
            require((pathlib.Path(root) / "entry.txt").read_bytes() == body, name + " independent all three bytes")

    budget = json.loads(commands["content-budget"]["command"]["Stdout"])
    require(len(budget["entries"]) == 1 and budget["entries"][0]["status"] == "Error"
            and budget["entries"][0]["classification"] is None
            and all(budget["entries"][0][key]["status"] == "Error" for key in ("middleLeft", "middleRight", "leftRight")), "content budget all pairs error")

    workspace = work / "workspace"
    for filename in ("saved.json", "reopened.json"):
        saved = read(workspace / filename)
        require("entries" not in saved, "legacy single comparison root roundtrip")
        entry = saved
        require(entry["mode"] == "Folder" and entry["baseReadOnly"] is True
                and entry["recursive"] is False and entry["folderShowFiltered"] is True, "workspace settings roundtrip")
        for key, side in (("leftPath", "left"), ("basePath", "middle"), ("rightPath", "right")):
            require(pathlib.Path(entry[key]).is_absolute(), "workspace relative input resolved")
            require(pathlib.Path(entry[key]).resolve() == (workspace / side).resolve(), "workspace side mapping")
    input_expected = {"leftPath": "left", "basePath": "middle", "rightPath": "right", "mode": "Folder", "baseReadOnly": True,
                      "recursive": False, "folderMode": "Content", "folderShowFiltered": True}
    require((workspace / "input.json").read_bytes() == json.dumps(input_expected, indent=2).replace("\n", os.linesep).encode("utf-8"), "all source project bytes retained")
    require((workspace / "saved.json").read_bytes() == (workspace / "reopened.json").read_bytes(), "stable project roundtrip bytes")

    gui = read(work / "gui-process.json")
    require(gui["ExitCode"] == 0, "GUI actual exit")
    report = read(work / "gui" / "ui-report.json")
    if os.name == "nt":
        environment_files = list((work / "gui").rglob("folder-threeway-physical-alias-environment.json"))
        require(len(environment_files) == 1, "GUI exact local share environment producer")
        environment = read(environment_files[0])
        third = str(environment_files[0].parent / "threeway" / "third-root-localhost-share" / "right" / "third")
        require(environment["canonicalRoot"] == third and environment["middleInput"] == "\\\\localhost\\" + third[0] + "$" + third[2:], "GUI exact existing local share candidate")
        require(environment["eligible"] == os.path.isdir(environment["middleInput"]), "GUI observed share availability")
        if environment["eligible"]:
            require(os.path.samefile(third, environment["middleInput"]), "GUI share actual directory identity")
            gui_protection_names.append("third-root-localhost-share")
    expected_gui_assertions = 79 + 4 * len(gui_protection_names) + (4 if os.name == "nt" else 0)
    require(report["scope"] == "folder-threeway-only" and len(report["assertions"]) == expected_gui_assertions
            and all(row["passed"] for row in report["assertions"]), "GUI exact platform scope and final pass")
    readonly_files = list((work / "gui").rglob("folder-threeway-long-readonly.json"))
    require(len(readonly_files) == (1 if os.name == "nt" else 0), "GUI exact long readonly producer")
    if readonly_files:
        readonly = read(readonly_files[0])
        readonly_work = readonly_files[0].parent / "threeway" / "readonly-other-tab-long-device"
        while len(str(readonly_work / "right")) < 270:
            readonly_work /= "long-path-component-0123456789"
        roots = [str(readonly_work / side) for side in ("left", "middle", "right")]
        raw = "\\\\.\\" + roots[2] + "\\\\"
        require(readonly["roots"] == roots and len(roots[2]) >= 270
                and all(len(part) <= 80 for part in pathlib.Path(roots[2]).parts), "GUI canonical long readonly root")
        require(readonly["rawReadOnlyRoot"] == readonly["capturedPath"] == raw
                and readonly["capturedReadOnly"] is True, "GUI original DOS readonly input retained")
        expected = ["left\n", "middle\n", "right\n"]
        require(readonly["before"] == readonly["after"] == expected
                and readonly["confirms"] == 1 and readonly["published"] == 0, "GUI readonly refuses after confirmation and before publication")
        for index, root in enumerate(roots):
            require((pathlib.Path(root) / "entry.txt").read_bytes() == expected[index].encode("utf-8"), "GUI readonly all physical bytes retained")
    observations = list((work / "gui").rglob("folder-threeway-observations.json"))
    require(len(observations) == 1, "GUI exact observation producer")
    rows = read(observations[0])
    directions = {"LeftToRight": (0, 2), "RightToLeft": (2, 0), "LeftToMiddle": (0, 1), "MiddleToLeft": (1, 0), "MiddleToRight": (1, 2), "RightToMiddle": (2, 1)}
    names = {direction + "-" + mode for direction in directions for mode in ("All", "DifferencesOnly")}
    names |= {"cancel", "middle-readonly", "middle-readonly-source", "middle-change-during-confirm"}
    require(len(rows) == 16 and {row["name"] for row in rows} == names, "GUI exact 16 cases")
    for row in rows:
        source, target = directions[row["direction"]]
        expected = [b"left\n", b"middle\n", b"right\n"]
        published = row["name"] not in {"cancel", "middle-readonly", "middle-change-during-confirm"}
        if published:
            expected[target] = expected[source]
        require(row["published"] == row["expectedPublished"] == int(published), "GUI publication")
        require(row["before"] == ["left\n", "middle\n", "right\n"], "GUI fixed input")
        require(row["after"] == [body.decode("utf-8") for body in expected], "GUI expected output")
        for side, root in enumerate(row["roots"]):
            require((pathlib.Path(root) / "entry.txt").read_bytes() == expected[side], "GUI independent actual bytes")
    protection_files = list((work / "gui").rglob("folder-threeway-protection-observations.json"))
    require(len(protection_files) == 1, "GUI exact protection observation producer")
    protection_rows = read(protection_files[0])
    require(len(protection_rows) == len(gui_protection_names) and {row["name"] for row in protection_rows} == set(gui_protection_names),
            "GUI exact platform protection cases separate from 16 copies")
    for row in protection_rows:
        expected_text = ["new\n", "protected\n", "right\n"]
        require(row["confirms"] == 0 and row["published"] == 0, row["name"] + " no confirmation/publication")
        require(row["before"] == row["after"] == expected_text, row["name"] + " fixed before/after bytes")
        require(len(row["roots"]) == 3 and row["roots"][1] == str(pathlib.Path(row["roots"][2]) / "third"), "GUI canonical nested third root")
        third = row["roots"][1]
        if row["name"] == "third-root-long-device-double-trailing":
            require(len(third) >= 270 and all(len(part) <= 80 for part in pathlib.Path(third).parts), "GUI long canonical root/components")
        prefix = {"third-root-extended-double-trailing": "\\\\?\\", "third-root-device-double-trailing": "\\\\.\\", "third-root-long-device-double-trailing": "\\\\.\\"}.get(row["name"])
        raw_expected = "\\\\localhost\\" + third[0] + "$" + third[2:] if row["name"] == "third-root-localhost-share" else prefix + third + os.sep * 2 if prefix else third + os.sep
        require(row["middleInput"] == raw_expected, "GUI raw namespace and exact trailing separators")
        relatives = ["third/entry.txt", "entry.txt", "entry.txt"]
        for root, relative, body in zip(row["roots"], relatives, expected_text):
            require((pathlib.Path(root) / relative).read_bytes() == body.encode("utf-8"), "GUI protection independent actual bytes")
    pngs = [png(work / "gui" / ("folder-threeway-" + name + ".png"), size)
            for name, size in (("normal", (1280, 850)), ("minimum", (850, 550)))]
    return {"accepted": True, "originalCases": 63, "originalComparisons": comparisons, "copyPublications": copied,
            "cliCases": len(records), "cliProtectionCases": len(protection_names), "cliLongAliasCases": len(long_names), "guiCases": len(rows),
            "guiProtectionCases": len(protection_rows), "guiAssertions": len(report["assertions"]), "pngs": pngs,
            "scope": "Default GNU UTF-8 fixed leaf originals and managed/AOT execution supplied by caller; no full-input or desktop qualification."}


if __name__ == "__main__":
    fixture, work = (pathlib.Path(arg).resolve() for arg in sys.argv[1:])
    result = verify(fixture, work)
    (work / "independent-result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False))
