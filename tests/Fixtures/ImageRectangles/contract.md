# Rectangle kernel failure-first contract

Before implementing the adapter, enumerate failures:
- DeleteRectangle invalid pane/read-only must return false without history/compare. Only valid half-open rectangles inside the image are executable; original has no rectangle bounds validation.
- Valid delete zeros all four BGRA bytes exactly inside [left,right) x [top,bottom). Empty valid rectangle still records history/compare.
- Paste uses memcpy, including RGB under zero alpha; no compositing. Negative coordinates and partial edge overlap clip source/target. Fully left/top no overlap leaves bytes unchanged but outer PasteImage still records history/compare.
- Empty source and empty target leave bytes unchanged but valid-pane outer PasteImage records history/compare. Identical paste likewise records history/compare.
- PasteImage has NO read-only guard: direct kernel paste on read-only pane still writes. GUI command read-only enforcement is a separate boundary.
- Positive fully right/bottom coordinates are excluded: original clamp to width-1/height-1 can create negative source indices, including empty source. Do not run or fabricate expected outputs for original undefined behavior. Invalid delete rectangles, arithmetic overflow, aliased source are also excluded.

Adapter boundary: minimal Image stores supplied raw BGRA in logical top-to-bottom contiguous rows; scanLine returns logical row addresses, unsigned dimensions. TemporaryTransformation only toggles/asserts the transformed flag; orientation is identity. Undo push owns snapshots and increments a count; CompareImages increments a count. These are explicitly shims, not the original FreeImage/orientation/history/diff implementations. Exact extracted source functions are compiled unmodified. No GUI clipboard, floating paste resize/move, selection coordinate mapping, cursor, focus, save, Undo/Redo, or original orientation behavior is verified.

Custom inputs are CC0-1.0. Extracted source functions and generated probe containing them retain upstream GPL license; repository reference-source/LICENSE.txt is preserved if present. Probe output is the original function result on this declared substrate, never a C#-constructed golden. Two separate process runs compare every output byte, and limited independent invariants verify source/target retention and obvious half-open/raw-copy results.
