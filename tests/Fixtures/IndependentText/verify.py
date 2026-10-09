"""固定literalで独立三者TextのCLI/GUI成果物を検証する別プロセスreader。"""
import argparse
import hashlib
import json
import math
import struct
import sys
import zipfile
import zlib
from datetime import datetime, timedelta
from html.parser import HTMLParser
from pathlib import Path, PurePosixPath

ROLES = ("left", "middle", "right")
PATH_KEYS = ("leftPath", "basePath", "rightPath")
TEXTS = ("共通\r\nleft <&>\r\nlast", "共通\nmiddle <&>\nlast\n", "共通\nright <&>\nlast")
GUI_TEXTS = tuple("head\r\n" + role + "\r\ntail\r\n" for role in ROLES)
PAIRS = (("left-middle", "LeftMiddle", 0, 1), ("middle-right", "MiddleRight", 1, 2), ("left-right", "LeftRight", 0, 2))
CHECKS = []


def check(name, condition):
    if not condition:
        raise ValueError(name)
    CHECKS.append(name)


def object_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key: " + key)
        result[key] = value
    return result


def loads(raw):
    return json.loads(raw.decode("utf-8-sig") if isinstance(raw, bytes) else raw,
                      object_pairs_hook=object_pairs,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))


def read_json(path):
    return loads(path.read_bytes())


def single(paths, description):
    paths = list(paths)
    if len(paths) != 1:
        raise ValueError(f"{description}: expected exactly one artifact, got {len(paths)}")
    return paths[0]


def raw_receipt(run, label, suffix):
    path = single(run.glob("*-" + label + suffix), label + suffix)
    index, separator, name = path.name.partition("-")
    check("exact numeric raw filename " + label + suffix,
          bool(index) and index.isascii() and index.isdecimal() and separator == "-" and name == label + suffix)
    return path


def independent_raw_receipts(run, commands):
    # 他scopeは宣言済みの6命令とfixtureだけに限定し、未知input-*を除外しない。
    siblings = {
        "independent-text-input-selection-gui": "independent-text-input-selection",
        "independent-text-input-selection-archives-gui": "independent-text-input-selection-archives",
        "independent-text-input-selection-cipher-gui": "independent-text-input-selection-cipher",
        "independent-text-input-selection-routes-gui": "independent-text-input-selection-routes",
        "independent-text-input-saved-archives-gui": "independent-text-input-saved-archives",
        "independent-text-input-lifetime-gui": "independent-text-input-lifetime",
    }
    expected = set(commands)
    for label, scope in siblings.items():
        fixtures = list(run.glob("fixtures/*/" + scope))
        check("single sibling fixture scope " + label, len(fixtures) <= 1)
        if fixtures:
            expected.add(label)
    for suffix in (".stdout.txt", ".stderr.txt"):
        files = list(run.glob("*-independent-text-*" + suffix))
        check("all exact command raw receipts" + suffix, len(files) == len(expected)
              and {p.name.split("-", 1)[1].removesuffix(suffix) for p in files} == expected)
    for label in sorted(expected):
        stdout = raw_receipt(run, label, ".stdout.txt")
        stderr = raw_receipt(run, label, ".stderr.txt")
        check("paired raw filename index " + label,
              stderr.name == stdout.name.removesuffix(".stdout.txt") + ".stderr.txt")
    return [raw_receipt(run, label, ".stdout.txt") for label in sorted(commands)]


