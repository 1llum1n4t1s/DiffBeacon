"""検証データを全画素・全ログ保持のまま圧縮し、各ファイルのSHAを照合する。削除はしない。"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import zipfile


def inventory(root):
    files, directories = [], []
    for base, names, leaves in os.walk(root, followlinks=False):
        for name in sorted(names + leaves):
            path = Path(base, name)
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise ValueError(f"リンクを含む証拠は圧縮しません: {path}")
            relative = path.relative_to(root).as_posix()
            record = dict(path=relative, bytes=info.st_size, mtimeNs=info.st_mtime_ns,
                          attributes=getattr(info, "st_file_attributes", 0), mode=info.st_mode)
            if stat.S_ISDIR(info.st_mode):
                # NTFSのディレクトリst_sizeは列挙により0/割当サイズが変わる。内容はentry一覧で照合する。
                record["bytes"] = 0
                directories.append(record)
            elif stat.S_ISREG(info.st_mode):
                files.append(record)
            else:
                raise ValueError(f"通常ファイル以外を含みます: {path}")
    return sorted(files, key=lambda item: item["path"]), sorted(directories, key=lambda item: item["path"])


def digest_file(path):
    with path.open("rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--verify-existing", action="store_true", help="途中ZIPを全entry再照合し、新規作成を省く")
    args = parser.parse_args()
    root = args.source.absolute()
    archive = args.archive.absolute()
    report = args.report.absolute()
    if not root.is_dir() or root.is_symlink() or archive.exists() != args.verify_existing or report.exists():
        raise ValueError("入力がディレクトリでないか、出力が既存です。")
    if root == archive or root in archive.parents or root == report or root in report.parents:
        raise ValueError("出力を入力内へ置けません。")
    marker = ".diffbeacon-retention-manifest.json"
    if args.verify_existing:
        with zipfile.ZipFile(archive, "r") as saved:
            manifest = json.loads(saved.read(marker))
        if manifest["source"] != str(root):
            raise ValueError("既存ZIPの元ディレクトリが一致しません。")
        files = manifest["files"]
        directories = [dict(item, bytes=0) for item in manifest["directories"]]
        if any(Path(item["path"]).is_absolute() or ".." in Path(item["path"]).parts for item in files + directories):
            raise ValueError("既存manifestにルート外のpathがあります。")
    else:
        files, directories = inventory(root)
        if any(item["path"] == marker for item in files + directories):
            raise ValueError("予約したmanifest名が入力に存在します。")
        print(json.dumps(dict(phase="compress", source=str(root), files=len(files)), ensure_ascii=False), flush=True)
        with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED,
                             compresslevel=1, allowZip64=True) as output:
            for item in directories:
                output.writestr(item["path"] + "/", b"")
            for item in files:
                path = root / item["path"]
                digest = hashlib.sha256()
                # ZIP64を先に指定し、単一巨大ファイルでも途中失敗しない。
                info = zipfile.ZipInfo.from_file(path, item["path"], strict_timestamps=False)
                info.compress_type = zipfile.ZIP_DEFLATED
                info._compresslevel = 1
                with path.open("rb") as source, output.open(info, "w", force_zip64=True) as destination:
                    while block := source.read(1024 * 1024):
                        digest.update(block)
                        destination.write(block)
                item["sha256"] = digest.hexdigest()
            manifest = dict(source=str(root), files=files, directories=directories,
                            restoration="Extract ZIP into source; manifest records source attributes, modes and timestamps.")
            output.writestr(marker, json.dumps(manifest, ensure_ascii=False).encode("utf-8"))
    print(json.dumps(dict(phase="verify", source=str(root)), ensure_ascii=False), flush=True)
    with zipfile.ZipFile(archive, "r") as saved:
        expected = {item["path"] for item in files} | {item["path"] + "/" for item in directories} | {marker}
        if len(saved.infolist()) != len(expected) or set(saved.namelist()) != expected:
            raise ValueError("ZIPのentry一覧が入力と一致しません。")
        if json.loads(saved.read(marker)) != manifest:
            raise ValueError("manifestが一致しません。")
        for item in files:
            entry = saved.getinfo(item["path"])
            with saved.open(entry) as handle:
                digest = hashlib.file_digest(handle, "sha256").hexdigest()
            if entry.file_size != item["bytes"] or digest != item["sha256"]:
                raise ValueError(f"ZIPのSHA/サイズ不一致: {item['path']}")
    # 名前・サイズ・mtime・属性を再列挙し、実行中に変更された元データは除去しない。
    current_files, current_directories = inventory(root)
    before_files = [{key: value for key, value in item.items() if key != "sha256"} for item in files]
    if current_files != before_files or current_directories != directories:
        raise ValueError("圧縮中に入力が変更されました。元ディレクトリを保持します。")
    result = dict(source=str(root), archive=str(archive), files=len(files), directories=len(directories),
                  sourceBytes=sum(item["bytes"] for item in files), archiveBytes=archive.stat().st_size,
                  archiveSha256=digest_file(archive), verifiedFiles=len(files), sourceUnchanged=True,
                  allFileHashesVerified=True, sourceRemoved=False)
    with report.open("x", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=2)
    print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
