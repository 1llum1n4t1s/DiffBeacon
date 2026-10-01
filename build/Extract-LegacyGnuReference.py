"""原本 executable の実行・採取。比較アルゴリズム・期待 script を実装しない。"""
import base64
import hashlib
import itertools
import json
from pathlib import Path
import re
import subprocess
import sys


def digest(data):
    return hashlib.sha256(data).hexdigest()


def flags(mode):
    return dict(outputStyle="normal", context=0, defaultAlgorithm=0,
                heuristic=1, noDiscards=0, alwaysText=0, horizon=0,
                noDetails=0, lineEndChar=10, movedBlocks=0,
                ignoreBlankLines=0, ignoreCase=int(mode == "case"),
                ignoreSpaceChange=int(mode == "space-change"),
                ignoreAllSpace=int(mode == "space-all"),
                ignoreNumbers=int(mode == "numbers"),
                ignoreEol=int(mode == "eol"),
                ignoreSomeChanges=int(mode in ("case", "space-change", "space-all", "eol")),
                lengthVaries=int(mode in ("space-change", "space-all")), locale="C")


def split_lines(data):
    # 検証専用。BOM は io.c が除去。script を計算しない。
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    return re.findall(rb"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$", data)


def main():
    exe, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    cases = []

    def add(name, left, right, mode="default", category="small", caller="direct UTF-8 bytes"):
        if isinstance(left, str):
            left = left.encode("utf-8")
        if isinstance(right, str):
            right = right.encode("utf-8")
        cases.append((name, left, right, mode, category, caller))

    add("both-empty", "", "")
    add("left-empty", "", "a\nb\n")
    add("right-empty", "a\nb\n", "")
    add("blank-vs-empty", "\n", "")
    add("identical", "a\nb\n", "a\nb\n")
    add("blank-repetition", "\n\na\n\n", "\na\n\n\n")
    add("repeated-anchor", "A\nB\nA\nB\nA\n", "B\nA\nB\nA\nB\n")
    add("crossing-unique", "A\nB\nC\nD\n", "C\nD\nA\nB\n")
    add("tie-ab-ba", "A\nB\n", "B\nA\n")
    add("prefix-insertion", "a\nb\n", "x\na\nb\n")
    add("suffix-insertion", "a\nb\n", "a\nb\nx\n")
    add("prefix-deletion", "x\na\nb\n", "a\nb\n")
    add("suffix-deletion", "a\nb\nx\n", "a\nb\n")
    add("middle-replace-prefix", "p1\np2\na\ns1\ns2\n", "p1\np2\nb\ns1\ns2\n")
    add("multi-hunk", "p\na\nm\nc\ns\n", "p\nb\nm\nd\ns\n")
    add("frequent-isolated", "p\n" + "X\n"*12 + "a\nY\nb\n" + "X\n"*12 + "s\n",
        "p\n" + "X\n"*12 + "c\nY\nd\n" + "X\n"*12 + "s\n", category="discard")
    add("frequent-discard-run", "L\n"*6 + "X\n" + "L\n"*6 + "A\n" + "X\n"*8,
        "R\n"*6 + "X\n" + "R\n"*6 + "A\n" + "X\n"*8, category="discard")
    for frequency in (5, 6, 9):
        add(f"provisional-frequency-{frequency}",
            "".join(f"l{i}\n" for i in range(6)) + "X\n" + "".join(f"l{i}\n" for i in range(6, 12)) + "Y\n" + "X\n"*(frequency-1) + "left-tail\n",
            "".join(f"r{i}\n" for i in range(6)) + "X\n" + "".join(f"r{i}\n" for i in range(6, 12)) + "Y\n" + "X\n"*(frequency-1) + "right-tail\n", category="discard")
    add("crlf-vs-lf", "a\r\nb\r\n", "a\nb\n", category="eol")
    add("cr-vs-lf", "a\rb\r", "a\nb\n", category="eol")
    add("mixed-eol", "a\r\nb\rc\n", "a\nb\r\nc\r", category="eol")
    add("final-newline", "a\nb", "a\nb\n", category="eol")
    add("missing-final-both", "a\nb", "a\nc", category="eol")
    add("empty-vs-no-eol", "", "a", category="eol")
    add("no-eol-vs-empty", "a", "", category="eol")
    add("utf8-non-ascii", "桜\n😀\né\n", "桜\n猫\né\n", category="encoding")
    add("utf8-bom-both", b"\xef\xbb\xbf" + "桜\na\n".encode(), b"\xef\xbb\xbf" + "桜\nb\n".encode(), category="encoding")
    add("utf8-bom-one-side", b"\xef\xbb\xbf"+b"a\nb\n", b"a\nb\n", category="encoding")
    add("utf8-bom-empty", b"\xef\xbb\xbf", b"", category="encoding")
    for mode in ("default", "case"):
        add("case-"+mode, "Alpha\nBETA\n", "alpha\nbeta\n", mode, "flags")
    for mode in ("default", "space-change", "space-all"):
        add("space-"+mode, "a  b \n c\n", "a\tb\nc\n", mode, "flags")
    for mode in ("default", "numbers"):
        add("numbers-equal-length-"+mode, "v12\nitem34\n", "v98\nitem76\n", mode, "flags")
        add("numbers-varying-length-"+mode, "v1\nitem22\n", "v333\nitem4\n", mode, "flags")
    add("ignore-eol", "a\r\nb\rc\n", "a\nb\r\nc\r", "eol", "flags")
    caller = "hand-authored comparison-temp bytes matching SaveToFile table escaping; caller not executed"
    bom = b"\xef\xbb\xbf"
    add("table-quoted-vs-unquoted", bom+b"a\n", bom+b'"a"\n', category="table-raw", caller=caller)
    add("table-quoted-embedded-lf", bom+b'"a\x1bnx",v\n', bom+b'"a\x1bny",v\n', category="table-raw", caller=caller)
    add("table-quoted-embedded-crlf", bom+b'"a\x1br\x1bnx",v\r\n', bom+b'"a\x1br\x1bny",v\r\n', category="table-raw", caller=caller)
    add("table-literal-escape-n", bom+b'"a\x1b\x1bnx",v\n', bom+b'"a\x1bnx",v\n', category="table-raw", caller=caller)
    add("table-no-final-eol", bom+b'"a\x1bnx",v', bom+b'"a\x1bny",v', category="table-raw", caller=caller)
    add("table-one-side-quote-only", bom+b'a,b\nkeep,v\n', bom+b'"a",b\nkeep,v\n', category="table-raw", caller=caller)
    # 全組合せでも script は原本実行から採取する。tie と空側・反復の網羅用。
    seqs = [()] + [s for n in range(1, 4) for s in itertools.product(("A", "B"), repeat=n)]
    for i, left in enumerate(seqs):
        for j, right in enumerate(seqs):
            add(f"ab-exhaustive-{i:02}-{j:02}", "".join(x+"\n" for x in left),
                "".join(x+"\n" for x in right), category="exhaustive-small")
    # heuristic の c>200 と TOO_EXPENSIVE>=4096 の入力規模を通す。
    # 分岐到達を instrumentation で観測してはいない。性能/分岐 coverage の成功主張はしない。
    for n in (199, 201, 4095, 4097):
        add(f"heuristic-reversed-{n}", "".join(f"u{i}\n" for i in range(n)),
            "".join(f"u{i}\n" for i in reversed(range(n))), category="heuristic-size")
    for n in (199, 201, 220):
        u = "".join(f"u{i}\n" for i in range(n))
        v = "".join(f"v{i}\n" for i in range(n))
        snake = "".join(f"s{i}\n" for i in range(40))
        add(f"heuristic-big-snake-{n}", u+snake+v, v+snake+u, category="heuristic-size")

    records, assertions = [], []
    for name, left, right, mode, category, caller in cases:
        case_dir = output / "cases" / name
        case_dir.mkdir(parents=True, exist_ok=True)
        inputs = [case_dir / "left.bin", case_dir / "right.bin"]
        inputs[0].write_bytes(left)
        inputs[1].write_bytes(right)
        command = [str(exe), str(inputs[0]), str(inputs[1]), mode]
        completed = subprocess.run(command, capture_output=True, timeout=90)
        (case_dir / "stdout.json").write_bytes(completed.stdout)
        (case_dir / "stderr.log").write_bytes(completed.stderr)
        if completed.returncode:
            raise RuntimeError(f"{name}: exit {completed.returncode}; see {case_dir}")
        result = json.loads(completed.stdout)
        passed = completed.stderr == b"" and result["binaryStatus"] == 0 and result["binaryFiles"] == 0
        passed &= result["lengths"] == result["bufferedLines"]
        passed &= result["equivMax"] == [result["classCount"]] * 2
        passed &= result["classCount"] >= 1
        passed &= [len(side) for side in result["equivs"]] == result["lengths"]
        passed &= all(isinstance(value, int) and 0 <= value < result["classCount"]
                      for side in result["equivs"] for value in side)
        pos0 = pos1 = 0
        left_lines, right_lines = split_lines(left), split_lines(right)
        applied = []
        for raw, restored in zip(result["rawChanges"], result["changes"], strict=True):
            a, b, d, ins = (restored[k] for k in ("line0", "line1", "deleted", "inserted"))
            passed &= a == raw["line0"] + result["prefixLines"][0]
            passed &= b == raw["line1"] + result["prefixLines"][1]
            passed &= 0 <= pos0 <= a <= a+d <= len(left_lines)
            passed &= 0 <= pos1 <= b <= b+ins <= len(right_lines)
            passed &= a-pos0 == b-pos1 and d+ins > 0
            applied += left_lines[pos0:a] + right_lines[b:b+ins]
            pos0, pos1 = a+d, b+ins
        passed &= len(left_lines)-pos0 == len(right_lines)-pos1
        applied += left_lines[pos0:]
        if mode == "default":
            passed &= applied == right_lines
        passed &= [p.read_bytes() for p in inputs] == [left, right]
        assertion = dict(name=name, passed=bool(passed), scriptOrigin="original executable only",
                         sourceInputsUnchanged=True, exactReconstructionChecked=mode == "default",
                         monotoneCoordinatesAndCountsChecked=True,
                         originalEquivalenceLengthsAndRangeChecked=True)
        assertions.append(assertion)
        records.append(dict(name=name, category=category, callerScope=caller, mode=mode, flags=flags(mode),
                            inputs=[dict(path=str(p.relative_to(output)).replace("\\", "/"),
                                         byteLength=len(data), sha256=digest(data), base64=base64.b64encode(data).decode())
                                    for p, data in zip(inputs, (left, right), strict=True)],
                            command=command, exitCode=completed.returncode, stdoutSha256=digest(completed.stdout),
                            stderrSha256=digest(completed.stderr), **result))
        if not passed:
            raise RuntimeError(f"{name}: coordinate/reconstruction contract failed; see {case_dir}")
    (output / "legacy-gnu-golden.json").write_text(json.dumps(dict(schemaVersion=1, cases=records), ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    (output / "assertions.json").write_text(json.dumps(assertions, indent=2)+"\n", encoding="utf-8")
    summary = dict(cases=len(records), assertionsPassed=len(assertions),
                   defaultCases=sum(r["mode"] == "default" for r in records),
                   categoryCounts={c: sum(r["category"] == c for r in records) for c in sorted({r["category"] for r in records})},
                   discardedCases=sum(any(b > n for b, n in zip(r["bufferedLines"], r["nondiscardedLines"])) for r in records),
                   scope="GNU original two-file raw scripts; not old GUI/table-caller execution")
    (output / "summary.json").write_text(json.dumps(summary, indent=2)+"\n", encoding="utf-8")
    metadata_path = output / "metadata.json"
    metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig"))
    metadata["equivalenceObservation"] = dict(
        equivs="file_data.equivs[0..buffered_lines)", lengths="file_data.buffered_lines",
        equivMax="each file_data.equiv_max",
        classCount="shared equiv_max, including reserved class 0; valid IDs [0,classCount)",
        adapter="read-only JSON output, no reclassification or renumbering")
    metadata_path.write_text(json.dumps(metadata, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
