#!/usr/bin/env python3
"""Independently verify archived image roots, nested GIF bytes, and every decoded pixel frame."""

import hashlib
import io
import json
import sys
import zipfile
from pathlib import Path


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def locate_source_dir(path: Path) -> Path:
    if (path / "image-child-proof.json").is_file():
        return path
    candidates = sorted(path.glob("fixtures/*/archive-sources"))
    if len(candidates) != 1:
        raise AssertionError(f"expected one archive-sources self-test output, found {len(candidates)}")
    return candidates[0].resolve(strict=True)


def read_nested_gif(root: Path) -> tuple[bytes, str]:
    root_bytes = root.read_bytes()
    with zipfile.ZipFile(io.BytesIO(root_bytes)) as archive:
        inner_bytes = archive.read("inner.zip")
    with zipfile.ZipFile(io.BytesIO(inner_bytes)) as archive:
        gif_bytes = archive.read("frames.gif")
    return root_bytes, sha256(gif_bytes)


def proof_frames(frames: list[dict]) -> list[dict]:
    return [
        {
            "number": index,
            "width": frame["width"],
            "height": frame["height"],
            "pixelSha256": frame["pixelSha256"].upper(),
        }
        for index, frame in enumerate(frames, 1)
    ]


def expected_frames(frames: list[dict]) -> list[dict]:
    result = []
    for index, frame in enumerate(frames, 1):
        pixels = bytes.fromhex(frame["bgraHex"])
        digest = sha256(pixels)
        if digest != frame["pixelSha256"].upper():
            raise AssertionError(f"fixture frame {index} pixel hash is inconsistent")
        if len(pixels) != frame["width"] * frame["height"] * 4:
            raise AssertionError(f"fixture frame {index} has an unexpected byte count")
        result.append({
            "number": index,
            "width": frame["width"],
            "height": frame["height"],
            "pixelSha256": digest,
        })
    return result


def main() -> None:
    source_dir = locate_source_dir(Path(sys.argv[1]).resolve(strict=True))
    expectations_path = Path(sys.argv[2]).resolve(strict=True)
    proof = json.loads((source_dir / "image-child-proof.json").read_text(encoding="utf-8"))
    expectations = json.loads(expectations_path.read_text(encoding="utf-8"))["assets"]
    roots = {
        "left": source_dir / "left.zip",
        "right": source_dir / "right.zip",
    }
    files = {}
    for side, root in roots.items():
        root_bytes, image_sha = read_nested_gif(root)
        expected = expectations[f"same-first-{side}.gif"]
        if sha256(root_bytes) != proof[f"{side}RootSha256"]:
            raise AssertionError(f"{side} root ZIP SHA differs from the GUI proof")
        if image_sha != expected["fileSha256"] or image_sha != proof[f"{side}ImageSha256"]:
            raise AssertionError(f"{side} nested GIF bytes differ from the fixed fixture")
        if proof[f"{side}EntryChain"] != ["inner.zip"] or proof[f"{side}LeafEntry"] != "frames.gif":
            raise AssertionError(f"{side} typed archive input has the wrong chain or leaf")
        expected_pixels = expected_frames(expected["frames"])
        actual_pixels = proof_frames(proof[f"{side}Frames"])
        if actual_pixels != expected_pixels:
            raise AssertionError(f"{side} decoded full-frame pixel hashes differ from the independent fixture")
        files[side] = {"rootSha256": sha256(root_bytes), "imageSha256": image_sha, "frames": actual_pixels}

    if proof["mode"] != "Image" or proof["selectedMode"] != "Auto" or proof["selectedModeIndex"] != 0:
        raise AssertionError("the fixture was not opened through automatic image detection")
    if not proof["leftReadOnly"] or not proof["rightReadOnly"]:
        raise AssertionError("archive image child was not read-only")

    workspace = json.loads((source_dir / "image-child-workspace.json").read_text(encoding="utf-8"))
    matches = [entry for entry in workspace["entries"] if entry.get("mode") == "Image"]
    if len(matches) != 1:
        raise AssertionError("workspace must contain exactly one archived image child")
    image_entry = matches[0]
    for side in ("left", "right"):
        archive_input = image_entry[f"{side}ArchiveInput"]
        if archive_input["rootSha256"] != proof[f"{side}RootSha256"]:
            raise AssertionError(f"workspace {side} root SHA changed")
        if archive_input["entryChain"] != ["inner.zip"] or archive_input["leafEntry"] != "frames.gif":
            raise AssertionError(f"workspace {side} chain or leaf changed")
        if not archive_input["inheritedReadOnly"]:
            raise AssertionError(f"workspace {side} input lost fixed read-only state")
    if not image_entry["leftReadOnly"] or not image_entry["rightReadOnly"]:
        raise AssertionError("workspace image child is not read-only")

    print(json.dumps({"status": "passed", "roots": files}, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
