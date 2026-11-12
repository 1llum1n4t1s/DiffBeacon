# Folder Replace native failure matrix — written before probes/code

- Exact pinned ReplaceFunc (not RegexReplaceFunc), direct runtime body only; null args and arity !=3 return None without evaluating nodes.
- Argument evaluation is source→from→to before strict from/to type validation. Throw probes at every position preserve prior trace; invalid from still evaluates to, source None still evaluates all args.
- from accepts text or typed shared_ptr<Poco::RegularExpression>. Text is escaped as a literal and empty text yields None. Typed Regex executes its existing compiled options, regardless of outer case sensitivity; empty regex and zero-width regex differ from empty text.
- to accepts only string, including empty string and raw bytes. Numeric/None/array/Regex replacements are rejected after eager evaluation; replacement is not implicitly stringified.
- source None stays None; scalar values use exact getAsString/ToStringValue; source arrays shallow-map, nested source elements stringify. from/to arrays do not broadcast.
- Global substitution uses exact pinned Poco/PCRE2. Fix capture groups/named groups, zero-width, empty source, empty pattern, all matches, anchors, literal metacharacters and Unicode sensitivity.
- Replacement $ tokens ($0..$9, $$, unmatched/unknown groups, trailing $, literal backslash/NUL, byte-invalid replacement) follow pinned substOne, not Python/.NET replacement rules.
- Regex compile error in a lazy typed node is an argument-evaluation exception. Native substitution/UTF8 errors caught by regexSubst become None. Distinguish these from native process fault; isolate every case in a separate process and preserve exit/stdout/stderr/handle/EOF.
- Raw malformed UTF8 from existing Substr oracle, invalid text/pattern/replacement bytes, embedded NUL, valid multibyte and raw non-UTF8 typed Regex options (where supported) are represented as hex, never replacement-decoded.
- Independently verify full source/Git blobs, includes/PCRE sources, compiler/object/EXE hashes, argv, PID/birth/handle/EOF/exit and all typed output/trace bytes in two independent builds/runs before integrating golden/index/manifest/docs.
- No native Regex constructor/parser syntax is evidence of future managed public .flt acceptance. No optimizer/ReplaceWithList/RegexReplaceFunc/product/E2E behavior claims.
