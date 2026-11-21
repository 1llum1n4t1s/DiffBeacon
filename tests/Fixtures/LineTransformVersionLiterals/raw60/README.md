# VersionLiteral Native constructor external draft

This external draft captures 60 real Native constructor/Evaluate observations, plus 12 separate static proposals, defined-domain analyses and exclusions. Fixed source commit: `6780924ba35f7f55af0f6f14dd302561175830a6`. Shared reference ZIP SHA256: `18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7`. No repository mutation, Core/runtime dependency, release version, external service or credential change occurs.

## Original contract and observed domain

Original Nodes.cpp lines 4563–4568 initializes all four int components to zero, calls `sscanf_s` with `%d.%d.%d.%d`, then adds signed int64 shifts48/32/16 and revision. Its original inline Evaluate returns the int64 arm. Constructor bytes, VersionLiteral declaration and ExprNode base bytes are extracted unchanged from the fixed original source. `extractions.json` retains byte offsets, original lines and SHA; `source-manifest.json` retains source size, SHA and Git blob identity; original GPL-2.0 license is retained.

| Coverage | Qualification |
| --- | --- |
| Empty/missing fields, partial conversion, trailing text, extra components, ASCII whitespace/signs, leading zeroes, backslash and embedded NUL | Direct original constructor/Evaluate only |
| Nonoverlap positive 0/65534/65535, major32767 and INT64_MAX | Safe real native observations |
| Positive65536 overlaps and selected int maximum minor/build/revision | Safe added-term observations, including overlap addition rather than bitwise OR |
| Negative revision including INT_MIN, negative zero in shifted components | Int-representable and safe int64 addition; no negative nonzero shifted component executed |
| Negative shifted components, int parse overflow, sign-bit major shift, signed sum overflow | Explicitly excluded; no native golden |
| NonASCII/locale, native lexer/parser/directives, whole AST/Optimize/type equivalence | Unexecuted/unqualified |
| Proposed `v"..."` public expressions | Native raw-value join only, planned/unexecuted |

Base safe domain is major0..32767 and remaining0..65535. Extra overlaps only use selected small surrounding terms whose total remains representable. Negative revision does not shift; int minimum is representable and is added to nonnegative safe terms. `failure-list.json` was recorded before adapter/producer implementation. All 60 numerical golden strings originate only in actual native stdout. The producer performs no golden-number packing calculation.

Adapter boundaries: original ValueType's unused arms are omitted in a verification-only variant, preserving the int64 index3; FilterEvalContext is an empty stub ignored by original Evaluate. Hex-input decoding and decimal JSON output are adapter transport. The original ToStringValue serializer is not executed, no old dedicated Version Optimize or current long folding/type/AST equivalence is claimed. There is no Poco/shared-source modified licensed stub in this minimal adapter. Native lexer lower-case token, parser and directives are static parent evidence only and are not reclassified as runtime coverage.

## Independent verification

`python -B <draft>/verify.py --reference-zip C:/Users/IMT/dev/DiffBeacon/tests/Fixtures/LineTransformReplace/reference-source.zip`

The independent reader imports no producer and computes no expected packed numbers. It checks exact draft manifest contents; pinned ZIP SHA and CRC; original source blobs, byte spans and license; ZIP entry sizes/CRCs/SHA; two original raw stdout byte streams and fixed golden bytes; input IDs/hex/int64 type; safe scanf-domain component bounds; actual OS-held native PID/birth/signaled/exit and bothEOF/noTimeout receipts; direct cl.exe receipts; captured executable SHA. Public planned records join native stdout strings without substituting arithmetic values.

`python -B <draft>/reproduce.py --output <fresh-user-Temp/Codex/DiffBeacon/version-recapture-unique> --reference-zip <fixed-reference-source.zip> --compiler <existing-Hostx64/x64/cl.exe> --vcvars <existing-vcvars64.bat>`

Capture and reproduce require fresh explicit OS userTemp outputs and never overwrite existing paths. The compiler is capture-only; no MSVC runtime is introduced to product paths. Two newly compiled executables are run in new child directories and independently compared with fixed raw observations. Timeout is a failure and does not trigger process kill or automatic deletion.

## Retention and scope

Keep this minimal standalone draft and proof ZIP for parent review/reproduction. Original source archive remains the fixed shared repository input. All temporary producer scripts, failed first extraction files and expanded executable runs are external and await parent exact cleanup GO. Cleanup uses the common Windows trash workflow only; deletion refusal is not bypassed. This draft may be removed after acceptance/supersession, with prior paths/receipt SHA recorded by the parent. Repo working tree and canonical fixtures were not edited. Native parser, actual public serialized bridge, .NET build, AOT and app E2E were not run: there is no product code change in this task.
