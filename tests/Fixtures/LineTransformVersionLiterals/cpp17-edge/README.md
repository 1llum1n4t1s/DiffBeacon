# VersionLiteral C++17 sign-bit edge supplement

This new external supplement preserves the immutable `../draft` raw60 and sibling serializer/quote128 evidence. It captures18 additional MSVC C++17 Version constructor/Evaluate, original ToStringValue int64 serialized-key and original direct UnescapeQuotes pairs,36 records per actual Native run. The producer computes no numerical golden. Values and serialized UTF8 come only from original Native stdout.

## C++ standard and source evidence

Current readonly `Src/Merge.vcxproj` SHA256 is `5dd1ec8efee4d6a4a1ac093093e6ae15af3803c1b386c81d35fe0feb61819186`, with12 LanguageStandard settings `stdcpp17` at lines317/378/435/491/547/600/653/703/752/801/850/898. Original current bytes and line evidence are retained separately. The fixed shared reference ZIP contains no `Src/Merge.vcxproj`; the current project evidence is explicitly not fixed-ZIP project evidence.

N4659 [expr.shift] paragraph2 permits a nonnegative signed left shift when the shifted mathematical value fits the corresponding unsigned type, followed by conversion to signed result. Thus major32768..65535 shifted48 is not excluded solely for setting the signed sign bit. [Primary shift rule](https://timsong-cpp.github.io/cppwp/n4659/expr.shift#2)

The resulting out-of-range conversion to signed is implementation-defined in C++17. Captured negative values and serialized keys are MSVC C++17 observations, not universally portable golden arithmetic. [Primary integral-conversion rule](https://timsong-cpp.github.io/cppwp/n4659/conv.integral#3)

`SourceCppStandardEvidence.json` records both primary citations, retained current project bytes/hash, explicit fixed-ZIP project absence and `/std:c++17`. Direct cl.exe response files and receipts independently confirm the actual capture standard. The verification adapter needs no C++20 transport replacement: its string/variant/hex transport uses C++17-compatible APIs. The8 existing quote probes are omitted in this edge-only adapter; all original method/declaration bytes remain unchanged.

## Captured and excluded scope

| Scope | Qualification |
| --- | --- |
| major32768/32769/65534/65535 with zero or positive16-bit tails | Original constructor/Evaluate and int64 ToStringValue captured |
| selected INTMAX minor/build/revision and all INTMAX tails | MSVC negative major base and left-associated signed additions remain representable; actual original observations |
| partial sscanf stopping, ASCII whitespace/plus flags and extra suffix | Direct constructor and quote-method observations |
| safe negative revision with major32769 or65535 | Representable signed additions captured |
| major32768 zero tail | Actual Native serialization is `-9223372036854775808` |
| major65536+, negative shifted components, int32 scan overflow, signed sum overflow/underflow including32768.0.0.-1 | Explicitly excluded, never executed, no golden |
| full Native AST/yylex/parser/directive/non-int64 serializer/typed context | Unexecuted/unqualified |

The safety reader checks original input components independently without generating packed expected numbers. For the tested MSVC sign-bit result, nonnegative int32 minor/build/revision tails have safe left-associated intermediates; tested negative revisions have major>=32769 and zero minor/build, leaving sufficient distance from INT64_MIN. Old historical raw60 sign-bit exclusions remain untouched; this new evidence supersedes only that overly narrow assumption for formal integration.

Fixed original source commit `6780924ba35f7f55af0f6f14dd302561175830a6`; reference ZIP SHA `18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7`. Existing exact source/lexer/header spans, additional fixed Git blobs and GPL-2.0 license are independently rechecked. Original Version constructor/Evaluate, ToStringValue and UnescapeQuotes use unchanged source bytes. Full original ValueType arm3 is int64. Unused Timestamp/regex/FileContent adapters retain their explicit rejecting stub boundary and int64 gateway; context/whole serializer equivalence is not claimed.

`edge-public-plan.json` joins18 actual Native integer/serialized/quote records. `formal-integration-proposal.json` retains a verbatim copy of the sibling59 quoted proposals plus NUL1 constructor-only classification and adds18 sign-bit proposals:77 quoted proposals plus1 rejected NUL. All remain planned/unexecuted. v/V candidates derive from unchanged static case-insensitive re2c flag/rule, not runtime yylex.

## Independent reader and actual regeneration

`python -B <edge>/verify.py --reference-zip <fixed-reference-source.zip> --raw60-dir <immutable-raw60> --supplement128-dir <immutable-supplement128> --current-project <repo>/Src/Merge.vcxproj`

The reader imports no producer and calculates no packed numerical expected values. It verifies both immutable parent manifests/proof ZIPs; source ZIP/blob/SHA/CRC/spans/license; retained current C++17 project bytes plus optional live readonly equality; explicit fixed-ZIP project absence; exact edge manifest/proof ZIP; original serialized-key and quote record joins; two distinct actual OS-held Native PID/birth/exit/signaled/EOF/noTimeout receipts; compiler response standard; complete raw stdout equality; all78 planned formal-integration provenance rows.

`python -B <edge>/reproduce.py --output <fresh-user-Temp/Codex/DiffBeacon/version-cpp17-recapture-unique> --reference-zip <fixed-reference-source.zip> --raw60-dir <immutable-raw60> --supplement128-dir <immutable-supplement128> --current-project <repo>/Src/Merge.vcxproj --compiler <existing-Hostx64/x64/cl.exe> --vcvars <existing-vcvars64.bat>`

Reproduction uses a fresh explicit external Temp destination, two new direct compiler/native processes and the independent reader. Timeout fails without automatic process kill/deletion. There is no product/Core/MSVC runtime dependency, canonical edit, Git operation, release version, external service or credential change. Failure conditions were saved before producer implementation.

Retention: minimal edge source/proof/reader evidence stays in this separate KEEP sibling for review/reproduction. Producer scripts and expanded initial/fresh runs remain in this task's external Temp pending separate parent exact cleanup GO. The first preparation stopped on default text decoding, then remaining new files were produced with explicit UTF8; a final reporting-only NameError did not change captured inputs. Existing files were not overwritten. Remove only after parent acceptance/supersession and common Windows trash workflow; refusal is not bypassed.
