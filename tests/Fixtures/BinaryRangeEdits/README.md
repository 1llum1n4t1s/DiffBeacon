# Binary range edit independent oracle

Self-authored fixed literals in verify.py (Python stdlib only; no imported algorithm).
Inputs 0001FF / 0A0B0C0D80 / 1415161718191A, insert AABB at1, overwrite CC at1, delete count1 at2.
Expected 00CC01FF / 0ACC0B0C0D80 / 14CC15161718191A. No externally copied fixture.
Real application writes original inputs, saved bytes, workspace assets and package during self-test. Reader compares every byte, SHA and ZIP CRC; no application implementation is used as oracle.
Run: python -B -X utf8 tests/Fixtures/BinaryRangeEdits/verify.py <self-test-output> [e2e-work].
Source contract: WinMerge/frhed f952092530cc16e2f8832fc15dc6ba4b13cbc032 FRHED/PasteDlg.cpp (overwrite EOF rejection), DeleteDlg.cpp (inclusive end converted to start/count).
https://github.com/WinMerge/frhed/tree/f952092530cc16e2f8832fc15dc6ba4b13cbc032/FRHED
Native save dialog/direct nibble/real OS clipboard parity is outside this slice.

Shared64MiB with existing Redo: fixed16MiB zeros, overwrite first12MiB with11 then22 (48MiB retained history), overwrite first8MiB with33 (64MiB), Undo then attempt full16MiB44 (80MiB refusal). Real loaded BinaryPanel uses the same Core operation as accepted dialogs; the32MiB Hex text is not laid out in a dialog. Reader independently checks original allzeros, before12MiB22+4MiB00, preserved Redo8MiB33+4MiB22+4MiB00, every byte. Saved point/revision/stamp/Redo preservation is recorded by real panel assertions.
