"""Deterministic synthetic inputs. Never derives expectations from product output."""
import base64
import hashlib
import io
import json
import pathlib
import zlib
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[4]


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def archive(entries):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as stream:
        for name, payload in entries:
            info = zipfile.ZipInfo(name, (2024, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.external_attr = (0o40755 if name.endswith("/") else 0o100644) << 16
            stream.writestr(info, payload)
    return buffer.getvalue()


def generate():
    target = HERE / "fixture"
    if target.exists():
        raise SystemExit("Existing fixture retained; use a new owned directory or compare without overwrite.")
    target.mkdir()
    inputs = target / "inputs"
    inputs.mkdir()
    roles = ["left", "middle", "right"]
    texts = ["shared café\r\nLEFT €\nlast-left\r", "shared café\r\nMIDDLE £\nlast-middle\r", "shared café\r\nRIGHT “quote”\nlast-right\r"]
    encodings = ["utf-8", "utf-16-le", "utf-8"]
    prefixes = [b"\xef\xbb\xbf", b"\xff\xfe", b""]
    raws = [prefix + text.encode(codec) for prefix, text, codec in zip(prefixes, texts, encodings)]
    file_pins, zip_entries = [], []

    def write(name, data):
        (inputs / name).write_bytes(data)
        file_pins.append(dict(path="inputs/" + name, size=len(data), sha256=sha(data)))

    def pin_zip(path, data, chain=()):
        with zipfile.ZipFile(io.BytesIO(data)) as stream:
            entries = []
            for info in stream.infolist():
                raw = stream.read(info)
                entries.append(dict(name=info.filename, size=len(raw), crc32=f"{zlib.crc32(raw):08X}", sha256=sha(raw), directory=info.is_dir()))
                if info.filename.endswith(".zip"):
                    pin_zip(path, raw, (*chain, info.filename))
            zip_entries.append(dict(path=path, chain=list(chain), entries=entries))

    for role, raw in zip(roles, raws):
        write(role + ".txt", raw)
        inner = archive([("docs/", b""), ("docs/" + role + ".txt", raw), ("untouched.bin", b"\x00\x01\xff")])
        outer = archive([("folder/", b""), ("nested.zip", inner), ("top.txt", b"TOP\r\n")])
        write(role + ".zip", outer)
        pin_zip("inputs/" + role + ".zip", outer)
    combined = archive([(role + ".txt", raw) for role, raw in zip(roles, raws)])
    write("combined.zip", combined)
    pin_zip("inputs/combined.zip", combined)
    # 同size差替え: 外部から供給した異なるliteral。元ZIP構造の無効化は期待する拒否理由。
    alternate = bytearray((inputs / "left.zip").read_bytes())
    alternate[31] ^= 1
    write("left-alternate.zip", alternate)
    alternate_physical = raws[0].replace(b"LEFT", b"NEXT")
    assert len(alternate_physical) == len(raws[0])
    write("left-alternate.txt", alternate_physical)
    cases = []
    pair_names = ["LeftMiddle", "MiddleRight", "LeftRight"]
    for route in ["physical", "untitled", "archives", "mixed", "same-root"]:
        for pair_index, pair in enumerate(pair_names):
            sides = []
            for i, role in enumerate(roles):
                kind = "Untitled" if route == "untitled" else "Archive" if route in ("archives", "same-root") or route == "mixed" and i == 1 else "Physical"
                source = "inputs/combined.zip" if route == "same-root" else "inputs/" + role + (".zip" if kind == "Archive" else ".txt") if kind != "Untitled" else ""
                sides.append(dict(kind=kind, source=source, chain=["nested.zip"] if kind == "Archive" and route != "same-root" else [], leaf=("docs/" if route != "same-root" else "") + role + ".txt" if kind == "Archive" else None,
                                  readOnly=kind == "Archive", inheritedReadOnly=False if kind == "Archive" else None,
                                  editorReadOnly=False, text="" if kind == "Untitled" else texts[i],
                                  bytesBase64=base64.b64encode(b"" if kind == "Untitled" else raws[i]).decode(),
                                  encoding=None if kind == "Untitled" else encodings[i], bom=kind != "Untitled" and bool(prefixes[i])))
            cases.append(dict(id=route + "-" + str(pair_index), pair=pair, pairIndex=pair_index, sides=sides))
    existing = REPO / "tests/Fixtures/IndependentArchiveText"
    pins = json.loads((existing / "fixed-sha256.json").read_text(encoding="utf-8-sig"))
    for pin in pins["files"]:
        data = (existing / pin["path"]).read_bytes()
        assert len(data) == pin["size"] and sha(data) == pin["sha256"], pin["path"]
    manifest = dict(schema="input-selection-fixture-v1", source="synthetic-input266 literal; not original WinMerge output", license="MIT; tests/Fixtures/IndependentArchiveText/LICENSE retained by reference", files=file_pins, zipEntries=zip_entries, cases=cases,
                    reusedFixedScope=dict(path="tests/Fixtures/IndependentArchiveText", manifestSha256=sha((existing / "fixed-sha256.json").read_bytes()), files=pins["files"], productReader="verify-products.py", reuseStatus="read-only pin validation; B588/D99 product execution remains separate"))
    (target / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(dict(status="synthetic-generated", files=len(file_pins), cases=len(cases), reusedPins=len(pins["files"]))))


if __name__ == "__main__":
    generate()
