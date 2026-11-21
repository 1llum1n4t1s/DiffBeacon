"""Independent standard-library reader for the new GUI input-selection scope.

No imports from DiffBeacon or B588/D99 readers. No product-output expectation generation.
"""
import argparse
import base64
import hashlib
import io
import json
import math
import pathlib
import struct
import zlib
import zipfile

FIXED_RUN_MTIME_NS = 1704067200123456700


def require(condition, detail):
    if not condition:
        raise ValueError(detail)


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def json_read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def raw(side):
    return base64.b64decode(side["bytesBase64"], validate=True)


def reject_secrets(value):
    if isinstance(value, dict):
        for key, child in value.items():
            require(not any(token in key.lower() for token in ("password", "credential", "secret")), "Secret-bearing observation key: " + key)
            reject_secrets(child)
    elif isinstance(value, list):
        for child in value:
            reject_secrets(child)


def verify_fixture(fixture, repo, run=None):
    manifest = json_read(fixture / "manifest.json")
    require(manifest["schema"] == "input-selection-fixture-v1", "fixture schema")
    require(len(manifest["cases"]) == 15, "15 independent route/pair expectations required")
    roots = {}
    for pin in manifest["files"]:
        require(pathlib.PurePosixPath(pin["path"]).parts[0] == "inputs" and ".." not in pathlib.PurePosixPath(pin["path"]).parts, "fixture unsafe path")
        data = (fixture / pin["path"]).read_bytes()
        require(len(data) == pin["size"] and sha(data) == pin["sha256"], "fixture pin " + pin["path"])
        roots[pin["path"]] = data
        if run:
            run_source = run / pathlib.PurePosixPath(pin["path"]).name
            require(run_source.read_bytes() == data, "final run source preservation " + pin["path"])
            require(run_source.stat().st_mtime_ns == FIXED_RUN_MTIME_NS, "exact fixed ns after each run input reset " + pin["path"])
    for expected in manifest["zipEntries"]:
        data = roots[expected["path"]]
        for container in expected["chain"]:
            with zipfile.ZipFile(io.BytesIO(data)) as stream:
                require(stream.testzip() is None, "nested container CRC")
                data = stream.read(container)
        with zipfile.ZipFile(io.BytesIO(data)) as stream:
            require(stream.testzip() is None, "all ZIP entries CRC")
            infos = stream.infolist()
            require(len(infos) == len(expected["entries"]), "all ZIP entries count")
            for info, entry in zip(infos, expected["entries"]):
                payload = stream.read(info)
                require(info.filename == entry["name"] and len(payload) == entry["size"] and info.is_dir() == entry["directory"], "ZIP entry identity")
                require(f"{zlib.crc32(payload):08X}" == entry["crc32"] and sha(payload) == entry["sha256"], "ZIP entry CRC/SHA")
    for case in manifest["cases"]:
        for side in case["sides"]:
            payload = raw(side)
            if side["kind"] == "Physical":
                require(roots[side["source"]] == payload, "physical literal bytes")
            elif side["kind"] == "Archive":
                current = roots[side["source"]]
                for entry in [*side["chain"], side["leaf"]]:
                    with zipfile.ZipFile(io.BytesIO(current)) as stream:
                        require(stream.testzip() is None, "source chain full CRC")
                        current = stream.read(entry)
                require(current == payload, "archive leaf literal bytes")
            else:
                require(payload == b"" and side["source"] == "", "Untitled literal")
            decoded = payload.decode("utf-8-sig" if side["encoding"] == "utf-8" else "utf-16" if side["encoding"] == "utf-16-le" else "utf-8")
            require(decoded == side["text"], "literal encoding/BOM/newline exact text")
    reuse = manifest["reusedFixedScope"]
    source = repo / reuse["path"]
    require(sha((source / "fixed-sha256.json").read_bytes()) == reuse["manifestSha256"], "fixed B588 manifest unchanged")
    require(len(reuse["files"]) == 42, "all existing 42 pins")
    for pin in reuse["files"]:
        data = (source / pin["path"]).read_bytes()
        require(len(data) == pin["size"] and sha(data) == pin["sha256"], "fixed scope unchanged: " + pin["path"])
    return manifest, roots


