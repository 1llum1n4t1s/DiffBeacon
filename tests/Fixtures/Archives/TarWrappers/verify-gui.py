"""多層TAR GUIの完成候補と原本を固定fixtureから独立照合する。"""
import argparse
import datetime
import importlib.util
import json
import pathlib


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("facts", type=pathlib.Path)
    args = parser.parse_args()
    fixture = pathlib.Path(__file__).resolve().parent
    spec = importlib.util.spec_from_file_location("tar_wrapper_independent", fixture / "verify.py")
    independent = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(independent)
    manifest_bytes = (fixture / "manifest.json").read_bytes()
    manifest = json.loads(manifest_bytes)
    facts = json.loads(args.facts.read_text(encoding="utf-8-sig"))
    assert facts["manifestSha256"] == independent.sha(manifest_bytes), "GUI manifest binding"
    expected = {case["name"]: case for case in manifest["cases"] if case["valid"]}
    observed = facts["cases"]
    assert len(observed) == len(expected) == 22, "All normal cases required"
    assert len({case["name"] for case in observed}) == 22, "Duplicate GUI case"
    assert {case["name"] for case in observed} == set(expected), "Missing GUI case"
    entry_count = 0
    for case in observed:
        golden = expected[case["name"]]
        original = (fixture / case["name"]).read_bytes()
        assert independent.sha(original) == golden["sha256"], "Fixed input SHA"
        assert pathlib.Path(case["inputPath"]).read_bytes() == original, "GUI physical input bytes"
        assert case["inputShaBefore"] == case["inputShaAfter"] == golden["sha256"], "GUI input preservation"
        decoded = original
        for layer in golden["layers"]:
            decoded = independent.decode(decoded, layer["wrapper"])
            assert len(decoded) == layer["decodedBytes"] and independent.sha(decoded) == layer["decodedSha256"]
        assert independent.sha(decoded) == golden["tarSha256"]
        assert independent.entries(decoded) == golden["entries"], "Independent TAR full entries"
        rows = case["candidateEntries"]
        assert len(rows) == len(golden["entries"]), "GUI candidate entry count"
        assert len({row["path"] for row in rows}) == len(rows), "Duplicate GUI candidate entry"
        actual = {row["path"]: row for row in rows}
        for entry in golden["entries"]:
            row = actual[entry["path"]]
            for field in ("directory", "size", "sha256"):
                assert row[field] == entry[field], (case["name"], entry["path"], field)
            expected_time = datetime.datetime.fromtimestamp(entry["mtime"], datetime.timezone.utc)
            assert datetime.datetime.fromisoformat(row["modifiedUtc"].replace("Z", "+00:00")) == expected_time
        stages = case["observations"]
        assert len(stages) == 3 and {stage["stage"] for stage in stages} == {"early-stop", "late-stop", "refresh-stop"}
        for stage in stages:
            assert all(stage[key] is True for key in ("canceled", "samePanel", "rowsPreserved", "previewPreserved", "operable")), stage
        assert case["staleLatestOnly"] is True and case["discardedDisposed"] is True
        entry_count += len(rows)
    print(json.dumps({"accepted": True, "normalCases": 22, "candidateEntries": entry_count,
                      "inputFullBytes": True, "independentFullLayersAndTar": True,
                      "scope": "GUI completed-candidate bytes/metadata and recorded real Stop/adoption observations; decode-in-progress timing not inferred"}))


if __name__ == "__main__":
    main()
