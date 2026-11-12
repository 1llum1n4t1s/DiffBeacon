# InRange: implementation-before-capture failure matrix

This document is written and reviewed before cases, adapter, or golden capture.
All results below concern the fixed native function, not folder `.flt` execution.

| Failure class | Expected native boundary / capture |
| --- | --- |
| Half-open interval | lower inclusive, upper exclusive; equal/reversed bounds false; integer limits remain int64 |
| Mixed numeric precision | int64/int64 comparisons exact; each mixed comparison converts only its int64 operand to binary64; double value converts both integer bounds. Probe all eight type combinations around 2^53 and int64 endpoints |
| IEEE exceptional inputs | +/-zero, subnormal, infinities and quiet NaN remain double typed inputs; NaN comparison false, infinity follows half-open comparisons |
| Timestamp | only all-Timestamp triples compare, using fixed Poco value/copy/destructor/comparison definitions; integer lookalikes rejected as none |
| String | only all-string triples compare; cs uses std::string UTF-8 bytes, ci uses fixed Poco ASCII folding, not Unicode folding; embedded NUL and non-ASCII distinguish boundaries |
| Coercion | none/bool/regex/file and heterogeneous bounds return none, not false; no string/numeric/Timestamp coercion |
| Arrays | only value argument shallow-maps; nested arrays become none; array bounds rejected for each element; empty value array remains empty |
| Arity | original registration requires exactly three; null/0/1/2/4 arguments throw before any argument evaluation |
| Evaluation / errors | lower then upper then value, once each; throwing lower prevents upper/value; throwing upper prevents value; bad bounds do not skip value evaluation; empty value arrays still evaluate bounds |

Evidence: unchanged InRangeFunc, shallow helper, registration and constructor arity validation extracted from the pinned WinMerge Git blob; pinned Poco sources; typed literals/throwing spies; two separately compiled MSVC x64 executables and separately spawned processes with held-handle completion, stdout/stderr EOF, identities, SHA receipts; independent Python semantic reader.

Limits: the spy is an adapter AST with original function/arity code, not the original complete FunctionNode constructor, parser, Optimize, folder loader/scanner, or public CLI. Original literal/parser expressibility is not claimed. No public folder CLI case is verified by this oracle alone. LineTransformRanges evidence describes a separate serialized line-transform boundary and contributes zero folder public-compatibility cases. Native exceptions only describe injected throwing nodes and original arity errors; parser/loader diagnostics and managed cancellation/budget/output protection remain outside this fixture.