def check_png(path, width, height):
    data = path.read_bytes()
    require(data[:8] == b"\x89PNG\r\n\x1a\n", "PNG signature")
    offset, idat, ended = 8, bytearray(), False
    dimensions = None
    while offset < len(data):
        require(offset + 12 <= len(data), "PNG chunk header")
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        require(offset + length + 12 <= len(data), "PNG chunk length")
        require(zlib.crc32(kind + payload) == struct.unpack_from(">I", data, offset + 8 + length)[0], "PNG full chunk CRC")
        if kind == b"IHDR":
            dimensions = struct.unpack(">IIBBBBB", payload)
        elif kind == b"IDAT":
            idat.extend(payload)
        elif kind == b"IEND":
            ended = True
            require(length == 0 and offset + 12 == len(data), "PNG terminal IEND")
        offset += length + 12
    require(ended and dimensions is not None, "complete PNG")
    w, h, depth, color, compression, filtering, interlace = dimensions
    require(w == math.ceil(width) and h == math.ceil(height), "PNG/bounds dimensions")
    require(depth == 8 and color in (2, 6) and compression == filtering == interlace == 0, "expected headless PNG encoding")
    raster = zlib.decompress(idat)
    channels = 3 if color == 2 else 4
    require(len(raster) == h * (1 + w * channels), "PNG independent scanline length")
    require(len(set(raster)) > 4, "headless PNG is not an empty solid image")


