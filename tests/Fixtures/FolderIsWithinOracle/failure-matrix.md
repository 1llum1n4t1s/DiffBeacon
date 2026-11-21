# IsWithin: implementation-before-capture failure matrix

Written and reviewed before adapter or native capture. The fixed native
IsWithinFunc body is inspected directly; InRange output is not an oracle here.
Typed cases are fixed before adapter implementation.

| Failure class | Expected native boundary / capture |
| --- | --- |
| Closed interval | lower and upper inclusive; singleton range matches exactly; reversed bounds false. Integer endpoint comparisons retain int64 |
| Mixed numeric precision | int64/int64 comparison exact; on each mixed edge convert the int64 operand to binary64; double value converts integer bounds. All eight numeric type combinations at 2^53 and int64 endpoints |
| IEEE inputs | +/-zero, subnormal, finite maximum, infinity and quiet NaN retain double typed inputs. NaN comparisons false; both infinity endpoints inclusive |
| Timestamp | only Timestamp/Timestamp/Timestamp compares using fixed Poco class/value/copy/assignment/destructor/comparison definitions; integer lookalikes rejected |
| String | all-string only; cs uses unsigned UTF-8 byte order; ci uses pinned Poco ASCII fold and captured char ABI, not Unicode folding. Highbit bytes, NUL, empty strings, equality and reversed intervals |
| Coercion | none/bool/regex/file and incompatible scalar types return None, not false; no numeric/string/Timestamp coercion |
| Arrays | only first/value argument shallow maps. Empty array stays empty; nested array elements become None. Boundary arrays do not map |
| Arity | exactly three; null/0/1/2/4 arguments reject before evaluation, including injected throwing nodes |
| Evaluation / errors | lower then upper then value, each once. A throwing lower stops upper/value; throwing upper stops value. Incompatible bounds and empty value array do not suppress argument evaluation |

Evidence: pinned WinMerge commit 6780924ba35f7f55af0f6f14dd302561175830a6;
unchanged IsWithinFunc, shallow helper, registration and arity validation;
fixed Poco definitions; typed literals and throwing-node spies; two independent
MSVC x64 compile/process captures; raw streams, real process identity/completion,
EOF and SHA receipts; a self-contained independent Python reader.

Limits: only extracted function/arity code executes in adapter AST bindings.
The complete FunctionNode constructor, parser, optimizer, folder loader/scanner,
public CLI, real file context, shared budgets and managed cancellation/output
protection do not execute. Injected throw diagnostics are adapter evidence only.
Original literal/parser expressibility is not claimed. This fixture alone adds
zero verified folder public CLI compatibility cases. InRange and line-transform
fixtures are reference sources, not substitutes for IsWithin native output.
Other compiler/OS/char ABIs remain unverified.
