"""Frozen engineering contract reader. Does not run the application or implement Sublines."""
import argparse
import hashlib
import html
import json
import pathlib
import re
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--actual", type=pathlib.Path, required=True)
    args = parser.parse_args()
    fixture = pathlib.Path(__file__).resolve().parent
    raw = (fixture / "contract.json").read_bytes()
    assert hashlib.sha256(raw).hexdigest().upper() == (fixture / "contract.sha256").read_text().split()[0]
    contract = json.loads(raw)
    actual = json.loads(args.actual.read_bytes())
    assert actual["qualification"] == contract["qualification"]
    observations = {item["id"]: item for item in actual["observations"]}
    path_observations = {item["id"]: item for item in actual["pathObservations"]}
    rejections = {item["id"]: item for item in actual["rejections"]}
    assert len(observations) == len(actual["observations"]) == 2 * len(contract["cases"])
    assert len(path_observations) == len(actual["pathObservations"]) == 4
    assert len(rejections) == len(actual["rejections"]) == 2 * len(contract["errors"])
    for item in contract["cases"]:
        for suffix in ("-optimized", "-noopt"):
            case_id = item["id"] + suffix
            observation = observations[case_id]
            assert observation["passed"] is True and observation["ExitCode"] == 0, case_id
            assert observation["expression"] == item["expression"], case_id
            argv = observation["arguments"]
            expected_key = item["expectedKey"].replace(
                "<Content>", f"<FileContent:{pathlib.Path(argv[1]).resolve()}>"
            )
            assert observation["expectedKey"] == expected_key, case_id
            assert argv[0] == ("--table" if item.get("mode") == "Table" else "--compare")
            quote = '"' + expected_key.replace('"', '""') + '"'
            rules = []
            if "preExpression" in item:
                rules += ["--line-transform", "left", item["preExpression"]]
            case_directive = "@cs " if item.get("caseSensitive", True) else "@ci "
            rules += ["--line-transform", "left", case_directive + ("@noopt " if suffix == "-noopt" else "") + item["expression"]]
            if "postExpression" in item:
                rules += ["--line-transform", "left", item["postExpression"]]
            rules += ["--line-transform", "right", "If(true," + quote + ",Sublines(Content,0))"]
            assert argv[3:] == rules, case_id
            for side, path in zip(("left", "right"), argv[1:3]):
                assert pathlib.Path(path).resolve() == (args.actual.parent / (case_id + "-" + side + ".txt")).resolve()
                assert pathlib.Path(path).read_bytes() == item["source"].encode("utf-8"), case_id
            comparison = json.loads(observation["stdout"])
            assert comparison["different"] is False, case_id
            if item.get("mode") != "Table":
                rows = re.split(r"\r\n|\r|\n", item["source"])
                if item["source"].endswith(("\r", "\n")):
                    rows.pop()
                if item["source"] == "":
                    rows = []
                for side in ("left", "right"):
                    actual_rows = [row for row in comparison["rows"] if side + "Line" in row]
                    assert [row[side] for row in actual_rows] == rows, case_id
                    assert [row[side + "Line"] for row in actual_rows] == list(range(1, len(rows) + 1)), case_id
    for case_id, content_side in (("legacy-left", "left"), ("legacy-right", "right")):
        observation = path_observations[case_id]
        assert observation["passed"] is True and observation["ExitCode"] == 0, case_id
        argv = observation["arguments"]
        assert argv[0] == "--compare" and len(argv) == 9, case_id
        source_index = 1 if content_side == "left" else 2
        source_path = str(pathlib.Path(argv[source_index]).resolve())
        marker = f"<FileContent:{source_path}>"
        assert observation["sourcePath"] == source_path and observation["expectedMarker"] == marker, case_id
        quote = '"' + marker.replace('"', '""') + '"'
        expected_rules = (["--line-transform", "left", "Content", "--line-transform", "right", quote]
                          if content_side == "left" else
                          ["--line-transform", "left", quote, "--line-transform", "right", "Content"])
        assert argv[3:] == expected_rules, case_id
        for side, path in zip(("left", "right"), argv[1:3]):
            assert pathlib.Path(path).read_bytes() == b"path probe\r\n", case_id
        comparison = json.loads(observation["stdout"])
        assert comparison["different"] is False, case_id
    for case_id, pair in (("legacy-three-way-left-middle", "left-middle"),
                          ("legacy-three-way-middle-right", "middle-right")):
        three_way = path_observations[case_id]
        assert three_way["passed"] is True and three_way["ExitCode"] == 0, case_id
        assert three_way["pair"] == pair, case_id
        paths = three_way["sourcePaths"]
        argv = three_way["arguments"]
        expected = ["--compare", paths["left"], paths["right"], "--independent-text",
                    "--middle", paths["base"], "--pair", pair]
        expected_sources = {"left": b"same\r\n", "base": b"same\r\n", "right": b"same\r\n"}
        for side in ("left", "base", "right"):
            source_path = pathlib.Path(paths[side]).resolve()
            marker = f"<FileContent:{source_path}>"
            quote = '"' + marker.replace('"', '""') + '"'
            rule = f'If(""+Content={quote},"same","bad-{side}")'
            expected += ["--line-transform", side, rule]
            assert source_path == (args.actual.parent / f"three-path-{side}.txt").resolve(), case_id
            assert source_path.read_bytes() == expected_sources[side], case_id
        assert argv == expected, case_id
        comparison = json.loads(three_way["stdout"])
        assert comparison["different"] is False and comparison["pair"] == ("LeftMiddle" if pair == "left-middle" else "MiddleRight"), case_id
        expected_paths = {"left": paths["left"], "middle": paths["base"], "right": paths["right"]}
        assert {item["role"]: item["path"] for item in comparison["inputs"]} == expected_paths, case_id
    work = args.actual.parent
    report = html.unescape((work / "report.html").read_text(encoding="utf-8"))
    assert 'data-ignored="true"' in report and "left-original" in report and "right-original" in report
    with zipfile.ZipFile(work / "package.zip") as package:
        report_entries = [name for name in package.namelist() if name.lower().startswith("report.files/") and name.lower().endswith(".html")]
        assert report_entries == ["report.files/1.html"]
        assert "report.html" in package.namelist()
        packaged_report = html.unescape(package.read("report.files/1.html").decode("utf-8"))
        assert 'data-ignored="true"' in packaged_report and "left-original" in packaged_report and "right-original" in packaged_report
    for item in contract["errors"]:
        for suffix in ("-optimized", "-noopt"):
            observation = rejections[item["id"] + suffix]
            assert observation["expression"] == ("@noopt " if suffix == "-noopt" else "") + item["expression"]
            assert observation["ExitCode"] == 2 and not observation["stdout"].strip() and observation["stderr"].strip()
    print(json.dumps({"successCases": len(observations), "rejections": len(rejections), "nativeGolden": False}))


if __name__ == "__main__":
    main()
