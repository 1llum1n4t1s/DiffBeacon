"""自作lossless WebPの再生成。SPDX-License-Identifier: CC0-1.0"""
import hashlib
import json
from pathlib import Path
from PIL import Image, features
import PIL

ROOT = Path(__file__).resolve().parent
R, G, B, T = (255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (0, 0, 0, 0)
DEFINITIONS = {
    "lossless-left.webp": [[R] * 6, [G] * 6],
    "lossless-right.webp": [[R] * 6, [B] * 6],
    "lossless-transparent.webp": [[R] * 6, [R, G, R, R, R, R], [T, T, T, T, T, B], [T, T, T, G, T, T]],
}
expected = json.loads((ROOT / "expectations.json").read_text(encoding="utf-8"))
for name, rgba in DEFINITIONS.items():
    images = [Image.frombytes("RGBA", (3, 2), bytes(v for pixel in frame for v in pixel)) for frame in rgba]
    images[0].save(ROOT / name, save_all=True, append_images=images[1:], lossless=True, exact=True, duration=100, loop=0)
    frames = []
    for frame in rgba:
        bgra = bytes(v for r, g, b, a in frame for v in (b, g, r, a))
        frames.append({"width": 3, "height": 2, "rgba": [list(p) for p in frame],
                       "bgraHex": bgra.hex(), "pixelSha256": hashlib.sha256(bgra).hexdigest().upper()})
    expected["assets"][name] = {"fileSha256": hashlib.sha256((ROOT / name).read_bytes()).hexdigest().upper(), "frames": frames}
    # 再生成時だけ別デコーダーでも元の固定RGBAを照合する。期待値は復号結果から作らない。
    with Image.open(ROOT / name) as encoded:
        assert encoded.n_frames == len(rgba)
        for index, frame in enumerate(rgba):
            encoded.seek(index)
            assert encoded.convert("RGBA").tobytes() == bytes(v for p in frame for v in p)
expected["webpGenerator"] = {"pillow": PIL.__version__, "libwebp": features.version("webp"),
                             "options": {"lossless": True, "exact": True, "duration": 100, "loop": 0}}
(ROOT / "expectations.json").write_text(json.dumps(expected, indent=2) + "\n", encoding="utf-8")
print(f"Generated 3 WebP fixtures with Pillow {PIL.__version__}, libwebp {features.version('webp')}")
