# Binary Copy All fixed oracle

Self-authored fixed inputs are `0001FF`, `0A0B0C0D80`, and `1415161718191A`. No external binary fixture or license is required. `verify.py` uses only Python standard library; expected destination bytes are the literal source followed by the destination suffix after source length. The actual GUI captures all six three-side and both two-side directions, saves them, and publishes archive working state. The reader checks exact saved bytes, original inputs, ZIP CRC/entry bytes, relative workspace assets and packaged assets independently.

Run `python -B -X utf8 tests/Fixtures/BinaryCopyAll/verify.py <gui-output> [<e2e-work>]`. The E2E runner captures stdout/stderr and records exit/assertions.