def encoded(text, side, gui=False):
    # 製品が返すencoding名から期待bytesを作らない。
    if side == 1:
        return b"\xff\xfe" + text.encode("utf-16-le")
    bom = (side == 2) if gui else (side == 0)
    return (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")


def bytes_equal(path, expected):
    check("bytes/BOM/EOL: " + path.name, path.read_bytes() == expected)


class Snapshots(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.role = None
        self.in_pre = False
        self.values = {}

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "section" and "data-input-role" in attrs:
            role = attrs["data-input-role"]
            if self.role is not None or role not in ROLES or role in self.values:
                raise ValueError("unexpected/duplicate HTML role")
            self.role = role
        if tag == "pre" and attrs.get("class") == "input-snapshot":
            if self.role is None or self.role in self.values or self.in_pre:
                raise ValueError("unexpected HTML snapshot")
            self.values[self.role] = ""
            self.in_pre = True
        elif self.in_pre:
            raise ValueError("unescaped markup inside original text")

    def handle_endtag(self, tag):
        if tag == "pre":
            self.in_pre = False
        if tag == "section":
            self.role = None

    def handle_data(self, data):
        if self.in_pre:
            self.values[self.role] += data


def html_check(raw, expected, name):
    parser = Snapshots()
    parser.feed(raw.decode("utf-8-sig"))
    parser.close()
    check("HTML escaped full originals: " + name, parser.values == dict(zip(ROLES, expected)))


def project_check(value, paths, untitled=False):
    check("v6 single active entry", value["formatVersion"] == 6 and value["activeEntryIndex"] == 0 and len(value["entries"]) == 1)
    entry = value["entries"][0]
    expected = {"semantics": "Independent", **{r: {"kind": "Untitled" if untitled and r == "middle" else "Physical"} for r in ROLES}}
    check("typed semantics/kinds/pair", entry["textInputs"] == expected and entry["textComparisonPair"] == "MiddleRight" and entry["mode"] == "Text")
    check("three exact input paths", tuple(entry[k] for k in PATH_KEYS) == tuple(str(p) for p in paths))
    check("three readonly flags", tuple(entry[k] for k in ("leftReadOnly", "baseReadOnly", "rightReadOnly")) == (False, True, False))
    check("no archive payload", all(entry[k] is None for k in ("leftArchiveInput", "baseArchiveInput", "rightArchiveInput")))
    return entry


def verify(run, products_only=False, legacy88=False):
    work = single(run.glob("fixtures/*/independent-text"), "CLI fixture root")
    paths = tuple(work / (r + ".txt") for r in ROLES)
    before = read_json(work / "inputs-before.json")
    check("original SHA keys", set(before) == set(map(str, paths)))
    for side, path in enumerate(paths):
        expected = encoded(TEXTS[side], side)
        bytes_equal(path, expected)
        check("original independent SHA: " + path.name, before[str(path)].lower() == hashlib.sha256(expected).hexdigest())
    for option, canonical, first, second in PAIRS:
        for equal in (False, True):
            command = "independent-text-" + ("equal-" if equal else "compare-") + option
            value = read_json(raw_receipt(run, command, ".stdout.txt"))
            expected_texts = (TEXTS[0], TEXTS[0]) if equal else (TEXTS[first], TEXTS[second])
            check(command + " pair/full text", value["textSemantics"] == "Independent" and value["comparisonPair"] == canonical and
                  (value["firstText"], value["secondText"]) == expected_texts)
            check(command + " selected roles", (value["firstRole"], value["secondRole"]) == (ROLES[first], ROLES[second]))
            check(command + " different", value["different"] is (not equal))
            check(command + " exactly three inputs", len(value["inputs"]) == 3)
            for side, descriptor in enumerate(value["inputs"]):
                path = paths[0 if equal else side]
                check(command + " descriptor " + ROLES[side], descriptor == {"role": ROLES[side], "kind": "Physical", "path": str(path), "description": str(path), "exists": True, "encoding": "utf-16" if side == 1 and not equal else "utf-8"})
    copied = read_json(work / "physical-copy.json")
    project_check(copied, paths)
    untitled_paths = (paths[0], "", paths[2])
    project_check(read_json(work / "untitled-copy.json"), untitled_paths, True)
    html_check((work / "physical.html").read_bytes(), TEXTS, "physical")
    empty_texts = (TEXTS[0], "", TEXTS[2])
    html_check((work / "untitled.html").read_bytes(), empty_texts, "untitled")
    extracted = work / "untitled-extracted"
    expected_names = {"project.json", "1/left.txt", "3/right.txt", "report.html", "report.files/1.html"}
    zip_evidence = []
    contents = {}
    with zipfile.ZipFile(work / "untitled.zip") as archive:
        infos = archive.infolist()
        check("ZIP exact entries/no duplicate", len(infos) == len(expected_names) and {i.filename for i in infos} == expected_names)
        for info in infos:
            parts = PurePosixPath(info.filename).parts
            check("ZIP safe regular unencrypted entry " + info.filename, not info.is_dir() and not info.flag_bits & 1 and
                  not info.filename.startswith("/") and "\\" not in info.filename and all(p not in ("", ".", "..") and ":" not in p for p in parts) and
                  info.file_size <= 4 * 1024 * 1024 and (info.external_attr >> 16) & 0o170000 != 0o120000)
            crc, size, sha, blocks = 0, 0, hashlib.sha256(), []
            with archive.open(info) as stream:
                while block := stream.read(65536):
                    crc = zlib.crc32(block, crc)
                    size += len(block)
                    sha.update(block)
                    blocks.append(block)
            check("ZIP streamed CRC/size " + info.filename, size == info.file_size and crc & 0xffffffff == info.CRC)
            raw = b"".join(blocks)
            contents[info.filename] = raw
            check("ZIP/extracted all bytes/SHA " + info.filename, (extracted / info.filename).read_bytes() == raw and hashlib.sha256((extracted / info.filename).read_bytes()).hexdigest() == sha.hexdigest())
            zip_evidence.append({"name": info.filename, "size": size, "crc32": f"{crc & 0xffffffff:08x}", "sha256": sha.hexdigest()})
    check("extracted exact file set", {p.relative_to(extracted).as_posix() for p in extracted.rglob("*") if p.is_file()} == expected_names)
    check("ZIP left fixed snapshot", contents["1/left.txt"] == encoded(TEXTS[0], 0))
    check("ZIP right fixed snapshot", contents["3/right.txt"] == encoded(TEXTS[2], 2))
    package_entry = project_check(loads(contents["project.json"]), ("1/left.txt", "", "3/right.txt"), True)
    reopened = project_check(read_json(work / "untitled-reopened.json"), (extracted / "1/left.txt", "", extracted / "3/right.txt"), True)
    check("reopened full settings match packaged entry", {k: v for k, v in package_entry.items() if k not in PATH_KEYS} == {k: v for k, v in reopened.items() if k not in PATH_KEYS})
    check("ZIP report index exact", contents["report.html"].decode("utf-8") == '<!doctype html><meta charset="utf-8"><title>比較レポート</title><h1>比較レポート</h1><ol><li><a href="report.files/1.html">left.txt</a></li></ol>')
    html_check(contents["report.files/1.html"], empty_texts, "packaged")
    rejection_ids = ["old-version-" + str(v) for v in range(1, 6)] + ["unknown-semantics", "unknown-kind", "absent-middle", "archive-middle", "null-middle", "missing-middle", "extra-side-field", "physical-empty-path", "untitled-has-path", "non-text-mode", "unknown-selected-pair", "duplicate-side-kind"]
    if not legacy88:
        rejection_ids.append("fixed-ancestor-untitled")
    source_literal = {"formatVersion": 6, "activeEntryIndex": 0, "entries": [{**dict(zip(PATH_KEYS, map(str, paths))), "mode": "Text", "baseReadOnly": True, "textComparisonPair": "MiddleRight", "textInputs": {"semantics": "Independent", **{r: {"kind": "Physical"} for r in ROLES}}}]}
    check("physical project input unchanged", read_json(work / "physical.json") == source_literal)
    for rejection in rejection_ids:
        expected = loads(json.dumps(source_literal))
        entry = expected["entries"][0]
        descriptor = entry["textInputs"]
        if rejection.startswith("old-version-"):
            expected["formatVersion"] = int(rejection[-1])
        elif rejection == "unknown-semantics":
            descriptor["semantics"] = "Other"
        elif rejection in ("unknown-kind", "absent-middle", "archive-middle", "untitled-has-path"):
            descriptor["middle"]["kind"] = {"unknown-kind": "Other", "absent-middle": "Absent", "archive-middle": "Archive", "untitled-has-path": "Untitled"}[rejection]
        elif rejection == "null-middle":
            descriptor["middle"] = None
        elif rejection == "missing-middle":
            del descriptor["middle"]
        elif rejection == "extra-side-field":
            descriptor["middle"]["extra"] = 1
        elif rejection == "physical-empty-path":
            entry["basePath"] = ""
        elif rejection == "non-text-mode":
            entry["mode"] = "Folder"
        elif rejection == "unknown-selected-pair":
            entry["textComparisonPair"] = "Other"
        elif rejection == "fixed-ancestor-untitled":
            descriptor["semantics"] = "FixedAncestor"
            descriptor["middle"]["kind"] = "Untitled"
            entry["basePath"] = ""
            entry["textComparisonPair"] = None
        raw_input = (work / ("reject-" + rejection + ".json")).read_bytes()
        if rejection == "duplicate-side-kind":
            duplicate = b'"kind":"Physical","kind":"Physical"'
            check("duplicate kind raw input retained", raw_input.count(duplicate) == 3)
            raw_input = raw_input.replace(duplicate, b'"kind":"Physical"')
        check("rejected input unchanged " + rejection, loads(raw_input) == expected)
    check("exact rejection sentinel set", {p.name for p in work.glob("reject-*-output.json")} == {"reject-" + name + "-output.json" for name in rejection_ids})
    for path in work.glob("reject-*-output.json"):
        bytes_equal(path, b"KEEP")
    commands = {"independent-text-" + kind + "-" + p[0] for p in PAIRS for kind in ("compare", "equal")}
    commands.update("independent-text-reject-" + name for name in ("missing-middle", "unknown-pair", "pair-without-independent"))
    commands.update("independent-text-project-reject-" + name for name in rejection_ids)
    commands.update("independent-text-" + name for name in ("project-copy", "project-report", "untitled-copy", "untitled-report", "untitled-package", "untitled-extract", "untitled-reopen", "gui"))
    if not legacy88:
        commands.add("independent-text-json-budget")
        for kind, extension in (("report", "html"), ("package", "zip")):
            commands.add("independent-text-fixed-ancestor-" + kind)
            bytes_equal(work / ("fixed-ancestor-output." + extension), b"KEEP")
        # 製品のSHAや宣言sizeを期待値にせず、固定literalで9 MiBを構成する。
        oversized_expected = "制".encode("utf-8") * (3 * 1024 * 1024)
        oversized = work / "oversized-cli.txt"
        bytes_equal(oversized, oversized_expected)
        check("oversized UTF8 no-BOM full SHA", hashlib.sha256(oversized.read_bytes()).digest() == hashlib.sha256(oversized_expected).digest())
    files = independent_raw_receipts(run, commands)
    for path in files:
        stderr = path.with_name(path.name.replace(".stdout.", ".stderr."))
        if "-reject-" in path.name or path.name.endswith("-json-budget.stdout.txt") or "-independent-text-fixed-ancestor-" in path.name:
            bytes_equal(path, b"")
            check("reject diagnostic: " + path.name, stderr.stat().st_size > 0)
            if path.name.endswith("-json-budget.stdout.txt"):
                check("32 MiB refusal diagnostic", "32 MiB" in stderr.read_bytes().decode("utf-8-sig"))
        else:
            bytes_equal(stderr, b"")
            if path.name.endswith("-gui.stdout.txt"):
                check("GUI completed raw process output", "HeadlessIndependentTextChecks complete" in path.read_text(encoding="utf-8-sig"))
            else:
                check("successful command JSON object: " + path.name, isinstance(read_json(path), dict))
    process_evidence = [] if legacy88 else verify_commands(run, work, commands, rejection_ids, paths)
    gui_result = verify_gui(work / "gui", legacy88)
    if not products_only:
        driver = read_json(run / "assertions.json")
        verify_driver(run, driver, commands, process_evidence, legacy88)
    return {"run": str(run), "productsOnly": products_only, "legacy88": legacy88, "checks": len(CHECKS), "checkNames": CHECKS, "zipEntries": zip_evidence, "processes": process_evidence, **gui_result}


def verify_driver(run, driver, commands, process_evidence, legacy88):
    assertions = driver["assertions"]
    check("driver assertion rows", isinstance(assertions, list) and all(isinstance(row, dict)
          and set(row) == {"Name", "Status", "Detail"} and all(isinstance(row[key], str) for key in row) for row in assertions))
    statuses = ("passed", "failed", "skipped")
    check("driver known assertion statuses", all(row["Status"] in statuses for row in assertions))
    for status in statuses:
        check("driver exact aggregate " + status, type(driver[status]) is int and driver[status] >= 0
              and driver[status] == sum(row["Status"] == status for row in assertions))
    check("driver receipts passed", driver["failed"] == 0 and driver["passed"] >= 80)
    if legacy88:
        # 旧34命令は明示scope。未採取のcommands.json/PIDを新scopeの成功と読み替えない。
        exits = {label: 1 if label.startswith("independent-text-compare-") else 2 if "-reject-" in label else 0 for label in commands}
    else:
        exits = {row["label"]: row["actualExit"] for row in process_evidence}
        check("driver verified process scope", len(process_evidence) == len(commands) + 2
              and set(exits) == commands | {"legacy-text-flags-in-option-payload", "legacy-table-flags-in-option-payload"})
    def exit_assertion(label, code):
        assertion = single((row for row in assertions if row["Name"] == label + " exit"), label + " driver exit")
        stderr = raw_receipt(run, label, ".stderr.txt")
        stdout = raw_receipt(run, label, ".stdout.txt")
        check("driver paired raw filename index " + label, stderr.name == stdout.name.removesuffix(".stdout.txt") + ".stderr.txt")
        check("driver exact passed exit " + label, assertion["Status"] == "passed"
              and assertion["Detail"] == f"expected={code}, actual={code}; " + stderr.read_bytes().decode("utf-8-sig"))
    for label, code in sorted(exits.items()):
        exit_assertion(label, code)
    # 完全文言は各正式suiteのSkip/Assertion宣言から固定。可変exception/capabilityは許容しない。
    known_skips = {
        ("archive Unix 0600 preservation", "Windows does not expose UnixFileMode."): ("archive-protected-source.7z", "archive-repack-input-output-same", 2),
        ("archive Mac case alias preservation", "The current host is not macOS; actual APFS behavior requires the macOS CI runner."): ("archive-protected-source.7z", "archive-repack-input-output-same", 2),
        ("archive Mac case alias preservation", "The alternate casing does not resolve to the existing file on this case-sensitive filesystem."): ("archive-protected-source.7z", "archive-repack-input-output-same", 2),
        ("packaging case collision", "ファイルシステムが二つの入力名を同じファイルへ解決します。"): ("packaging/collision-case", "packaging-single-all-options", 0),
        ("packaging nfc collision", "ファイルシステムが二つの入力名を同じファイルへ解決します。"): ("packaging/collision-nfc", "packaging-single-all-options", 0),
        ("Folder copy Mac FIFO/socket actual app", "Mac lstat/非regularの実OS拒否とCSDK ABIはMac RID実検証工程。"): ("folder-copy/gui-process.json", "folder-copy-gui", 0),
        ("Folder copy Windows compressed sparse actual app", "NTFS圧縮/Sparse mainstream bytesはWindows実測工程。ADS/EFS完全保持ではありません。"): ("folder-copy/gui-process.json", "folder-copy-gui", 0),
        ("Folder Windows metadata rejection GUI", "Windows NTFSでの実GUI・Win32 handle照合専用。"): ("folder-copy/gui-process.json", "folder-copy-gui", 0),
        ("Folder Windows full streams", "Windows file stream backend専用。macOSのdefault-only経路はwholeで検証。"): ("folder-copy/gui-process.json", "folder-copy-gui", 0),
        ("Folder Windows metadata actual main", "Windows local NTFS専用。"): ("folder-copy/gui-process.json", "folder-copy-gui", 0),
        ("Folder copy Mac case alias", "Case-sensitive filesystem: Directory.Exists(LEFT) is false"): ("folder-copy/gui-process.json", "folder-copy-gui", 0),
        ("Folder review Mac nested alias", "Case-sensitive filesystem: nested alias unavailable"): ("folder-copy/gui-process.json", "folder-copy-gui", 0),
        ("Folder threeway actual desktop and all input compatibility", "既定GNU UTF-8原本63とheadless GUIの限定契約。実OS pointer/dialog、任意plugins/encoding/binaryは範囲外。"): ("folder-threeway/gui-process.json", "folder-threeway-gui", 0),
        ("Folder threeway existing localhost share alias", "既存のローカル管理共有からfixtureを読めないため、UNC別名の実測を省略します。共有やOS設定は変更しません。"): ("folder-threeway/physical-alias-environment.json", "folder-threeway-gui", 0),
        ("Folder long existing localhost DOS UNC alias", "既存管理共有を読めないため長いDOS UNC別名だけ省略します。共有やOS設定は変更しません。"): ("folder-threeway/long-alias-environment.json", "folder-threeway-gui", 0),
    }
    for row in assertions:
        if row["Status"] != "skipped":
            continue
        check("driver target scope never skipped", not row["Name"].startswith("independent-text-")
              and row["Name"] not in {label + " exit" for label in exits})
        key = (row["Name"], row["Detail"])
        check("driver declared outside-scope skip " + row["Name"], key in known_skips)
        fixture, label, code = known_skips[key]
        artifact = single(run.glob("fixtures/*/" + fixture), row["Name"] + " declared suite fixture")
        check("driver skip suite fixture exists " + row["Name"], artifact.is_file() or artifact.is_dir())
        exit_assertion(label, code)


def verify_commands(run, work, commands, rejection_ids, paths):
    expected = {}

    def command(label, code, json_output, arguments):
        expected["independent-text-" + label] = (code, json_output, [str(a) for a in arguments])

    for option, canonical, first, second in PAIRS:
        command("compare-" + option, 1, True, ["--compare", paths[0], paths[2], "--middle", paths[1], "--independent-text", "--pair", option])
        command("equal-" + option, 0, True, ["--compare", paths[0], paths[0], "--middle", paths[0], "--independent-text", "--pair", option])
    command("reject-missing-middle", 2, False, ["--compare", paths[0], paths[2], "--middle", work / "missing.txt", "--independent-text"])
    command("reject-unknown-pair", 2, False, ["--compare", paths[0], paths[2], "--middle", paths[1], "--independent-text", "--pair", "other"])
    command("reject-pair-without-independent", 2, False, ["--compare", paths[0], paths[2], "--pair", "left-middle"])
    for label, arguments in (
        ("project-copy", ["--project-copy", work / "physical.json", work / "physical-copy.json"]),
        ("project-report", ["--report-project", work / "physical.json", work / "physical.html"]),
        ("untitled-copy", ["--project-copy", work / "untitled.json", work / "untitled-copy.json"]),
        ("untitled-report", ["--report-project", work / "untitled.json", work / "untitled.html"]),
        ("untitled-package", ["--package-project", work / "untitled.json", work / "untitled.zip", "--report"]),
        ("untitled-extract", ["--archive-extract", work / "untitled.zip", work / "untitled-extracted"]),
        ("untitled-reopen", ["--project-copy", work / "untitled-extracted/project.json", work / "untitled-reopened.json"]),
    ):
        command(label, 0, True, arguments)
    for rejection in rejection_ids:
        command("project-reject-" + rejection, 2, False, ["--project-copy", work / ("reject-" + rejection + ".json"), work / ("reject-" + rejection + "-output.json")])
    oversized = work / "oversized-cli.txt"
    command("json-budget", 2, False, ["--compare", oversized, oversized, "--middle", oversized, "--independent-text", "--max-work", "0"])
    for kind, option, extension in (("report", "--report-project", "html"), ("package", "--package-project", "zip")):
        command("fixed-ancestor-" + kind, 2, False, [option, work / "reject-fixed-ancestor-untitled.json", work / ("fixed-ancestor-output." + extension)])
    command("gui", 0, False, ["--self-test-independent-text", work / "gui"])
    check("literal command scope", set(expected) == commands)
    records = read_json(work / "commands.json")
    check("exact command process receipts", isinstance(records, list) and len(records) == len(commands) and {r["label"] for r in records} == commands)
    legacy_input = work / "legacy-payload-input.txt"
    bytes_equal(legacy_input, b"tag --pair --independent-text\n")
    check("legacy compatibility uses supported Compare/Table only", not (work / "legacy-payload-merged.txt").exists())
    legacy_records = read_json(work / "legacy-payload-proof.json")
    legacy_labels = {"legacy-text-flags-in-option-payload", "legacy-table-flags-in-option-payload"}
    check("exact two legacy payload process receipts", isinstance(legacy_records, list) and len(legacy_records) == 2 and {r["Name"] for r in legacy_records} == legacy_labels)
    expected["legacy-text-flags-in-option-payload"] = (0, True, ["--compare", str(legacy_input), str(legacy_input), "--substitute", "--pair", "--independent-text"])
    expected["legacy-table-flags-in-option-payload"] = (0, True, ["--table", str(legacy_input), str(legacy_input), "--substitute", "--pair", "--independent-text"])
    for result in legacy_records:
        label = result["Name"]
        arguments = expected[label][2]
        parsed = loads(result["Stdout"])
        if label.startswith("legacy-text-"):
            check("legacy compare payload remains ordinary equal Text", parsed["different"] is False and "textSemantics" not in parsed)
        else:
            check("legacy Table payload fixed unchanged alignment", parsed == {"different": False, "rows": 1, "cols": 1, "alignedRows": 1, "alignmentFallback": False, "alignmentWorkUsed": 0, "alignmentFallbackReason": None, "mapping": [{"left": 1, "right": 1}]})
        check("legacy payload stderr empty", result["Stderr"] == "")
        records.append({"label": label, "expectedExit": 0, "json": True, "arguments": arguments, "result": result})
    seen, evidence = set(), []
    for record in records:
        label = record["label"]
        code, json_output, arguments = expected[label]
        check("command record exact fields " + label, set(record) == {"label", "expectedExit", "json", "arguments", "result"})
        check("literal command contract " + label, record["expectedExit"] == code and record["json"] is json_output and record["arguments"] == arguments)
        result = record["result"]
        check("process result exact fields " + label, set(result) == {"Name", "Arguments", "ExitCode", "Stdout", "Stderr", "DurationMilliseconds", "Pid", "CreationUtc", "LaunchUtc", "ExitObservedUtc", "LaunchEvidence"})
        check("actual command/exit " + label, result["Name"] == label and result["Arguments"] == arguments and result["ExitCode"] == code and type(result["Pid"]) is int and result["Pid"] > 0 and type(result["DurationMilliseconds"]) is int and result["DurationMilliseconds"] >= 0)
        times = []
        for key in ("LaunchUtc", "CreationUtc", "ExitObservedUtc"):
            value = result[key]
            check("actual UTC timestamp " + label + " " + key, isinstance(value, str) and value.endswith("Z"))
            times.append(datetime.fromisoformat(value.replace("Z", "+00:00")))
        launch, creation, exit_time = times
        check("process lifetime order " + label, launch <= exit_time and creation <= exit_time and creation >= launch - timedelta(seconds=1))
        identity = (result["Pid"], result["CreationUtc"])
        check("unique actual process identity " + label, identity not in seen)
        seen.add(identity)
        stdout = raw_receipt(run, label, ".stdout.txt")
        stderr = raw_receipt(run, label, ".stderr.txt")
        check("raw complete streams match process receipt " + label, result["Stdout"] == stdout.read_bytes().decode("utf-8-sig") and result["Stderr"] == stderr.read_bytes().decode("utf-8-sig"))
        mac = result["LaunchEvidence"]
        if mac is not None:
            check("mac terminal process evidence " + label, mac["ActualPid"] == result["Pid"] and mac["RawExitCode"] == code and mac["Terminal"] is True and mac["StdoutComplete"] is True and mac["StderrComplete"] is True and mac["PipesReleased"] is True and mac["ProcessDisposed"] is True and mac["CleanupIssues"] == [])
        evidence.append({"label": label, "pid": result["Pid"], "creationUtc": result["CreationUtc"], "exitObservedUtc": result["ExitObservedUtc"], "actualExit": result["ExitCode"], "stdoutSha256": hashlib.sha256(stdout.read_bytes()).hexdigest(), "stderrSha256": hashlib.sha256(stderr.read_bytes()).hexdigest()})
    return evidence


def verify_gui(output, legacy88=False):
    gui = single(output.glob("fixtures/*/independent-text"), "GUI fixture root")
    for side, role in enumerate(ROLES):
        bytes_equal(gui / (role + ".txt"), encoded(GUI_TEXTS[side], side, True))
    if not legacy88:
        proof_names = {
            "FixedAncestor Untitled side 0 rejects before save and GUI adoption",
            "FixedAncestor Untitled side 1 rejects before save and GUI adoption",
            "FixedAncestor Untitled side 2 rejects before save and GUI adoption",
            "raw v6 FixedAncestor Untitled load preserves old tab source inputs and workspace",
            "actual pair selection invalid regex guarded with old diff and all texts",
            "corrected regex pair selection recovers without process failure",
            "recompare calculation gate runs off UI and remains responsive",
            "actual recompare Cancel preserves previous diff and all texts",
            "async recompare rejects later side 1 edit including unselected third",
            "async recompare rejects later side 2 edit including unselected third",
            "async recompare role change preserves adopted independent diff",
            "async recompare readonly change keeps previous diff and body",
            "async recompare options change rejects old candidate",
            "async recompare tab move keeps old diff and body",
            "later pair completes while older gated completion cannot overwrite",
            "async copy Cancel at adoption preserves destination revision dirty savepoint and old diff",
        }
        proofs = read_json(gui / "async-proof.json")
        check("exact fixed GUI async proof names", len(proofs) == len(proof_names) and {p["name"] for p in proofs} == proof_names)
        check("all GUI async proofs passed", all(set(p) == {"name", "passed", "detail"} and p["passed"] is True and isinstance(p["detail"], str) for p in proofs))
        for side in range(3):
            bytes_equal(gui / f"fixed-untitled-{side}-sentinel.json", b"KEEP-FIXED-UNTITLED")
        expected_invalid = {"formatVersion": 6, "activeEntryIndex": 0, "entries": [{"mode": "Text", "leftPath": str(gui / "left.txt"), "basePath": "", "rightPath": str(gui / "right.txt"), "textInputs": {"semantics": "FixedAncestor", "left": {"kind": "Physical"}, "middle": {"kind": "Untitled"}, "right": {"kind": "Physical"}}}]}
        check("raw v6 FixedAncestor input retained", read_json(gui / "raw-fixed-untitled.json") == expected_invalid)
    observations = read_json(gui / "observations.json")
    expected_observations = {}
    for option, canonical, first, second in PAIRS:
        for source, dest in ((first, second), (second, first)):
            name = f"copy-{source}-{dest}"
            texts = [GUI_TEXTS[source if s == dest else s] for s in range(3)]
            expected_observations[name] = (canonical, texts, [s == dest for s in range(3)], [gui / (r + ".txt") for r in ROLES])
            bytes_equal(gui / (name + ".txt"), encoded(GUI_TEXTS[source], dest, True))
    saved = GUI_TEXTS[1] + "saved-middle\r\n"
    left_dirty = GUI_TEXTS[0] + "unsaved-left\r\n"
    expected_observations["middle-saved"] = ("LeftMiddle", [left_dirty, saved, GUI_TEXTS[2]], [True, False, False], [gui / "left.txt", gui / "middle-save-input.txt", gui / "right.txt"])
    expected_observations["middle-late-edit"] = ("LeftMiddle", [left_dirty, saved + "newer-edit\r\n", GUI_TEXTS[2]], [True, True, False], [gui / "left.txt", gui / "middle-late.txt", gui / "right.txt"])
    expected_observations["stale-middle-preserved"] = ("LeftMiddle", [GUI_TEXTS[0], "middle edited during read\n", GUI_TEXTS[2]], [False] * 3, [gui / "left.txt", gui / "stale-middle-input.txt", gui / "right.txt"])
    expected_observations["restored-pristine"] = ("MiddleRight", [GUI_TEXTS[0], "", GUI_TEXTS[2]], [False] * 3, [gui / "left.txt", "", gui / "right.txt"])
    check("exact GUI observation names", len(observations) == len(expected_observations) and {o["name"] for o in observations} == set(expected_observations))
    for observation in observations:
        pair, texts, dirty, observed_paths = expected_observations[observation["name"]]
        check("GUI pair and all sides " + observation["name"], observation == {"name": observation["name"], "pair": pair, "sides": [{"side": s, "text": texts[s], "dirty": dirty[s], "path": str(observed_paths[s])} for s in range(3)]})
    for name, text, side in (("middle-save-input.txt", saved, 1), ("middle-late.txt", saved, 1), ("middle-history.txt", GUI_TEXTS[1] + "typed-middle", 1), ("readonly-middle-copy.txt", GUI_TEXTS[1], 1), ("untitled-middle.txt", "untitled-dirty\n", 0), ("stale-middle-input.txt", "middle edited during read\n", 1), ("published-not-adopted.txt", GUI_TEXTS[1] + "saved before rejection\r\n", 1)):
        bytes_equal(gui / name, encoded(text, side, True))
    layouts = []
    for name in ("normal", "minimum", "minimum-toolbar"):
        layout = read_json(gui / ("layout-" + name + ".json"))
        check("many tabs " + name, layout["tabCount"] >= 17)
        check("window dimensions " + name, (layout["windowWidth"], layout["windowHeight"]) == ((1280, 850) if name == "normal" else (850, 550)))
        for key in ("leftEditor", "middleEditor", "rightEditor"):
            bounds = layout[key]
            check("visible editor in window " + name + " " + key, bounds["visible"] and bounds["width"] > 50 and bounds["height"] > 50 and
                  all(math.isfinite(bounds[k]) for k in ("width", "height", "windowX", "windowY")) and bounds["windowX"] >= 0 and bounds["windowY"] >= 0 and
                  bounds["windowX"] + bounds["width"] <= layout["windowWidth"] and bounds["windowY"] + bounds["height"] <= layout["windowHeight"])
        if name == "minimum-toolbar":
            toolbar = layout["toolbar"]
            check("common settings can scroll", layout["toolbarExtentHeight"] > layout["toolbarViewportHeight"] and layout["toolbarOffsetY"] == 0)
            for key in ("middleSave", "leftPath", "middlePath", "rightPath"):
                b = layout[key]
                check("toolbar reachable " + key, b["visible"] and b["width"] > 0 and b["height"] > 0 and b["windowX"] >= toolbar["windowX"] and b["windowY"] >= toolbar["windowY"] and
                      b["windowX"] + b["width"] <= toolbar["windowX"] + toolbar["width"] and b["windowY"] + b["height"] <= toolbar["windowY"] + toolbar["height"])
        layouts.append({"name": name, "tabs": layout["tabCount"], "window": [layout["windowWidth"], layout["windowHeight"]]})
    pngs = []
    for name, layout_name in (("independent-text-three-panes.png", "normal"), ("independent-text-minimum.png", "minimum")):
        pngs.append(png_check(output / name, read_json(gui / ("layout-" + layout_name + ".json"))))
    # UI aggregateは原文や座標のoracleに使わず、Independent Text行だけの観測成否を別確認する。
    ui = read_json(output / "ui-report.json")
    relevant = [a for a in ui["assertions"] if a["name"].startswith("Independent Text ")]
    check("GUI Independent Text observations passed", ui["scope"] in ("independent-text-only", "all") and len(relevant) >= 50 and all(a["passed"] is True for a in relevant))
    return {"gui": str(output), "layouts": layouts, "pngs": pngs}


def png_check(path, layout):
    raw = path.read_bytes()
    check("PNG signature " + path.name, raw.startswith(b"\x89PNG\r\n\x1a\n"))
    offset, compressed, header, ended, idat_end = 8, bytearray(), None, False, False
    while offset < len(raw):
        check("PNG chunk header", offset + 12 <= len(raw))
        length = struct.unpack_from(">I", raw, offset)[0]
        check("PNG chunk length", length <= len(raw) - offset - 12)
        kind = raw[offset + 4:offset + 8]
        body = raw[offset + 8:offset + 8 + length]
        crc = struct.unpack_from(">I", raw, offset + 8 + length)[0]
        check("PNG chunk CRC " + kind.decode("ascii"), zlib.crc32(kind + body) & 0xffffffff == crc)
        if kind == b"IHDR":
            check("PNG first unique IHDR", header is None and offset == 8 and length == 13)
            header = struct.unpack(">IIBBBBB", body)
            check("PNG geometry equals JSON", header[:2] == (layout["windowWidth"], layout["windowHeight"]) and header[2] == 8 and header[3] in (2, 6) and header[4:] == (0, 0, 0))
        elif kind == b"IDAT":
            check("PNG consecutive IDAT", header is not None and not idat_end)
            compressed.extend(body)
        elif kind == b"IEND":
            check("PNG IEND length", length == 0 and bool(compressed))
            ended = True
        else:
            check("PNG known ancillary chunk", kind in (b"sRGB", b"gAMA", b"cHRM", b"pHYs", b"sBIT", b"tEXt", b"iTXt"))
            idat_end = bool(compressed)
        offset += length + 12
        if ended:
            break
    check("PNG complete end", ended and offset == len(raw) and header is not None)
    width, height = header[:2]
    channels = 4 if header[3] == 6 else 3
    stride = width * channels
    decoder = zlib.decompressobj()
    decoded = decoder.decompress(compressed, (stride + 1) * height + 1)
    check("PNG zlib complete/row length", decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail and len(decoded) == (stride + 1) * height)
    rows, previous = [], bytearray(stride)
    for y in range(height):
        start = y * (stride + 1)
        kind, row = decoded[start], bytearray(decoded[start + 1:start + 1 + stride])
        if kind > 4:
            raise ValueError("invalid PNG row filter")
        for x in range(stride):
            a = row[x - channels] if x >= channels else 0
            b = previous[x]
            c = previous[x - channels] if x >= channels else 0
            if kind == 1:
                prediction = a
            elif kind == 2:
                prediction = b
            elif kind == 3:
                prediction = (a + b) // 2
            elif kind == 4:
                p = a + b - c
                da, db, dc = abs(p - a), abs(p - b), abs(p - c)
                prediction = a if da <= db and da <= dc else b if db <= dc else c
            else:
                prediction = 0
            row[x] = (row[x] + prediction) & 255
        rows.append(row)
        previous = row
    for key in ("leftEditor", "middleEditor", "rightEditor"):
        bounds = layout[key]
        x0, y0 = math.ceil(bounds["windowX"]), math.ceil(bounds["windowY"])
        x1, y1 = math.floor(bounds["windowX"] + bounds["width"]), math.floor(bounds["windowY"] + bounds["height"])
        colours = {bytes(rows[y][x * channels:x * channels + channels]) for y in range(y0 + 2, y1 - 2) for x in range(x0 + 2, x1 - 2)}
        check("PNG decoded editor contains content " + path.name + " " + key, len(colours) > 2)
    return {"name": path.name, "dimensions": [width, height], "sha256": hashlib.sha256(raw).hexdigest(), "decodedBytes": stride * height}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    scope = parser.add_mutually_exclusive_group(required=True)
    scope.add_argument("--run", type=Path, help="E2E output directory containing independent-text fixtures")
    scope.add_argument("--gui", type=Path, help="GUI output directory containing fixtures and screenshots")
    parser.add_argument("--products-only", action="store_true", help="E2E集計前: outer assertions.jsonだけを検証対象から外す")
    parser.add_argument("--legacy88", action="store_true", help="旧limited88の34命令scopeを明示選択。新scopeの互換推測はしない")
    parser.add_argument("--receipt", type=Path, help="new JSON receipt path (existing files rejected)")
    args = parser.parse_args()
    try:
        if args.gui and (args.products_only or args.legacy88):
            raise ValueError("--products-only/--legacy88 require --run")
        if args.gui:
            receipt = {"passed": True, **verify_gui(args.gui.resolve()), "checks": len(CHECKS), "checkNames": CHECKS}
        else:
            receipt = {"passed": True, **verify(args.run.resolve(), args.products_only, args.legacy88)}
        raw = json.dumps(receipt, ensure_ascii=False, indent=2)
        if args.receipt:
            with args.receipt.open("x", encoding="utf-8", newline="\n") as stream:
                stream.write(raw + "\n")
        print(json.dumps({"passed": True, "checks": receipt["checks"], "run": receipt.get("run"), "gui": receipt["gui"]}, ensure_ascii=True))
        return 0
    except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile) as error:
        print(f"independent-text reader failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
