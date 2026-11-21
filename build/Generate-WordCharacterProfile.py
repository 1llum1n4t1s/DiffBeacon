"""採取済み Windows/CRT 分類 RLE を検証し、Core の定数表を生成する。"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def main() -> None:
    directory = Path(__file__).resolve().parent
    repository = directory.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=repository / "tests/Fixtures/WordDiffs/legacy-worddiff-golden.json")
    parser.add_argument("--output", type=Path, default=repository / "Src/DiffBeacon.Core/WordCharacterProfile.cs")
    args = parser.parse_args()
    source_bytes = args.input.read_bytes()
    profile = json.loads(source_bytes)
    ranges = profile.get("classificationRanges")
    if not isinstance(ranges, list) or not ranges:
        raise ValueError("classificationRanges must be a nonempty array")
    if profile.get("locale") != "C":
        raise ValueError("this profile requires the measured CRT C locale")
    expected_folds = [[value, value + 32] for value in range(65, 91)]
    if profile.get("caseFoldPairs") != expected_folds:
        raise ValueError("caseFoldPairs must be exactly the measured 26 ASCII pairs")

    next_start = 0
    previous_flags = None
    encoded = []
    coverage = [0] * 8
    for index, item in enumerate(ranges):
        if not isinstance(item, list) or len(item) != 3 or any(type(value) is not int for value in item):
            raise ValueError(f"range {index}: expected three integers")
        start, end, flags = item
        if start != next_start or not 0 <= start <= end <= 65535 or not 0 <= flags <= 7:
            raise ValueError(f"range {index}: invalid bounds, continuity, or flags")
        if flags == previous_flags:
            raise ValueError(f"range {index}: adjacent equal flags must be merged")
        encoded.append((end << 3) | flags)
        coverage[flags] += end - start + 1
        previous_flags = flags
        next_start = end + 1
    if next_start != 65536 or sum(coverage) != 65536:
        raise ValueError("classificationRanges must cover all 65536 UTF-16 code units")
    if any(value >> 3 != item[1] or value & 7 != item[2] for value, item in zip(encoded, ranges, strict=True)):
        raise ValueError("packed table differs from the source profile")

    digest = hashlib.sha256(source_bytes).hexdigest()
    lines = [
        "namespace DiffBeacon.Core;",
        "",
        "// このファイルは build/Generate-WordCharacterProfile.py から生成する。",
        "// 元ソース実行による Windows GetStringTypeW / CRT C の UTF-16 全分類採取値。",
        "// 採取基準: Windows build 26300 / MSVC 19.51 / ICU 72.1.0.4 / CRT locale=C。",
        "// ICU は採取環境情報。分類表自体は Windows/CRT に由来し、実行時 DLL は不要。",
        "// 入力: tests/Fixtures/WordDiffs/legacy-worddiff-golden.json",
        f"// 入力 SHA-256: {digest}",
        "// bit 0: C1_UPPER|C1_LOWER|C1_DIGIT、bit 1: iswdigit、bit 2: iswspace (CR/LF 除外)。",
        f"// {len(encoded)} 区間、inclusive end << 3 | flags。全 BMP (surrogate を含む) を連続して覆う。",
        "internal static class WordCharacterProfile",
        "{",
        "    private static ReadOnlySpan<uint> EncodedEnds =>",
        "    [",
    ]
    for index in range(0, len(encoded), 8):
        lines.append("        " + ", ".join(f"0x{value:05X}u" for value in encoded[index:index + 8]) + ",")
    lines += [
        "    ];",
        "",
        "    internal static byte Flags(char value)",
        "    {",
        "        var ranges = EncodedEnds;",
        "        var low = 0;",
        "        var high = ranges.Length - 1;",
        "        while (low < high)",
        "        {",
        "            var middle = low + (high - low) / 2;",
        "            if ((uint)value > ranges[middle] >> 3) low = middle + 1;",
        "            else high = middle;",
        "        }",
        "        return (byte)(ranges[low] & 7u);",
        "    }",
        "}",
        "",
    ]
    content = "\n".join(lines)
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(content)
    print(json.dumps({"input_sha256": digest, "ranges": len(encoded), "coverage": sum(coverage),
                      "flags_counts": coverage, "packed_bytes": len(encoded) * 4,
                      "output": str(args.output)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