def verify_observations(manifest, roots, report_path, full_ui=False):
    report = json_read(report_path)
    require(report["scope"] == ("all" if full_ui else "independent-text-inputs-only"), "declared GUI execution scope")
    require(report["assertions"] and all(a["passed"] for a in report["assertions"]), "GUI final assertions required/all pass")
    run = pathlib.Path(report["fixtures"]) / "independent-text-input-selection"
    require(json_read(run / "manifest.json") == manifest, "copied run manifest")
    observation = json_read(run / "observations.json")
    reject_secrets(observation)
    require(observation["schema"] == "input-selection-observations-v1" and observation["status"] == "completed-minimum-set", "completed new observations")
    specs = {c["id"]: c for c in manifest["cases"]}
    expected_ids = {c + ":accept" for c in specs}
    physical_modes = ["double-submit", "cancel-before", "close-before", "cancel-gate", "close-gate", "abort-gate", "kind-change", "root-change", "readonly-change", "pair-change", "tab-away-return", "physical-sha-change", "tab-limit"]
    archive_modes = ["manifest-cancel", "manifest-root-change", "manifest-retry", "archive-sha-change"]
    expected_ids.update("physical-0:" + mode for mode in physical_modes)
    expected_ids.update("archives-0:" + mode for mode in archive_modes)
    records = observation["cases"]
    require(len(records) == len(expected_ids) == 32 and {r["id"] for r in records} == expected_ids, "all 32 minimum cases exactly once")
    for row in records:
        spec, mode = specs[row["fixtureCase"]], row["mode"]
        success = mode in ("accept", "double-submit", "manifest-retry")
        require(row["accepted"] is success and row["tabsAfter"] == row["tabsBefore"] + int(success), row["id"] + " adoption/count")
        require(row["sameParentIdentity"] and row["sameStoreReference"] and row["storeBefore"] == row["storeAfter"], row["id"] + " parent/store identity")
        require(row["before"] == row["after"], row["id"] + " full parent state")
        require(base64.b64decode(row["existingOutput"], validate=True) == b"existing-output\r\n", row["id"] + " existing-output bytes")
        require("entry" == row["clicks"][0], row["id"] + " actual entry")
        expected_candidates = 0 if mode in ("cancel-before", "close-before", "tab-limit", "manifest-cancel", "manifest-root-change") else 1
        require(row["candidateCount"] == expected_candidates, row["id"] + " exact candidate count")
        if "candidatePayloadBeforeGate" in row:
            for payload, expected in zip(row["candidatePayloadBeforeGate"]["sides"], spec["sides"]):
                require(payload["text"] == expected["text"] and base64.b64decode(payload["bytes"], validate=True) == raw(expected), "candidate read completed with literal bytes before gate")
        if mode in ("physical-sha-change", "archive-sha-change"):
            change = row["tamper"]
            suffix = ".txt" if mode == "physical-sha-change" else ".zip"
            require("candidatePayloadBeforeGate" in row and change["timing"] == "after-candidate-read-before-final-rehash", "tamper window after original read")
            require(change["shaBefore"] == sha(roots["inputs/left" + suffix]) and change["shaAfter"] == sha(roots["inputs/left-alternate" + suffix]), "independent literal tamper SHA")
            before_path = pathlib.Path(change["beforePath"]).resolve(strict=True)
            after_path = pathlib.Path(change["afterPath"]).resolve(strict=True)
            capture_directory = (run / "tamper" / row["id"].replace(":", "-")).resolve(strict=True)
            require(before_path != after_path and before_path == capture_directory / "before.raw" and after_path == capture_directory / "after.raw", "case-local raw backup/capture paths")
            before_bytes, after_bytes = before_path.read_bytes(), after_path.read_bytes()
            require(before_bytes == roots["inputs/left" + suffix] and after_bytes == roots["inputs/left-alternate" + suffix], "retained before/after raw full literal bytes")
            require(change["sizeBefore"] == len(before_bytes) == before_path.stat().st_size and change["sizeAfter"] == len(after_bytes) == after_path.stat().st_size and len(before_bytes) == len(after_bytes), "actual same-size raw capture binding")
            require(sha(before_bytes) == change["shaBefore"] and sha(after_bytes) == change["shaAfter"] and change["shaBefore"] != change["shaAfter"], "raw capture fixed SHA binding")
            require(before_path.stat().st_mtime_ns == after_path.stat().st_mtime_ns == FIXED_RUN_MTIME_NS, "actual stat exact ns equality; DateTime Ticks are not ns evidence")
            require(change["fixedUtc"] == "2024-01-01T00:00:00.1234567Z" and change["provenance"] == "synthetic-input266: immutable fixture original and alternate; case-local before.raw backup and after.raw capture; shared run input restored after case", "raw snapshot provenance")
        if mode in ("cancel-gate", "close-gate", "abort-gate", "kind-change", "root-change", "readonly-change", "pair-change", "tab-away-return", "manifest-cancel", "manifest-root-change", "manifest-retry"):
            require(row["gateTokenCancelled"], row["id"] + " stale operation token cancelled")
        require(row["activeAfter"] != row["activeBefore"] if success else row["activeAfter"] == row["activeBefore"], row["id"] + " selected tab")
        # 親は製品の自己申告だけでなく、別literalの元原文とdirty状態へ照合する。
        for index, side in enumerate(row["before"]["sides"]):
            expected = specs["physical-0"]["sides"][index]
            text = expected["text"] + ("\nparent-dirty" if index == 0 else "")
            require(side["text"] == text and side["dirty"] is (index == 0), row["id"] + " literal parent body/dirty")
            encoded = (b"\xef\xbb\xbf" + text.encode("utf-8")) if index == 0 else (b"\xff\xfe" + text.encode("utf-16-le")) if index == 1 else text.encode("utf-8")
            require(base64.b64decode(side["bytes"], validate=True) == encoded and side["sha256"] == sha(encoded), "parent encoding/BOM/newline bytes")
        if not success:
            require("payload" not in row and "routes" not in row, "refused candidate has no adopted payload")
            continue
        require("compare" in row["clicks"] and row["pair"] == spec["pair"] and row["semantics"] == "Independent", "actual Compare and adopted semantics/pair")
        require(row["payload"]["pairIndex"] == spec["pairIndex"], "adopted pair index")
        require(len(row["routes"]) == len(row["payload"]["sides"]) == 3, "exact adopted 3side")
        for side_index, (route, payload, expected) in enumerate(zip(row["routes"], row["payload"]["sides"], spec["sides"])):
            require(route["kind"] == expected["kind"] and route["readOnly"] is expected["readOnly"], "route kind/readonly")
            require(payload["text"] == expected["text"] and base64.b64decode(payload["bytes"], validate=True) == raw(expected) and payload["sha256"] == sha(raw(expected)), "all original bytes/encoding/BOM/newline")
            require(payload["editorReadOnly"] is expected["editorReadOnly"] and payload["dirty"] is False, "editor readonly/save point")
            if route["kind"] == "Untitled":
                require(route["path"] == "" and not any(c["side"] == side_index for c in row["pickerCalls"]), "Untitled empty path/no picker")
            else:
                path = run / pathlib.PurePosixPath(expected["source"]).name
                call = [c for c in row["pickerCalls"] if c["side"] == side_index]
                require(len(call) == 1 and pathlib.Path(call[0]["path"]) == path and call[0]["archive"] is (route["kind"] == "Archive"), "actual injected picker absolute path")
                require(path.is_absolute(), "absolute source picker")
                if route["kind"] == "Archive":
                    require(route["path"] == "" and pathlib.Path(route["root"]) == path and route["rootSha256"] == sha(roots[expected["source"]]), "typed root/SHA")
                    require(route["chain"] == expected["chain"] and route["leaf"] == expected["leaf"] and route["inheritedReadOnly"] is False, "chain/leaf/explicit editing")
                    require(f"{side_index}:load" in row["clicks"] and f"{side_index}:row:{expected['leaf']}" in row["clicks"], "actual manifest/leaf row")
                    if expected["chain"]:
                        require(f"{side_index}:back" in row["clicks"] and row["clicks"].count(f"{side_index}:open") == 2, "actual open/back/reopen")
                else:
                    require(pathlib.Path(route["path"]) == path and "root" not in route, "Physical exact path")
    layouts = observation["layouts"]
    require(layouts, "PNG/bounds layouts missing")
    dialog_lists = {(l["width"], l["height"], l["name"].split("-")[-2]) for l in layouts if l["name"].endswith("-list")}
    require(dialog_lists == {(w, h, str(side)) for w, h in [(1000, 680), (850, 550)] for side in range(3)}, "all six dialog list geometry sizes/sides")
    for layout in layouts:
        require(pathlib.Path(layout["png"]).name == layout["png"], "safe relative PNG")
        check_png(run / layout["png"], layout["width"], layout["height"])
        for control in layout["controls"]:
            b, c = control["bounds"], control["clip"]
            require(control["visible"] and b["width"] > 0 and b["height"] > 0, "visible control " + control["name"])
            require(b["x"] >= c["x"] - 0.01 and b["y"] >= c["y"] - 0.01 and b["x"] + b["width"] <= c["x"] + c["width"] + 0.01 and b["y"] + b["height"] <= c["y"] + c["height"] + 0.01, "actual clipping viewport " + control["name"])
            require(control["name"] != "list" or b["height"] >= 100, "list 100 DIP")
    return run, dict(status="completed-minimum-set-only", productCases=len(records), pngBounds=len(layouts), fixedScope="42 unchanged pins; B588/D99 execution is a separate required run", unverified="coverage.md lists remaining critical contracts; native picker/pointer/AOT not covered")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=pathlib.Path, required=True)
    parser.add_argument("--repo", type=pathlib.Path, required=True)
    parser.add_argument("--gui-report", type=pathlib.Path)
    parser.add_argument("--full-ui", action="store_true")
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()
    manifest, roots = verify_fixture(args.fixture.resolve(), args.repo.resolve())
    result = dict(status="fixture-only-product-unexecuted", fixtureCases=15, reusedPins=42)
    if args.gui_report:
        run, result = verify_observations(manifest, roots, args.gui_report.resolve(), args.full_ui)
        verify_fixture(args.fixture.resolve(), args.repo.resolve(), run)
    text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        require(not args.output.exists(), "reader output must be a new run path")
        args.output.write_text(text, encoding="utf-8")
    print(text, end="")


if __name__ == "__main__":
    main()
