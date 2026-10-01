"""自作 fixture の再生成。Python 標準ライブラリのみ。SPDX-License-Identifier: CC0-1.0"""
import hashlib
import json
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PALETTE = [(0, 0, 0, 0), (255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255)]
assets = {}

def pixels(width, height, rgba):
    bgra = bytes(v for r, g, b, a in rgba for v in (b, g, r, a))
    return {"width": width, "height": height, "rgba": [list(p) for p in rgba],
            "bgraHex": bgra.hex(), "pixelSha256": hashlib.sha256(bgra).hexdigest().upper()}

def save(name, data, frames):
    (ROOT / name).write_bytes(data)
    assets[name] = {"fileSha256": hashlib.sha256(data).hexdigest().upper(), "frames": frames}

def png(name, width, height, rgba):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))
    rows = b"".join(b"\0" + bytes(v for p in rgba[y*width:(y+1)*width] for v in p) for y in range(height))
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    data += chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    save(name, data, [pixels(width, height, rgba)])

def gif(name, width, height, updates, expected):
    data = bytearray(b"GIF89a" + struct.pack("<HHBBB", width, height, 0xF2, 0, 0))
    data.extend(bytes(v for p in PALETTE for v in p[:3]) + bytes(12))
    for x, y, w, h, indices, disposal in updates:
        data.extend(b"\x21\xF9\x04" + bytes([(disposal << 2) | 1, 0, 0, 0, 0]))
        data.extend(b"\x2C" + struct.pack("<HHHHB", x, y, w, h, 0))
        # 毎画素 clear code に戻すため code 幅は常に4bit。辞書拡張に依存しない。
        codes = [v for index in indices for v in (8, index)] + [9]
        packed = bytes(codes[i] | ((codes[i+1] if i+1 < len(codes) else 0) << 4) for i in range(0, len(codes), 2))
        data.append(3)
        for offset in range(0, len(packed), 255):
            block = packed[offset:offset+255]
            data.extend(bytes([len(block)]) + block)
        data.append(0)
    data.append(0x3B)
    save(name, bytes(data), [pixels(width, height, [PALETTE[i] for i in frame]) for frame in expected])

red = [1] * 6
green = [2] * 6
gif("same-first-left.gif", 3, 2, [(0, 0, 3, 2, red, 1), (0, 0, 3, 2, green, 1)], [red, green])
gif("same-first-right.gif", 3, 2, [(0, 0, 3, 2, red, 1), (0, 0, 3, 2, red, 1)], [red, red])
gif("short.gif", 3, 2, [(0, 0, 3, 2, red, 1)], [red])
gif("disposal.gif", 3, 2, [(0, 0, 3, 2, red, 1), (1, 0, 1, 1, [2], 2),
    (2, 1, 1, 1, [3], 3), (0, 1, 1, 1, [2], 1)],
    [red, [1, 2, 1, 1, 1, 1], [1, 0, 1, 1, 1, 3], [1, 0, 1, 2, 1, 1]])
# PNG 側の期待合成画像は GIF の記録を参照せず、別の完全 RGBA 定義で作る。
R = (255, 0, 0, 255)
G = (0, 255, 0, 255)
B = (0, 0, 255, 255)
T = (0, 0, 0, 0)
for i, rgba in enumerate([[R, R, R, R, R, R], [R, G, R, R, R, R],
                         [R, T, R, R, R, B], [R, T, R, G, R, R]], 1):
    png(f"disposal-{i}.png", 3, 2, rgba)
gif("transparent-update.gif", 3, 2, [(0, 0, 3, 2, red, 1), (0, 0, 3, 1, [0, 2, 0], 1)],
    [red, [1, 2, 1, 1, 1, 1]])
png("red.png", 3, 2, [PALETTE[1]] * 6)
png("green.png", 3, 2, [PALETTE[2]] * 6)
png("red-wide.png", 4, 2, [PALETTE[1]] * 8)
png("alpha-opaque.png", 1, 1, [(255, 0, 0, 255)])
png("alpha-half.png", 1, 1, [(255, 0, 0, 128)])
png("threshold-100.png", 1, 1, [(100, 0, 0, 255)])
png("threshold-105.png", 1, 1, [(105, 0, 0, 255)])
save("broken.gif", b"not a GIF", [])
save("truncated.gif", (ROOT / "disposal.gif").read_bytes()[:23], [])
# 宣言寸法のみ巨大化。圧縮画素の展開・巨大な入力の保存は不要。
huge = bytearray((ROOT / "short.gif").read_bytes())
huge[6:10] = struct.pack("<HH", 4097, 4096)
save("over-pixel-limit.gif", bytes(huge), [])
gif("over-frame-limit.gif", 1, 1, [(0, 0, 1, 1, [1], 1)] * 1025, [])
# 17小領域ページの canvas による最悪作業量は256Mを超える。
gif("over-work-limit.gif", 4000, 4000, [(0, 0, 1, 1, [1], 1)] * 17, [])
# 各画像は小さいが、左右の最大幅と最大高さを組み合わせると上限を超える。
gif("canvas-wide.gif", 10000, 1, [(0, 0, 1, 1, [1], 1)], [])
gif("canvas-tall.gif", 1, 10000, [(0, 0, 1, 1, [1], 1)], [])
(ROOT / "expectations.json").write_text(json.dumps({"format": 1, "pixelFormat": "BGRA8888 Unpremul tightly packed", "assets": assets}, indent=2) + "\n", encoding="utf-8")
