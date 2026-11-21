"""実アプリの生成物をPython標準ライブラリで独立に検証する。"""
import hashlib
import json
import platform
import sys
import tarfile


def read_entries(path):
    entries = []
    with tarfile.open(path, "r:*", encoding="utf-8", errors="strict") as archive:
        for entry in archive:
            if entry.isdir():
                entries.append({"path": entry.name.rstrip("/"), "directory": True, "size": 0, "sha256": ""})
            elif entry.isfile():
                digest = hashlib.sha256()
                size = 0
                with archive.extractfile(entry) as content:
                    while chunk := content.read(1024 * 1024):
                        digest.update(chunk)
                        size += len(chunk)
                entries.append({"path": entry.name, "directory": False, "size": size, "sha256": digest.hexdigest().upper()})
            else:
                raise ValueError("Unexpected special entry in verification fixture")
    return entries


if sys.argv[1] == "create-root":
    with tarfile.open(sys.argv[2], "x", format=tarfile.PAX_FORMAT) as archive:
        archive.add(sys.argv[3], arcname=".")
elif sys.argv[1] != "read":
    raise ValueError("Unknown verification operation")

print(json.dumps({"python": sys.version, "platform": platform.platform(), "entries": read_entries(sys.argv[2])}, ensure_ascii=True))
