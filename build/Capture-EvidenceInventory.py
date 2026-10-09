"""ZIPに残らない空directory・linkを含む、upload直前の実体を記録する。"""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import stat
import subprocess


def capture(base, roots):
    entries = []
    missing = []

    def visit(path):
        metadata = path.lstat()
        row = {"path": path.relative_to(base).as_posix(), "mtimeNs": metadata.st_mtime_ns}
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & 0x400:
            row.update(kind="link", target=os.readlink(path))
        elif stat.S_ISDIR(metadata.st_mode):
            children = sorted(path.iterdir(), key=lambda child: child.name)
            row.update(kind="directory", empty=len(children) == 0)
            entries.append(row)
            for child in children:
                visit(child)
            return
        elif stat.S_ISREG(metadata.st_mode):
            digest = hashlib.sha256()
            length = 0
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
                    length += len(block)
            after = path.lstat()
            if (metadata.st_size, metadata.st_mtime_ns, metadata.st_ino) != (after.st_size, after.st_mtime_ns, after.st_ino) or length != metadata.st_size:
                raise RuntimeError(f"Evidence changed during inventory: {path}")
            row.update(kind="file", bytes=length, sha256=digest.hexdigest())
        else:
            raise RuntimeError(f"Unsupported evidence entry: {path}")
        entries.append(row)

    for root in roots:
        if not os.path.lexists(root):
            missing.append(root.relative_to(base).as_posix())
        else:
            visit(root)
    return entries, missing


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-directory", help="Absolute evidence base directory; defaults to the repository")
    parser.add_argument("--root", action="append", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    repository = pathlib.Path(__file__).resolve().parent.parent
    base = pathlib.Path(args.base_directory) if args.base_directory is not None else repository
    if not base.is_absolute() or ".." in base.parts:
        raise ValueError("Evidence base must be absolute without parent traversal")
    # 外部baseも全祖先を検査し、link経由で別領域を読み書きしない。
    for ancestor in (base, *base.parents):
        if ancestor.is_symlink() or ancestor.is_junction():
            raise ValueError(f"Linked evidence base or parent: {ancestor}")
    if base.exists() and not base.is_dir():
        raise ValueError("Evidence base must be a directory")
    roots = []
    for value in args.root:
        relative = pathlib.PurePath(value)
        if relative.anchor or ".." in relative.parts or not relative.parts:
            raise ValueError("Evidence roots must be base-relative paths without parent traversal")
        root = base / relative
        # 親link経由の別領域は走査しない。root自身のlinkは実体として記録する。
        for ancestor in root.parents:
            if ancestor == base:
                break
            if ancestor.is_symlink() or ancestor.is_junction():
                raise ValueError(f"Linked evidence parent: {ancestor}")
        if any(root == previous or root in previous.parents or previous in root.parents for previous in roots):
            raise ValueError("Evidence roots must not overlap")
        roots.append(root)
    output_relative = pathlib.PurePath(args.output)
    if output_relative.anchor or ".." in output_relative.parts or not output_relative.parts:
        raise ValueError("Inventory output must be base-relative without parent traversal")
    output = base / output_relative
    for ancestor in output.parents:
        if ancestor == base:
            break
        if ancestor.is_symlink() or ancestor.is_junction():
            raise ValueError(f"Linked inventory parent: {ancestor}")
    if any(output == root or root in output.parents for root in roots):
        raise ValueError("Inventory output must be outside captured roots")
    if os.path.lexists(output):
        raise FileExistsError("Fresh inventory output required")
    entries, missing = capture(base, roots)
    receipt = {
        "schema": 1,
        "observedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip(),
        "evidenceBase": str(base),
        "roots": [root.relative_to(base).as_posix() for root in roots],
        "missingRoots": missing,
        "entries": entries,
        "scope": "Physical evidence immediately before upload; links recorded without following; absent roots retained for failed runs",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(receipt, stream, ensure_ascii=False, indent=2)
    print(json.dumps({"entries": len(entries), "emptyDirectories": sum(row.get("empty", False) for row in entries), "missingRoots": missing}))


if __name__ == "__main__":
    main()
