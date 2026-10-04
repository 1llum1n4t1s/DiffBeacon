"""ZIPに残らない空directory・linkを含む、upload直前の実体を記録する。"""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import stat
import subprocess


def capture(repository, roots):
    entries = []
    missing = []

    def visit(path):
        metadata = path.lstat()
        row = {"path": path.relative_to(repository).as_posix()}
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
            missing.append(root.relative_to(repository).as_posix())
        else:
            visit(root)
    return entries, missing


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", action="append", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    repository = pathlib.Path(__file__).resolve().parent.parent
    roots = []
    for value in args.root:
        relative = pathlib.PurePath(value)
        if relative.anchor or ".." in relative.parts:
            raise ValueError("Evidence roots must be repository-relative paths without parent traversal")
        root = repository / relative
        # 親link経由の別領域は走査しない。root自身のlinkは実体として記録する。
        for ancestor in root.parents:
            if ancestor == repository:
                break
            if ancestor.is_symlink() or ancestor.is_junction():
                raise ValueError(f"Linked evidence parent: {ancestor}")
        if any(root == previous or root in previous.parents or previous in root.parents for previous in roots):
            raise ValueError("Evidence roots must not overlap")
        roots.append(root)
    output_relative = pathlib.PurePath(args.output)
    if output_relative.anchor or ".." in output_relative.parts:
        raise ValueError("Inventory output must be repository-relative without parent traversal")
    output = repository / output_relative
    for ancestor in output.parents:
        if ancestor == repository:
            break
        if ancestor.is_symlink() or ancestor.is_junction():
            raise ValueError(f"Linked inventory parent: {ancestor}")
    if any(output == root or root in output.parents for root in roots):
        raise ValueError("Inventory output must be outside captured roots")
    if os.path.lexists(output):
        raise FileExistsError("Fresh inventory output required")
    entries, missing = capture(repository, roots)
    receipt = {
        "schema": 1,
        "observedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip(),
        "roots": [root.relative_to(repository).as_posix() for root in roots],
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
